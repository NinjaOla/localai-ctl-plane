using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using llamactl.Contracts;

namespace llamactl.Agent;

internal sealed class SystemNodeDiscovery(ILogger<SystemNodeDiscovery> logger)
{
    public async Task<AgentAnnouncement> DiscoverAsync(CancellationToken cancellationToken)
    {
        var fileSystems = DiscoverFileSystems();
        var llamaServer = FindExecutable("llama-server");
        var rocminfo = FindExecutable(
            "rocminfo",
            Environment.GetEnvironmentVariable("ROCM_PATH") is { Length: > 0 } rocmPath
                ? [Path.Combine(rocmPath, "bin")]
                : ["/opt/rocm/bin"]);
        var llamaVersion = llamaServer is null
            ? null
            : FirstNonEmptyLine(await RunAsync(llamaServer, ["--version"], cancellationToken));
        var llamaHelp = llamaServer is null
            ? null
            : await RunAsync(llamaServer, ["--help"], cancellationToken);
        var rocmInfo = rocminfo is null
            ? null
            : await RunAsync(rocminfo, [], cancellationToken);
        var (gpuName, vramTotalMiB) = ParseRocmInfo(rocmInfo);
        var flagSchema = ParseFlagSchema(llamaHelp);
        var runtime = CreateRuntimeDescriptor(llamaServer, llamaVersion, flagSchema);
        var description = new NodeDescription(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            await GetKernelVersionAsync(cancellationToken),
            gpuName,
            vramTotalMiB,
            GetRocmVersion(rocminfo),
            llamaVersion,
            fileSystems);

        return new AgentAnnouncement(
            description,
            ProposePaths(llamaServer, rocminfo, fileSystems),
            [runtime]);
    }

    internal static (string? GpuName, long? VramTotalMiB) ParseRocmInfo(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (null, null);

        var agentSections = SplitAgentSections(output);
        var gpuSection = agentSections.FirstOrDefault(section =>
            section.Any(line => line.Contains("KERNEL_DISPATCH", StringComparison.OrdinalIgnoreCase)))
            ?? agentSections.Last();
        string? gpuName = null;
        long? vramTotalMiB = null;
        foreach (var line in gpuSection)
        {
            if (gpuName is null && ValueAfterMarker(line, "Marketing Name:") is { Length: > 0 } name)
                gpuName = name;

            if (vramTotalMiB is null
                && ValueAfterMarker(line, "Global Memory Size:") is { Length: > 0 } memory
                && long.TryParse(memory.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
            {
                vramTotalMiB = bytes / (1024 * 1024);
            }
        }

        return (gpuName, vramTotalMiB);
    }

    private static IReadOnlyList<IReadOnlyList<string>> SplitAgentSections(string output)
    {
        var sections = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Agent ", StringComparison.OrdinalIgnoreCase) && current.Count > 0)
            {
                sections.Add(current);
                current = [];
            }

            current.Add(line);
        }

        if (current.Count > 0)
            sections.Add(current);

        return sections;
    }

    internal static IReadOnlyDictionary<string, string> ParseFlagSchema(string? help)
    {
        if (string.IsNullOrWhiteSpace(help))
            return new Dictionary<string, string>();

        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in help.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = line.IndexOf("--", StringComparison.Ordinal);
            if (marker < 0)
                continue;

            var remainder = line[(marker + 2)..];
            var end = remainder.IndexOfAny([' ', '=', ',']);
            var name = (end < 0 ? remainder : remainder[..end]).Trim();
            if (name.Length > 0)
                flags.TryAdd(name, line);
        }

        return flags;
    }

