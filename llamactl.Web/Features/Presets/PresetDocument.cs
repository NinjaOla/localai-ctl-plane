namespace llamactl.Web.Features.Presets;

internal sealed record PresetSection(string Name, IReadOnlyDictionary<string, string> Values);

internal sealed record PresetDocument(IReadOnlyList<PresetSection> Sections)
{
    public static PresetDocument Parse(string content)
    {
        var sections = new List<PresetSection>();
        string? currentName = null;
        Dictionary<string, string>? currentValues = null;
        foreach (var (line, index) in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((line, index) => (line.Trim(), index)))
        {
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (currentName is not null)
                    sections.Add(new(currentName, currentValues!));
                currentName = line[1..^1].Trim();
                if (currentName.Length == 0)
                    throw new FormatException($"Section name is empty at line {index + 1}.");
                currentValues = new(StringComparer.Ordinal);
                continue;
            }
            if (currentValues is null)
                throw new FormatException($"Value appears before a section at line {index + 1}.");
            var separator = line.IndexOf('=');
            if (separator <= 0)
                throw new FormatException($"Expected key=value at line {index + 1}.");
            var key = line[..separator].Trim().TrimStart('-');
            if (!currentValues.TryAdd(key, line[(separator + 1)..].Trim()))
                throw new FormatException($"Duplicate key '{key}' in section '{currentName}'.");
        }
        if (currentName is not null)
            sections.Add(new(currentName, currentValues!));
        return new(sections);
    }

    public IReadOnlyList<string> Validate(IReadOnlySet<string> flags)
    {
        var errors = new List<string>();
        foreach (var section in Sections)
            foreach (var key in section.Values.Keys)
                if (!flags.Contains(key))
                    errors.Add($"[{section.Name}] option '{key}' is not supported by this node's llama.cpp build.");
        return errors;
    }
}

internal static class PresetDiff
{
    public static string Create(string original, string updated)
    {
        var before = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var after = updated.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lines = new List<string>();
        var count = Math.Max(before.Length, after.Length);
        for (var index = 0; index < count; index++)
        {
            var oldLine = index < before.Length ? before[index] : null;
            var newLine = index < after.Length ? after[index] : null;
            if (oldLine == newLine)
                lines.Add($"  {oldLine}");
            else
            {
                if (oldLine is not null) lines.Add($"- {oldLine}");
                if (newLine is not null) lines.Add($"+ {newLine}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }
}