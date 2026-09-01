using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace llamactl.Web.Platform.Auth;

internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", (Delegate)LoginAsync).AllowAnonymous();
        app.MapPost("/auth/logout", (Delegate)LogoutAsync).RequireAuthorization();
    }

    private static async Task<RedirectHttpResult> LoginAsync(
        HttpContext context,
        IOptions<SecurityOptions> options,
        CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (!ApiKeyAuthenticationHandler.FixedTimeEquals(form["password"].ToString(), options.Value.OperatorPassword))
            return TypedResults.Redirect("/login?failed=true");

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
        return TypedResults.Redirect("/nodes");
    }

    private static async Task<RedirectHttpResult> LogoutAsync(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.Redirect("/login");
    }
}