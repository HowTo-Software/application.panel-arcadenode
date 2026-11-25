using System.ComponentModel.DataAnnotations;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Security;

public sealed class EnrolledDockerNode
{
    public string Endpoint { get; set; } = "";
    public string ClientCertificatePath { get; set; } = "";
    public string ClientKeyPath { get; set; } = "";
    public string CaCertificatePath { get; set; } = "";
    public string GameBindAddress { get; set; } = "";
    public string GameNetwork { get; set; } = "";
    public int[] AllowedCpuCores { get; set; } = [];
    public int? CpuCapacityPercent { get; set; }
    public string QuotaVolumeDriver { get; set; } = "";
    public string QuotaSizeOption { get; set; } = "size";
    public bool QuotaEnforcementVerified { get; set; }
}

public sealed class ApprovedGameImage
{
    public string Image { get; set; } = "";
    public string? Alias { get; set; }
    public string DataPath { get; set; } = "";
}

public sealed class DockerEnrollment
{
    private readonly Dictionary<string, EnrolledDockerNode> _nodes;
    private readonly ApprovedGameImage[] _images;
    public DockerEnrollment(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _nodes = new(StringComparer.Ordinal);
        foreach (var node in configuration.GetSection("Docker:Nodes").Get<EnrolledDockerNode[]>() ?? Array.Empty<EnrolledDockerNode>())
        {
            var key = CanonicalEndpoint(node.Endpoint);
            if (!_nodes.TryAdd(key, node)) throw DiagnosticLog.Mark(new InvalidOperationException("Duplicate enrolled Docker endpoint."), DiagnosticPoints.NodeEnrollment);
            if (string.IsNullOrWhiteSpace(node.ClientCertificatePath) || string.IsNullOrWhiteSpace(node.ClientKeyPath) || string.IsNullOrWhiteSpace(node.CaCertificatePath))
                throw DiagnosticLog.Mark(new InvalidOperationException("Enrolled Docker nodes require client certificate, key, and trusted CA."), DiagnosticPoints.NodeEnrollment);
            if (node.CpuCapacityPercent is < 1 or > 409_600)
                throw DiagnosticLog.Mark(new InvalidOperationException("The node CPU capacity must be between 1 and 409600 percent."), DiagnosticPoints.NodeEnrollment);
        }
        _images = configuration.GetSection("Docker:Images").Get<ApprovedGameImage[]>() ?? Array.Empty<ApprovedGameImage>();
        var identities = _images.SelectMany(image => image.Alias is null ? new[] { image.Image } : new[] { image.Image, image.Alias }).ToArray();
        if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
            throw DiagnosticLog.Mark(new InvalidOperationException("Image digests and aliases must be unique."), DiagnosticPoints.DockerImage);
        foreach (var image in _images)
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing") &&
                !System.Text.RegularExpressions.Regex.IsMatch(image.Image, @"^[a-zA-Z0-9./:_-]+@sha256:[a-f0-9]{64}$"))
                throw DiagnosticLog.Mark(new InvalidOperationException("Production images must be approved by immutable sha256 digest."), DiagnosticPoints.DockerImage);
            if (!System.Text.RegularExpressions.Regex.IsMatch(image.DataPath, @"^/home/[a-z][a-z0-9_-]{0,31}$"))
                throw DiagnosticLog.Mark(new InvalidOperationException("Approved images must use a single quota-controlled home directory."), DiagnosticPoints.DockerImage);
        }
    }

    public static string CanonicalEndpoint(string endpoint) =>
        NormalizeEndpoint(endpoint) ??
        throw DiagnosticLog.Mark(new ValidationException("Docker endpoints must be enrolled HTTPS origins without paths, credentials, queries or fragments."), DiagnosticPoints.NodeEnrollment);

    private static string? NormalizeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Port is < 1 or > 65535)
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public EnrolledDockerNode RequireNode(string? endpoint)
    {
        if (endpoint is null || !_nodes.TryGetValue(CanonicalEndpoint(endpoint), out var node))
            throw DiagnosticLog.Mark(new ValidationException("Docker endpoint is not enrolled by the operator."), DiagnosticPoints.NodeEnrollment);
        return node;
    }

    public int? GetCpuCapacity(string endpoint) =>
        NormalizeEndpoint(endpoint) is { } canonical && _nodes.TryGetValue(canonical, out var node)
            ? node.CpuCapacityPercent : null;

    public ApprovedGameImage? FindImage(string image) => _images.SingleOrDefault(item => item.Image == image || item.Alias == image);

    public ApprovedGameImage RequireImage(string image) => FindImage(image)
        ?? throw DiagnosticLog.Mark(new ValidationException("Docker image is not approved by the operator."), DiagnosticPoints.DockerImage);

    public void RequireProvisioning(EnrolledDockerNode node)
    {
        if (!node.QuotaEnforcementVerified || string.IsNullOrWhiteSpace(node.QuotaVolumeDriver) || node.QuotaVolumeDriver == "local" ||
            !System.Text.RegularExpressions.Regex.IsMatch(node.QuotaSizeOption, @"^[a-zA-Z][a-zA-Z0-9_-]{0,63}$") ||
            !System.Net.IPAddress.TryParse(node.GameBindAddress, out _) ||
            !System.Text.RegularExpressions.Regex.IsMatch(node.GameNetwork, @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}$") ||
            node.GameNetwork is "host" or "bridge" or "none")
            throw DiagnosticLog.Mark(new InvalidOperationException("Provisioning requires a verified quota-capable data-volume driver, explicit game network and bind address."), DiagnosticPoints.DockerVolume);
    }
}
