using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using llamactl.Contracts;

namespace llamactl.Agent;

internal sealed class LlamaCppProcessSupervisor(
    InstanceLogBuffer logs,
    ILogger<LlamaCppProcessSupervisor> logger)
{
    private readonly ConcurrentDictionary<Guid, TrackedProcess> processes = new();

    public async Task<IReadOnlyList<ObservedInstance>> ReconcileAsync(
        NodeConfiguration configuration,
        IReadOnlyList<DesiredInstance> desiredInstances,
        CancellationToken cancellationToken)
    {
        var desiredIds = desiredInstances.Select(instance => instance.Id).ToHashSet();
        await StopOrphanedPidProcessesAsync(configuration, desiredIds, cancellationToken);
        foreach (var tracked in processes.Where(pair => !desiredIds.Contains(pair.Key)).ToList())
            await StopAsync(tracked.Key, tracked.Value, configuration, cancellationToken);

        var observed = new List<ObservedInstance>(desiredInstances.Count);
        foreach (var desired in desiredInstances)
            observed.Add(await ReconcileInstanceAsync(configuration, desired, cancellationToken));
        return observed;
    }

    private async Task<ObservedInstance> ReconcileInstanceAsync(
        NodeConfiguration configuration,
        DesiredInstance desired,
        CancellationToken cancellationToken)
    {
        processes.TryGetValue(desired.Id, out var tracked);
        tracked ??= TryRecover(configuration, desired);
        if (tracked is not null
            && (tracked.Process.HasExited || tracked.Revision != desired.Revision
                || desired.State == DesiredInstanceState.Stopped))
        {
            await StopAsync(desired.Id, tracked, configuration, cancellationToken);
            tracked = null;
        }

        if (desired.State == DesiredInstanceState.Stopped)
            return new(desired.Id, desired.Revision, ObservedInstanceState.Stopped, null, null);

        if (tracked is not null)
        {
            processes[desired.Id] = tracked;
            return new(desired.Id, desired.Revision, ObservedInstanceState.Running, tracked.Process.Id, null);
        }

        try
        {
            var process = desired.AdoptProcessId is { } processId
                ? Attach(processId)
                : Start(configuration, desired);
            tracked = new(process, desired.Revision);
            processes[desired.Id] = tracked;
            WritePidFile(configuration, desired.Id, process.Id, desired.Revision);
            return new(desired.Id, desired.Revision, ObservedInstanceState.Running, process.Id, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogError(exception, "Could not reconcile instance {InstanceId}", desired.Id);
            return new(desired.Id, desired.Revision, ObservedInstanceState.Failed, null, exception.Message);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(NodeConfiguration configuration, InstanceSpec spec)
    {
        var executable = Path.Combine(
            configuration.Paths.LlamaBin,
            OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["HF_HOME"] = configuration.Paths.HfHome;
        startInfo.Environment["LLAMA_CACHE"] = configuration.Paths.EmptyCache;

        if (string.Equals(spec.Profile, "router", StringComparison.OrdinalIgnoreCase))
        {
            Add(startInfo, "models-dir", configuration.Paths.FlatDir);
            Add(startInfo, "models-preset", configuration.Paths.PresetFile);
        }
        else if (string.Equals(spec.Profile, "single", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(spec.ModelRef))
                throw new InvalidOperationException("Single-model instances require a model reference.");
            Add(startInfo, "model", spec.ModelRef);
        }
        else
        {
            throw new InvalidOperationException("llama.cpp profile must be 'router' or 'single'.");
        }

        if (spec.Port is { } port)
            Add(startInfo, "port", port.ToString(CultureInfo.InvariantCulture));
        Add(startInfo, "n-gpu-layers", configuration.DefaultGpuLayers.ToString(CultureInfo.InvariantCulture));
        if (configuration.JinjaEnabled)
            Add(startInfo, "jinja", null);
        foreach (var argument in spec.Args.OrderBy(argument => argument.Key, StringComparer.Ordinal))
            Add(startInfo, argument.Key, argument.Value);
        return startInfo;
    }

    private Process Start(NodeConfiguration configuration, DesiredInstance desired)
    {
        var startInfo = BuildStartInfo(configuration, desired.Spec);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => LogOutput(desired.Id, ProcessLogStream.StandardOutput, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => LogOutput(desired.Id, ProcessLogStream.StandardError, eventArgs.Data);
        if (!process.Start())
            throw new InvalidOperationException("Process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void LogOutput(Guid instanceId, ProcessLogStream stream, string? line)
    {
        if (line is not null)
        {
            logs.Write(instanceId, stream, line);
            logger.LogInformation("[{InstanceId}] {ProcessOutput}", instanceId, line);
        }
    }

    private TrackedProcess? TryRecover(NodeConfiguration configuration, DesiredInstance desired)
    {
        var persisted = ReadPidFile(configuration, desired.Id);
        var processId = desired.AdoptProcessId ?? persisted?.ProcessId;
        if (processId is null)
            return null;
        try
        {
            var process = Attach(processId.Value);
            return process.HasExited ? null : new(process, persisted?.Revision ?? desired.Revision);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task StopOrphanedPidProcessesAsync(
        NodeConfiguration configuration,
        IReadOnlySet<Guid> desiredIds,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(configuration.Paths.ConfigRepo, "processes");
        if (!Directory.Exists(directory))
            return;

        foreach (var pidFile in Directory.EnumerateFiles(directory, "*.pid"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(pidFile), out var instanceId)
                || desiredIds.Contains(instanceId))
                continue;

            var persisted = ReadPidFile(configuration, instanceId);
            try
            {
                if (persisted is not null)
                {
                    using var process = Attach(persisted.Value.ProcessId);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                logger.LogDebug(exception, "Ignoring stale PID file {PidFile}", pidFile);
            }
            File.Delete(pidFile);
        }
    }

    private static Process Attach(int processId)
    {
        var process = Process.GetProcessById(processId);
        if (!string.Equals(process.ProcessName, "llama-server", StringComparison.OrdinalIgnoreCase))
        {
            process.Dispose();
            throw new InvalidOperationException($"Process {processId} is not llama-server.");
        }
        return process;
    }

    private async Task StopAsync(
        Guid id,
        TrackedProcess tracked,
        NodeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        processes.TryRemove(id, out _);
        if (!tracked.Process.HasExited)
        {
            tracked.Process.Kill(entireProcessTree: true);
            await tracked.Process.WaitForExitAsync(cancellationToken);
        }
        tracked.Process.Dispose();
        var pidFile = PidFile(configuration, id);
        if (File.Exists(pidFile))
            File.Delete(pidFile);
    }

    private static void WritePidFile(NodeConfiguration configuration, Guid id, int processId, long revision)
    {
        var directory = Path.Combine(configuration.Paths.ConfigRepo, "processes");
        Directory.CreateDirectory(directory);
        File.WriteAllText(PidFile(configuration, id), $"{processId} {revision}");
    }

    private static PersistedProcess? ReadPidFile(NodeConfiguration configuration, Guid id)
    {
        if (!File.Exists(PidFile(configuration, id)))
            return null;
        var values = File.ReadAllText(PidFile(configuration, id)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return values.Length == 2
            && int.TryParse(values[0], CultureInfo.InvariantCulture, out var processId)
            && long.TryParse(values[1], CultureInfo.InvariantCulture, out var revision)
                ? new(processId, revision)
                : null;
    }

    private static string PidFile(NodeConfiguration configuration, Guid id) =>
        Path.Combine(configuration.Paths.ConfigRepo, "processes", $"{id}.pid");

    private static void Add(ProcessStartInfo startInfo, string name, string? value)
    {
        startInfo.ArgumentList.Add($"--{name.TrimStart('-')}");
        if (value is not null)
            startInfo.ArgumentList.Add(value);
    }

    private sealed record TrackedProcess(Process Process, long Revision);
    private readonly record struct PersistedProcess(int ProcessId, long Revision);
}