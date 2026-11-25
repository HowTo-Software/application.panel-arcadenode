using System.ComponentModel.DataAnnotations;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Security;

public static class SecurityLimits
{
    public const int PageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxLogLines = 1000;
    public const int MaxLogBytes = 262144;
    public const int MaxAllocations = 100;
    public const int MaxSecondaryPorts = 16;
    public const int MaxGamePathLength = 1024;

    public static bool ValidGamePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= MaxGamePathLength && !path.Contains('\\') &&
        !path.Any(char.IsControl) && path.Split('/').All(part => part is not ("" or "." or ".."));

    public static string? CpuSet(IReadOnlyCollection<int>? cores, int cpuLimit, EnrolledDockerNode node)
    {
        if (cores is null or { Count: 0 }) return null;
        if (cores.Count > 64 || cores.Distinct().Count() != cores.Count ||
            cores.Any(core => core is < 0 or > 4095 || !node.AllowedCpuCores.Contains(core)) ||
            cpuLimit > cores.Count * 100)
            throw new ValidationException("CPU affinity must use unique enrolled logical cores and fit the requested CPU quota.");

        return string.Join(',', cores.Order().Select(core => core.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public static bool ValidSecondaryPorts(int primary, IReadOnlyCollection<int> ports) =>
        ports.Count <= MaxSecondaryPorts && ports.All(port => port is >= 1024 and <= 65535 && port != primary) &&
        ports.Distinct().Count() == ports.Count;

    public static void Resources(int memory, int cpu, int disk, int port)
    {
        if (memory is < 128 or > 1048576 || cpu is < 1 or > 6400 ||
            disk is < 256 or > 10485760 || port is < 1024 or > 65535)
            throw DiagnosticLog.Mark(new ValidationException("Invalid resource limits or port. Ports must be between 1024 and 65535."), DiagnosticPoints.ServerAdmission);
    }

    public static void Text(string? name, string? description)
    {
        if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 100) || description?.Length > 500)
            throw new ValidationException("Invalid server name or description.");
    }

    public static void EnvironmentVariables(Dictionary<string, string>? values)
    {
        if (values is null) return;
        if (values.Count > 100 || values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 100 || pair.Value is null || pair.Value.Length > 4096 ||
            pair.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || pair.Value.Any(c => c is '\0' or '\r' or '\n')))
            throw new ValidationException("Invalid environment variables.");
    }

}
