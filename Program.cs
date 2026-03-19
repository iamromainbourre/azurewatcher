using AzureWatcher;
using AzureWatcher.Components;
using AzureWatcher.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// CredentialService is Singleton: the MSAL token survives page reloads
builder.Services.AddSingleton<CredentialService>();
builder.Services.AddSingleton<AzureDiscoveryService>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton<AzureDevOpsService>();
builder.Services.AddSingleton<WatcherService>();

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

app.Run();
