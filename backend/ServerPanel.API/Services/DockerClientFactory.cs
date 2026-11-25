using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Security.Authentication;
using Docker.DotNet;
using ServerPanel.API.Security;
using ServerPanel.API.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServerPanel.API.Services;

public sealed class DockerClientFactory(DockerEnrollment enrollment, ILogger<DockerClientFactory>? logger = null) : IDockerClientFactory, IDisposable
{
    private sealed class EnrolledCredentials(SocketsHttpHandler transport) : Credentials
    {
        public override bool IsTlsCredentials() => true;

        public override HttpMessageHandler GetHandler(HttpMessageHandler innerHandler)
        {
            innerHandler.Dispose();
            return transport;
        }
    }

    private sealed record ClientResources(DockerClient Client, HttpClient Console, X509Certificate2 Certificate, X509Certificate2 Ca) : IDisposable
    {
        public void Dispose()
        {
            Console.Dispose();
            Client.Dispose();
            Certificate.Dispose();
            Ca.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, Lazy<ClientResources>> _clients = new(StringComparer.Ordinal);

    public DockerClient GetClient(string? endpoint = null) => GetEnrolledClient(endpoint).Client;

    public async Task WriteConsoleInputAsync(string endpoint, string containerId, ReadOnlyMemory<byte> input, CancellationToken token)
    {
        var console = GetEnrolledClient(endpoint).Console;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"containers/{Uri.EscapeDataString(containerId)}/attach?stream=1&stdin=1")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Connection.Add("Upgrade");
        request.Headers.Upgrade.Add(new ProductHeaderValue("tcp"));

        // Docker upgrades this HTTP/1.1 request to a raw, writable connection.
        using var response = await console.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.SwitchingProtocols ||
            !response.Headers.Upgrade.Any(protocol => string.Equals(protocol.Name, "tcp", StringComparison.OrdinalIgnoreCase)))
            throw new HttpRequestException("Docker did not upgrade the console input connection.", null, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        await stream.WriteAsync(input, token);
    }

    private ClientResources GetEnrolledClient(string? endpoint) =>
        DiagnosticLog.Run(logger ?? NullLogger<DockerClientFactory>.Instance, DiagnosticPoints.NodeConnection, () =>
        {
            var node = enrollment.RequireNode(endpoint);
            var key = DockerEnrollment.CanonicalEndpoint(node.Endpoint);
            return _clients.GetOrAdd(key, _ => new Lazy<ClientResources>(() => CreateClients(node, new Uri(key)),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        });

    private static ClientResources CreateClients(EnrolledDockerNode node, Uri endpoint)
    {
        using var pem = X509Certificate2.CreateFromPemFile(node.ClientCertificatePath, node.ClientKeyPath);
        // Windows Schannel requires a key container; re-import without EphemeralKeySet.
        var pkcs12 = pem.Export(X509ContentType.Pkcs12);
        X509Certificate2 certificate;
        try { certificate = X509CertificateLoader.LoadPkcs12(pkcs12, null); }
        finally { CryptographicOperations.ZeroMemory(pkcs12); }

        X509Certificate2? ca = null;
        SocketsHttpHandler? transport = null;
        DockerClient? client = null;
        try
        {
            ca = X509CertificateLoader.LoadCertificateFromFile(node.CaCertificatePath);
            transport = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                // Bound pooled connection attempts even if their original request has been cancelled.
                ConnectTimeout = TimeSpan.FromSeconds(4),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = new X509CertificateCollection { certificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateChainPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        CustomTrustStore = { ca },
                        ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") },
                        DisableCertificateDownloads = true,
                        // Private short-lived CA certificates are rotated by the operator.
                        RevocationMode = X509RevocationMode.NoCheck
                    }
                }
            };
            var credentials = new EnrolledCredentials(transport);
            client = new DockerClientConfiguration(endpoint, credentials, TimeSpan.FromSeconds(30)).CreateClient();
            var console = new HttpClient(transport, disposeHandler: false) { BaseAddress = endpoint };
            return new ClientResources(client, console, certificate, ca);
        }
        catch
        {
            if (client is null) transport?.Dispose();
            else client.Dispose();
            ca?.Dispose();
            certificate.Dispose();
            throw;
        }
    }

    public void Dispose() { foreach (var client in _clients.Values) if (client.IsValueCreated) client.Value.Dispose(); _clients.Clear(); }
}
