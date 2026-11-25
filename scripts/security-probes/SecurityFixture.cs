using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ServerPanel.API.Data;
using ServerPanel.API.Security;

namespace SecurityRegression;

public sealed class SecurityFixture : WebApplicationFactory<global::Program>
{
    public const string SigningKey = "synthetic-test-signing-key-at-least-forty-eight-bytes-only";
    public readonly System.Collections.Concurrent.ConcurrentQueue<(Uri? Uri, string Body)> AssistantRequests = new();

    private sealed class AssistantHandler(SecurityFixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            fixture.AssistantRequests.Enqueue((request.RequestUri, await request.Content!.ReadAsStringAsync(token)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"content":"Resposta sintética do modelo."},"done":true}""")
            };
        }
    }
    public readonly string Root = Directory.CreateTempSubdirectory("arcadenode-security-").FullName;
    public readonly Guid OwnerId = Guid.NewGuid(), OtherId = Guid.NewGuid(), AdminId = Guid.NewGuid();
    public readonly Guid NodeId = Guid.NewGuid(), ServerId = Guid.NewGuid();
    public readonly Guid SecondNodeId = Guid.NewGuid(), SecondServerId = Guid.NewGuid();
    public readonly X509Certificate2 Ca;
    public readonly X509Certificate2 ServerCertificate;
    public readonly X509Certificate2 ClientCertificate;
    public FakeDockerNode FirstNode = null!, SecondNode = null!;
    public readonly DiagnosticLogCollector DiagnosticLogs = new();
    private int _activeRequests;
    private sealed class RequestTracker(SecurityFixture fixture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                Interlocked.Increment(ref fixture._activeRequests);
                try { await proceed(context); }
                finally { Interlocked.Decrement(ref fixture._activeRequests); }
            });
            next(app);
        };
    }

    public SecurityFixture()
    {
        using var key = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=ArcadeNode Synthetic Audit CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Ca = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        ServerCertificate = IssueCertificate("synthetic-node", true);
        ClientCertificate = IssueCertificate("synthetic-client", false);
        File.WriteAllText(Path.Combine(Root, "ca.pem"), Ca.ExportCertificatePem());
        File.WriteAllText(Path.Combine(Root, "client.pem"), ClientCertificate.ExportCertificatePem());
        using var privateKey = ClientCertificate.GetRSAPrivateKey()!;
        File.WriteAllText(Path.Combine(Root, "client.key"), privateKey.ExportPkcs8PrivateKeyPem());
    }

    public X509Certificate2 IssueCertificate(string subject, bool server, bool matchingHostname = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
        if (server && matchingHostname)
        {
            var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
        }
        using var signed = request.Create(Ca, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    public EnrolledDockerNode Enrollment(FakeDockerNode node) => new()
    {
        Endpoint = node.Endpoint, ClientCertificatePath = Path.Combine(Root, "client.pem"),
        ClientKeyPath = Path.Combine(Root, "client.key"), CaCertificatePath = Path.Combine(Root, "ca.pem"),
        GameBindAddress = "192.0.2.10", GameNetwork = "audit-games", AllowedCpuCores = [1, 3],
        CpuCapacityPercent = node == SecondNode ? 1200 : null,
        QuotaVolumeDriver = "synthetic-quota", QuotaEnforcementVerified = true
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../backend/ServerPanel.API")));
        var configPath = Path.Combine(Root, "nodes.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new { Docker = new {
            Nodes = new[] { Enrollment(FirstNode), Enrollment(SecondNode) },
            Images = new[]
            {
                new ApprovedGameImage { Image = "audit/synthetic:secure", Alias = "arcadenode/minecraft:secure", DataPath = "/home/audit" },
                new ApprovedGameImage { Image = "audit/synthetic-zomboid:secure", Alias = "arcadenode/project-zomboid:secure", DataPath = "/home/pzserver" },
                new ApprovedGameImage { Image = "audit/synthetic-reforger:secure", Alias = "arcadenode/arma-reforger:secure", DataPath = "/home/reforger" },
                new ApprovedGameImage { Image = "audit/synthetic-bedrock:secure", Alias = "arcadenode/minecraft-bedrock:secure", DataPath = "/home/bedrock" }
            }
        }}));
        builder.UseEnvironment("Testing").UseSetting("Jwt:Key", SigningKey).UseSetting("NodeConfigurationPath", configPath);
        builder.UseSetting("CartridgesPath", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../cartridges")));
        builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(DiagnosticLogs));
        builder.UseSetting("LocalAssistant:Enabled", "true");
        builder.UseSetting("LocalAssistant:Endpoint", "http://local-assistant.invalid");
        builder.UseSetting("LocalAssistant:Model", "synthetic-local:latest");
        builder.UseSetting("LocalAssistant:MaxOutputTokens", "64");
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient("LocalAssistant").ConfigurePrimaryHttpMessageHandler(() => new AssistantHandler(this));
            services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(
                new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
            services.AddSingleton<IStartupFilter>(new RequestTracker(this));
            services.RemoveAll<DbContextOptions<PanelDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<PanelDbContext>>();
            services.RemoveAll<PanelDbContext>();
            services.AddDbContext<PanelDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(Root, "audit.db")};Default Timeout=10;Pooling=False"));
        });
    }

    public static bool Trusts(X509Certificate2 certificate, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    internal async Task WaitForRequestsAsync()
    {
        // TestServer can deliver a response before post-response audit/scoped disposal ends.
        for (var attempt = 0; Volatile.Read(ref _activeRequests) != 0 && attempt < 100; attempt++) await Task.Delay(50);
        if (Volatile.Read(ref _activeRequests) != 0) throw new InvalidOperationException("Synthetic API requests did not finish.");
    }

    public async Task CloseAsync()
    {
        await WaitForRequestsAsync();
        await base.DisposeAsync();
        await FirstNode.DisposeAsync(); await SecondNode.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Ca.Dispose(); ServerCertificate.Dispose(); ClientCertificate.Dispose();
        var resolved = Path.GetFullPath(Root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("arcadenode-security-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a directory outside the synthetic test root.");
        // SQLite finalizers can retain file handles briefly on Windows after TestHost stops.
        GC.Collect(); GC.WaitForPendingFinalizers();
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(resolved, recursive: true); break; }
            catch (IOException) when (attempt < 20) { await Task.Delay(100); }
        }
    }
}
