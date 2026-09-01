using System.Text.RegularExpressions;
using llamactl.Contracts;

namespace llamactl.Web.Features.Models;

internal static partial class ModelFitCalculator
{
    public static IReadOnlyList<ModelFitEstimate> Estimate(IReadOnlyList<HuggingFaceFile> files, long vramMiB, long diskFreeBytes)
    {
        return files.Where(file => !IsSidecar(file.Path)).GroupBy(Variant, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var size = group.Sum(file => file.SizeBytes);
                var verdict = size > diskFreeBytes ? FitVerdict.ExceedsDisk : size > vramMiB * 1024 * 1024 ? FitVerdict.ExceedsVram : FitVerdict.Fits;
                return new ModelFitEstimate(group.Key, size, verdict, null, "KV cache varies by architecture and context; model file size is not total runtime VRAM.");
            }).OrderBy(item => item.SizeBytes).ToList();
    }
    internal static string Variant(HuggingFaceFile file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Path);
        name = ShardSuffix().Replace(name, string.Empty);
        var match = Quant().Match(name);
        return match.Success ? match.Value : name;
    }
    private static bool IsSidecar(string path) => Path.GetFileName(path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase) || path.Contains("dflash", StringComparison.OrdinalIgnoreCase) || path.Contains("mtp", StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex(@"-\d{5}-of-\d{5}$", RegexOptions.IgnoreCase)] private static partial Regex ShardSuffix();
    [GeneratedRegex(@"(?:IQ|Q|BF|F)\d+(?:_[A-Z0-9]+)*", RegexOptions.IgnoreCase)] private static partial Regex Quant();
}