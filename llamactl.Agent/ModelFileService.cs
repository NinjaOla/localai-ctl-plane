using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using llamactl.Contracts;

namespace llamactl.Agent;

internal sealed partial class ModelFileService(NodeRuntimeState runtimeState, TimeProvider timeProvider)
{
    public Task<ModelInventory> ScanAsync(CancellationToken cancellationToken)
    {
        var configuration = GetConfiguration();
        var flat = Path.GetFullPath(configuration.Paths.FlatDir);
        var roots = new[]
        {
            (Path: configuration.Paths.ModelsRoot, FromHfCache: false),
            (Path: configuration.Paths.HfHome, FromHfCache: true),
        }.DistinctBy(root => Path.GetFullPath(root.Path), StringComparer.OrdinalIgnoreCase);
        var files = roots.SelectMany(root => Directory.Exists(root.Path)
                ? Directory.EnumerateFiles(root.Path, "*.gguf", SearchOption.AllDirectories)
                    .Select(path => (root.Path, root.FromHfCache, File: path))
                : [])
            .Where(item => !Path.GetFullPath(item.File).StartsWith(flat + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(item => !item.File.Contains($"{Path.DirectorySeparatorChar}blobs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(item => ToModelFile(item.Path, item.File, item.FromHfCache))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
        var orphans = FindOrphanedBlobs(configuration.Paths.HfHome);
        var drive = new DriveInfo(Path.GetPathRoot(configuration.Paths.ModelsRoot)!);
        return Task.FromResult(new ModelInventory(timeProvider.GetUtcNow(), drive.AvailableFreeSpace, files, orphans));
    }

    public async Task<ReconcileLibraryResult> ReconcileAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var configuration = GetConfiguration();
        var inventory = await ScanAsync(cancellationToken);
        var operations = PlanLibrary(configuration, inventory.Files);
        if (!dryRun)
            Apply(operations);
        return new(!dryRun, operations);
    }

    public async Task<GgufInspection> InspectAsync(InspectGgufRequest request, CancellationToken cancellationToken)
    {
        var configuration = GetConfiguration();
        var root = request.FromHfCache ? configuration.Paths.HfHome : configuration.Paths.ModelsRoot;
        var path = ResolveInside(root, request.RelativePath);
        await using var stream = File.OpenRead(path);
        return await GgufReader.ReadAsync(request.RelativePath, stream, cancellationToken);
    }

    public Task<DeleteModelResult> DeleteAsync(DeleteModelRequest request, CancellationToken cancellationToken)
    {
        var configuration = GetConfiguration();
        var root = request.FromHfCache ? configuration.Paths.HfHome : configuration.Paths.ModelsRoot;
        long freed = 0;
        foreach (var relativePath in request.RelativePaths.Distinct(StringComparer.Ordinal))
        {
            var path = ResolveInside(root, relativePath);
            if (!File.Exists(path))
                continue;
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            freed += target is null ? new FileInfo(path).Length : new FileInfo(target.FullName).Length;
            RemoveFlatLinksTo(configuration.Paths.FlatDir, path);
            File.Delete(path);
            if (target is not null && IsInside(configuration.Paths.HfHome, target.FullName) && File.Exists(target.FullName))
            {
                File.Delete(target.FullName);
            }
        }
        return Task.FromResult(new DeleteModelResult(freed));
    }

    internal static IReadOnlyList<LibraryOperation> PlanLibrary(NodeConfiguration configuration, IReadOnlyList<ModelFile> files)
    {
        var operations = new List<LibraryOperation>();
        var flat = configuration.Paths.FlatDir;
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usable = files.Where(file => !file.IsDraftHead).ToList();
        var projectors = usable.Where(file => file.IsMmproj).ToList();
        foreach (var group in usable.Where(file => !file.IsMmproj).GroupBy(ModelGroup, StringComparer.Ordinal))
        {
            var members = group.Concat(projectors.Where(projector => ModelFamily(projector) == ModelFamily(group.First()))).ToList();
            var needsDirectory = members.Count > 1 || members.Any(file => file.IsShard || file.IsMmproj);
            if (needsDirectory)
            {
                var directory = Path.Combine(flat, Sanitize(group.Key));
                desired.Add(directory);
                operations.Add(new(LibraryOperationKind.CreateDirectory, directory, null));
                operations.AddRange(members.Select(file =>
                {
                    var link = Path.Combine(directory, Path.GetFileName(file.RelativePath));
                    desired.Add(link);
                    return new LibraryOperation(LibraryOperationKind.CreateLink, link, Path.Combine(file.FromHfCache ? configuration.Paths.HfHome : configuration.Paths.ModelsRoot, file.RelativePath));
                }));
            }
            else
            {
                var file = members[0];
                var link = Path.Combine(flat, Path.GetFileName(file.RelativePath));
                desired.Add(link);
                operations.Add(new(LibraryOperationKind.CreateLink, link,
                    Path.Combine(file.FromHfCache ? configuration.Paths.HfHome : configuration.Paths.ModelsRoot, file.RelativePath)));
            }
        }
        if (Directory.Exists(flat))
        {
            var existing = Directory.EnumerateFileSystemEntries(flat, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length);
            operations.InsertRange(0, existing.Where(path => !desired.Contains(path))
                .Select(path => new LibraryOperation(LibraryOperationKind.Remove, path, null)));
        }
        return operations;
    }

    private static void Apply(IReadOnlyList<LibraryOperation> operations)
    {
        var roots = operations.Select(operation => Path.GetDirectoryName(operation.Path)!).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots) Directory.CreateDirectory(root);
        foreach (var operation in operations)
        {
            if (operation.Kind == LibraryOperationKind.CreateDirectory) Directory.CreateDirectory(operation.Path);
            else if (operation.Kind == LibraryOperationKind.Remove && File.Exists(operation.Path)) File.Delete(operation.Path);
            else if (operation.Kind == LibraryOperationKind.Remove && Directory.Exists(operation.Path)) Directory.Delete(operation.Path, recursive: true);
            else if (operation.Kind == LibraryOperationKind.CreateLink && operation.Target is not null)
            {
                if (File.Exists(operation.Path)) File.Delete(operation.Path);
                File.CreateSymbolicLink(operation.Path, operation.Target);
            }
        }
    }

    private static ModelFile ToModelFile(string root, string path, bool fromHfCache)
    {
        var name = Path.GetFileName(path);
        return new(Path.GetRelativePath(root, path), new FileInfo(path).Length, ShardPattern().IsMatch(name),
            name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase), IsDraft(name), fromHfCache);
    }

    private static IReadOnlyList<OrphanedBlob> FindOrphanedBlobs(string hfHome)
    {
        var hub = Path.Combine(hfHome, "hub");
        if (!Directory.Exists(hub)) return [];
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in Directory.EnumerateFiles(hub, "*", SearchOption.AllDirectories).Where(path => path.Contains($"{Path.DirectorySeparatorChar}snapshots{Path.DirectorySeparatorChar}")))
        {
            var target = new FileInfo(snapshot).ResolveLinkTarget(true);
            if (target is not null) referenced.Add(Path.GetFullPath(target.FullName));
        }
        return Directory.EnumerateFiles(hub, "*", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}blobs{Path.DirectorySeparatorChar}") && !referenced.Contains(Path.GetFullPath(path)))
            .Select(path => new OrphanedBlob(Path.GetRelativePath(hfHome, path), new FileInfo(path).Length)).ToList();
    }

