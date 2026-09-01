using llamactl.Web;
using llamactl.Web.Components;
using llamactl.Contracts;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using llamactl.Web.Platform.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using llamactl.Web.Platform.Results;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using llamactl.Web.Features.Models;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddLlamactlWebHandlers();
builder.Services.AddOptions<SecurityOptions>()
    .BindConfiguration(SecurityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "Llamactl";
        options.DefaultChallengeScheme = "Llamactl";
    })
    .AddPolicyScheme("Llamactl", null, options => options.ForwardDefaultSelector = context =>
        context.Request.Path.StartsWithSegments("/api")
            ? ApiKeyAuthenticationHandler.SchemeName
            : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.LoginPath = "/login")
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .Build());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddExceptionHandler<FallbackExceptionHandler>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"]);
builder.Services.AddSignalR();
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddScoped<AgentConnectionAuthenticator>();
builder.Services.AddScoped<AgentAnnouncementReceiver>();
builder.Services.AddScoped<AgentHeartbeatReceiver>();
builder.Services.AddScoped<DesiredStateStore>();
builder.Services.AddScoped<AgentLogReceiver>();
builder.Services.AddSingleton<InstanceLogStore>();
builder.Services.AddSingleton<IAgentPresetGateway, AgentPresetGateway>();
builder.Services.AddSingleton<IAgentModelGateway, AgentModelGateway>();
builder.Services.AddSingleton<DownloadProgressStore>();
builder.Services.AddHttpClient<HuggingFaceClient>(client =>
{
    client.BaseAddress = new Uri("https://huggingface.co/");
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("llamactl", "1.0"));
});
builder.Services.AddSingleton<IDesiredStateNotifier, DesiredStateNotifier>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<NodeHealthMonitor>();

var databasePath = Path.GetFullPath(
    builder.Configuration["Llamactl:Database:Path"] ?? "llamactl.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

builder.Services.AddDbContextFactory<LlamactlDb>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), api =>
    api.Use(async (context, next) =>
    {
        var result = await context.AuthenticateAsync(ApiKeyAuthenticationHandler.SchemeName);
        if (!result.Succeeded)
        {
            await context.ChallengeAsync(ApiKeyAuthenticationHandler.SchemeName);
            return;
        }
        context.User = result.Principal!;
        await next(context);
    }));
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AllowAnonymous();
app.MapLlamactlWebEndpoints();
app.MapHub<AgentHub>(Protocol.AgentHubPath).AllowAnonymous();
app.MapGet("/health/live", () => TypedResults.Ok(new { status = "Healthy" })).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
}).AllowAnonymous();
app.MapAuthEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LlamactlDb>();
    await db.Database.MigrateAsync();
}

app.Run();