    private static RuntimeDescriptor CreateRuntimeDescriptor(
        string? llamaServer,
        string? version,
        IReadOnlyDictionary<string, string> flagSchema)
    {
        var capabilities = RuntimeCapabilities.None;
        if (flagSchema.ContainsKey("models-dir"))
        {
            capabilities |= RuntimeCapabilities.MultiModelRouting
                | RuntimeCapabilities.OnDemandLoad
                | RuntimeCapabilities.PerModelConfig
                | RuntimeCapabilities.SelfManagedModels;
        }
        if (flagSchema.ContainsKey("draft-model") || flagSchema.ContainsKey("spec-type"))
            capabilities |= RuntimeCapabilities.SpeculativeDecode;
        if (flagSchema.ContainsKey("mmproj"))
            capabilities |= RuntimeCapabilities.Multimodal;

        var binDirectory = llamaServer is null ? null : Path.GetDirectoryName(llamaServer);
        if (binDirectory is not null && ExecutableExists(binDirectory, "llama-bench"))
            capabilities |= RuntimeCapabilities.NativeBenchmark;

        return new RuntimeDescriptor(
            RuntimeId.LlamaCpp,
            "llama.cpp",
            version,
            binDirectory,
            llamaServer is not null,
            ConfigFormat.LlamaCppIni,
            new HashSet<ModelFormat> { ModelFormat.Gguf },
            capabilities,
            flagSchema);
    }

    private IReadOnlyList<MountedFileSystem> DiscoverFileSystems()
    {
        var fileSystems = new List<MountedFileSystem>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                var writable = !drive.RootDirectory.Attributes.HasFlag(FileAttributes.ReadOnly)
                    && drive.DriveType != DriveType.CDRom;
                fileSystems.Add(new(
                    drive.RootDirectory.FullName,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    writable));
            }
            catch (IOException exception)
            {
                logger.LogDebug(exception, "Could not inspect filesystem {Drive}", drive.Name);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogDebug(exception, "Could not inspect filesystem {Drive}", drive.Name);
            }
        }

        return fileSystems;
    }

    private static IReadOnlyList<PathProposal> ProposePaths(
        string? llamaServer,
        string? rocminfo,
        IReadOnlyList<MountedFileSystem> fileSystems)
    {
        var proposals = new List<PathProposal>();
        if (Path.GetDirectoryName(llamaServer) is { } llamaBin)
            proposals.Add(new("llamaBin", llamaBin, "llama-server was found here."));
        if (Path.GetDirectoryName(Path.GetDirectoryName(rocminfo)) is { } rocmRoot)
            proposals.Add(new("rocm", rocmRoot, "rocminfo was found under this ROCm installation."));
        if (Environment.GetEnvironmentVariable("HF_HOME") is { Length: > 0 } hfHome)
            proposals.Add(new("hfHome", hfHome, "HF_HOME is set to this path."));

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var modelsVolume = fileSystems
            .Where(fileSystem => fileSystem.Writable
                && !string.Equals(fileSystem.MountPoint, systemRoot, StringComparison.OrdinalIgnoreCase)
                && fileSystem.MountPoint != "/")
            .MaxBy(fileSystem => fileSystem.FreeBytes);
        if (modelsVolume is not null)
            proposals.Add(new("modelsRoot", modelsVolume.MountPoint, "This is the writable non-system volume with the most free space."));

        return proposals;
    }

    private static string? FindExecutable(string name, IReadOnlyList<string>? additionalDirectories = null)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Concat(additionalDirectories ?? []);

        return directories
            .Select(directory => ExecutablePath(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static bool ExecutableExists(string directory, string name) =>
        File.Exists(ExecutablePath(directory, name));

    private static string ExecutablePath(string directory, string name) =>
        Path.Combine(directory, OperatingSystem.IsWindows() ? $"{name}.exe" : name);

    private static async Task<string?> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return string.Join(Environment.NewLine, await standardOutput, await standardError);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> GetKernelVersionAsync(CancellationToken cancellationToken)
    {
        const string kernelRelease = "/proc/sys/kernel/osrelease";
        return File.Exists(kernelRelease)
            ? (await File.ReadAllTextAsync(kernelRelease, cancellationToken)).Trim()
            : Environment.OSVersion.VersionString;
    }

    private static string? GetRocmVersion(string? rocminfo)
    {
        var rocmRoot = Path.GetDirectoryName(Path.GetDirectoryName(rocminfo));
        var versionFile = rocmRoot is null ? null : Path.Combine(rocmRoot, ".info", "version");
        return versionFile is not null && File.Exists(versionFile)
            ? File.ReadAllText(versionFile).Trim()
            : null;
    }

    private static string? FirstNonEmptyLine(string? value) => value?
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault();

    private static string? ValueAfterMarker(string line, string marker)
    {
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : line[(index + marker.Length)..].Trim();
    }
}