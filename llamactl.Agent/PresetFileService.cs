namespace llamactl.Agent;

internal sealed class PresetFileService(NodeRuntimeState runtimeState)
{
    public Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        var path = GetPath();
        return File.Exists(path)
            ? File.ReadAllTextAsync(path, cancellationToken)
            : Task.FromResult(string.Empty);
    }

    public async Task WriteAsync(string content, CancellationToken cancellationToken)
    {
        var path = GetPath();
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Preset path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private string GetPath() => runtimeState.Configuration?.Paths.PresetFile
        ?? throw new InvalidOperationException("Node configuration has not been applied.");
}