using llamactl.Contracts;

namespace llamactl.Agent;

internal sealed class NodeConfigurationApplier
{
    public IReadOnlyList<ValidationIssue> Apply(NodeConfiguration? configuration)
    {
        if (configuration is null)
            return [Error("configuration", "Node configuration has not been provided.")];

        var issues = new List<ValidationIssue>();
        CreateManagedDirectory(configuration.Paths.FlatDir, "paths.flatDir", issues);
        CreateManagedDirectory(configuration.Paths.EmptyCache, "paths.emptyCache", issues);
        CreateManagedDirectory(configuration.Paths.ConfigRepo, "paths.configRepo", issues);
        RequireWritableDirectory(configuration.Paths.ModelsRoot, "paths.modelsRoot", issues);
        RequireWritableDirectory(configuration.Paths.HfHome, "paths.hfHome", issues);
        RequireWritableDirectory(configuration.Paths.FlatDir, "paths.flatDir", issues);
        RequireWritableDirectory(configuration.Paths.EmptyCache, "paths.emptyCache", issues);
        RequireWritableDirectory(configuration.Paths.ConfigRepo, "paths.configRepo", issues);
        RequireDirectory(configuration.Paths.LlamaSource, "paths.llamaSource", issues);
        RequireExecutable(configuration.Paths.LlamaBin, "llama-server", "paths.llamaBin", issues);
        if (!OperatingSystem.IsWindows())
            RequireDirectory(configuration.Paths.Rocm, "paths.rocm", issues);

        if (File.Exists(configuration.Paths.PresetFile))
            issues.Add(new("adoption.preset", "Existing preset file is available for adoption.", ValidationSeverity.Information));
        if (Directory.Exists(configuration.Paths.ModelsRoot)
            && Directory.EnumerateFiles(configuration.Paths.ModelsRoot, "*.gguf", SearchOption.AllDirectories).Any())
            issues.Add(new("adoption.models", "Existing GGUF models were discovered.", ValidationSeverity.Information));

        return issues;
    }

    private static void CreateManagedDirectory(string path, string code, ICollection<ValidationIssue> issues)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(Error(code, $"Could not create or access '{path}': {exception.Message}"));
        }
    }

    private static void RequireDirectory(string path, string code, ICollection<ValidationIssue> issues)
    {
        if (!Directory.Exists(path))
            issues.Add(Error(code, $"Directory '{path}' does not exist."));
    }

    private static void RequireWritableDirectory(
        string path,
        string code,
        ICollection<ValidationIssue> issues)
    {
        if (!Directory.Exists(path))
        {
            issues.Add(Error(code, $"Directory '{path}' does not exist."));
            return;
        }

        var probe = Path.Combine(path, $".llamactl-write-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(Error(code, $"Directory '{path}' is not writable: {exception.Message}"));
        }
    }

    private static void RequireExecutable(
        string directory,
        string name,
        string code,
        ICollection<ValidationIssue> issues)
    {
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? $"{name}.exe" : name);
        if (!File.Exists(executable))
            issues.Add(Error(code, $"Executable '{executable}' was not found."));
    }

    private static ValidationIssue Error(string code, string message) =>
        new(code, message, ValidationSeverity.Error);
}