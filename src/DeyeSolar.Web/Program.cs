using DeyeSolar.Web.Operations;

if (args.Contains("--health-check", StringComparer.Ordinal))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try { using var result = await client.GetAsync("http://127.0.0.1:8080/health/ready"); Environment.ExitCode = result.IsSuccessStatusCode ? 0 : 1; }
    catch { Environment.ExitCode = 1; }
    return;
}

var migrateOnly = args.Contains("--migrate-only", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--migrate-only").ToArray());
var deployment = DeploymentConfiguration.Capture(builder, migrateOnly);
builder.AddSolarApplication(deployment);
var app = builder.Build();
using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(deployment.StartupTimeoutSeconds)))
    await app.Services.GetRequiredService<IApplicationDatabaseInitializer>().InitializeAsync(startup.Token);
if (migrateOnly) { await app.DisposeAsync(); return; }
app.UseSolarApplication(deployment);
await app.RunAsync();

public partial class Program;
