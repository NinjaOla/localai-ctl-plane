using llamactl.Agent;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AgentBootstrapOptions>()
    .BindConfiguration(AgentBootstrapOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(options => options.NodeId != Guid.Empty, "NodeId must not be empty.")
    .ValidateOnStart();
builder.Services.AddSingleton<SystemNodeDiscovery>();
builder.Services.AddSingleton<NodeConfigurationApplier>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InstanceLogBuffer>();
builder.Services.AddSingleton<NodeRuntimeState>();
builder.Services.AddSingleton<PresetFileService>();
builder.Services.AddSingleton<ModelFileService>();
builder.Services.AddHttpClient(nameof(ModelDownloadManager), client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("llamactl", "1.0"));
});
builder.Services.AddSingleton<ModelDownloadManager>();
builder.Services.AddSingleton<LlamaCppProcessSupervisor>();
builder.Services.AddSingleton<DesiredStateReconciler>();
builder.Services.AddHostedService<ControlPlaneConnection>();

var app = builder.Build();

app.MapGet("/health/live", () => TypedResults.Ok(new { status = "Healthy" }));

app.Run();
