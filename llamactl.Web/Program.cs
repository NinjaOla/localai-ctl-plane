using llamactl.Web;
using llamactl.Web.Components;
using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddLlamactlWebHandlers();

var databasePath = Path.GetFullPath(
    builder.Configuration["Llamactl:Database:Path"] ?? "llamactl.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

builder.Services.AddDbContextFactory<LlamactlDb>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapLlamactlWebEndpoints();
app.MapGet("/health/live", () => TypedResults.Ok(new { status = "Healthy" }));

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LlamactlDb>();
    await db.Database.MigrateAsync();
}

app.Run();
