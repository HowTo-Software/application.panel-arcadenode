using System.Runtime.Versioning;
using System.Text.Json;
using ArcadeNode.Quota;

[assembly: SupportedOSPlatform("linux")]

if (!OperatingSystem.IsLinux())
    throw new PlatformNotSupportedException("The ArcadeNode quota driver requires Linux.");

var root = Environment.GetEnvironmentVariable("ARCADENODE_QUOTA_ROOT") ?? "/srv/arcadenode-quota";
var socket = Environment.GetEnvironmentVariable("ARCADENODE_QUOTA_SOCKET") ?? "/run/arcadenode-quota/driver.sock";
await using var store = await XfsVolumeStore.OpenAsync(root);
await XfsVolumeStore.RequirePrivateSocketDirectoryAsync(socket);

var builder = WebApplication.CreateSlimBuilder();
builder.Configuration.Sources.Clear();
builder.Host.UseSystemd();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 8 * 1024;
    options.ListenUnixSocket(socket);
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = null);
var app = builder.Build();

app.Use(async (context, next) =>
{
    // All request bodies on this private plugin socket use JSON. Moby can omit their Content-Type.
    if (HttpMethods.IsPost(context.Request.Method) && string.IsNullOrEmpty(context.Request.ContentType))
        context.Request.ContentType = "application/json";

    try { await next(); }
    catch (QuotaException error)
    {
        if (!context.Response.HasStarted)
            await context.Response.WriteAsJsonAsync(new { Err = error.Message });
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or TimeoutException)
    {
        app.Logger.LogError("Quota storage operation failed with {ErrorType}", error.GetType().Name);
        if (!context.Response.HasStarted)
            await context.Response.WriteAsJsonAsync(new { Err = "Quota storage is unavailable." });
    }
});

app.MapPost("/Plugin.Activate", () => new { Implements = new[] { "VolumeDriver" } });
app.MapPost("/VolumeDriver.Capabilities", () => new { Capabilities = new { Scope = "local" } });
app.MapPost("/VolumeDriver.Create", async (VolumeRequest input, CancellationToken token) =>
{
    await store.CreateAsync(input.Name, input.Opts, token);
    return new { Err = "" };
});
app.MapPost("/VolumeDriver.Remove", async (VolumeRequest input, CancellationToken token) =>
{
    await store.RemoveAsync(input.Name, token);
    return new { Err = "" };
});
app.MapPost("/VolumeDriver.Mount", async (VolumeRequest input, CancellationToken token) =>
    new { Mountpoint = await store.GetMountpointAsync(input.Name, token), Err = "" });
app.MapPost("/VolumeDriver.Path", async (VolumeRequest input, CancellationToken token) =>
    new { Mountpoint = await store.GetMountpointAsync(input.Name, token), Err = "" });
// XFS project directories remain mounted on the host. Docker releases each bind mount.
app.MapPost("/VolumeDriver.Unmount", () => new { Err = "" });
app.MapPost("/VolumeDriver.Get", async (VolumeRequest input, CancellationToken token) =>
    new { Volume = await store.GetAsync(input.Name, token), Err = "" });
app.MapPost("/VolumeDriver.List", async (CancellationToken token) =>
    new { Volumes = await store.ListAsync(token), Err = "" });

await app.RunAsync();

internal sealed record VolumeRequest(string? Name, Dictionary<string, string>? Opts);