    private NodeConfiguration GetConfiguration() => runtimeState.Configuration ?? throw new InvalidOperationException("Node configuration has not been applied.");
    private static string ResolveInside(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path escapes the configured model root.");
        return path;
    }
    private static bool IsInside(string root, string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RemoveFlatLinksTo(string flatRoot, string targetPath)
    {
        if (!Directory.Exists(flatRoot)) return;
        var targetInfo = new FileInfo(targetPath);
        var finalTarget = targetInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? targetPath;
        foreach (var link in Directory.EnumerateFiles(flatRoot, "*", SearchOption.AllDirectories))
        {
            var target = new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null && string.Equals(Path.GetFullPath(target.FullName), Path.GetFullPath(finalTarget), StringComparison.OrdinalIgnoreCase)) File.Delete(link);
        }
    }
    internal static string ModelGroup(ModelFile file)
    {
        return Path.GetFileNameWithoutExtension(ShardPattern().Replace(Path.GetFileName(file.RelativePath), string.Empty));
    }
    internal static string ModelFamily(ModelFile file) => QuantSuffix().Replace(ModelGroup(file).Replace("mmproj-", string.Empty, StringComparison.OrdinalIgnoreCase), string.Empty);
    private static bool IsDraft(string name) => name.Contains("dflash", StringComparison.OrdinalIgnoreCase) || name.Contains("mtp", StringComparison.OrdinalIgnoreCase) || name.Contains("nextn", StringComparison.OrdinalIgnoreCase);
    private static string Sanitize(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    [GeneratedRegex(@"-\d{5}-of-\d{5}(?=\.gguf$)", RegexOptions.IgnoreCase)] private static partial Regex ShardPattern();
    [GeneratedRegex(@"[-_.](?:IQ|Q|BF|F)\d+(?:_[A-Z0-9]+)*$", RegexOptions.IgnoreCase)] private static partial Regex QuantSuffix();
}

internal static class GgufReader
{
    public static async Task<GgufInspection> ReadAsync(string relativePath, Stream stream, CancellationToken cancellationToken)
    {
        var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadUInt32();
        if (magic != 0x46554747) throw new InvalidDataException("File is not GGUF.");
        _ = reader.ReadUInt32();
        var tensorCount = checked((int)reader.ReadUInt64());
        var metadataCount = checked((int)reader.ReadUInt64());
        string? architecture = null;
        long? context = null;
        for (var index = 0; index < metadataCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ReadString(reader);
            var type = reader.ReadUInt32();
            if (key == "general.architecture" && type == 8) architecture = ReadString(reader);
            else if ((key.EndsWith(".context_length", StringComparison.Ordinal) || key == "n_ctx_train") && type is 4 or 10) context = type == 4 ? reader.ReadUInt32() : checked((long)reader.ReadUInt64());
            else SkipValue(reader, type);
        }
        var drafts = new List<string>();
        for (var index = 0; index < tensorCount; index++)
        {
            var name = ReadString(reader);
            if (name.Contains("nextn", StringComparison.OrdinalIgnoreCase) || name.Contains("mtp", StringComparison.OrdinalIgnoreCase)) drafts.Add(name);
            var dimensions = reader.ReadUInt32();
            stream.Seek(dimensions * sizeof(ulong) + sizeof(uint) + sizeof(ulong), SeekOrigin.Current);
        }
        await Task.CompletedTask;
        return new(relativePath, architecture, context, tensorCount, drafts.Count > 0, drafts);
    }
    private static string ReadString(BinaryReader reader) => Encoding.UTF8.GetString(reader.ReadBytes(checked((int)reader.ReadUInt64())));
    private static void SkipValue(BinaryReader reader, uint type)
    {
        var sizes = new Dictionary<uint, int> { [0] = 1, [1] = 1, [2] = 2, [3] = 2, [4] = 4, [5] = 4, [6] = 4, [7] = 1, [10] = 8, [11] = 8, [12] = 8 };
        if (type == 8) { _ = ReadString(reader); return; }
        if (type == 9) { var element = reader.ReadUInt32(); var count = reader.ReadUInt64(); for (ulong i = 0; i < count; i++) SkipValue(reader, element); return; }
        if (!sizes.TryGetValue(type, out var size)) throw new InvalidDataException($"Unsupported GGUF metadata type {type}.");
        reader.BaseStream.Seek(size, SeekOrigin.Current);
    }
}