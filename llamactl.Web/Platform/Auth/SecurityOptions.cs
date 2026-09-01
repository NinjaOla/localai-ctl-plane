using System.ComponentModel.DataAnnotations;

namespace llamactl.Web.Platform.Auth;

public sealed class SecurityOptions
{
    public const string SectionName = "Llamactl:Security";

    [Required, MinLength(12)]
    public required string OperatorPassword { get; init; }

    [Required, MinLength(16)]
    public required string ApiKey { get; init; }
}