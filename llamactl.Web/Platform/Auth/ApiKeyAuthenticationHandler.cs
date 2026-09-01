using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace llamactl.Web.Platform.Auth;

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<SecurityOptions> securityOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string Header = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var supplied = Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(supplied))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (!FixedTimeEquals(supplied, securityOptions.Value.ApiKey))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "api")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    internal static bool FixedTimeEquals(string supplied, string expected)
    {
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}