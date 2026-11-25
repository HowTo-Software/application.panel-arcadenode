using Docker.DotNet;

namespace ServerPanel.API.Services;

public interface IDockerClientFactory
{
    /// <summary>
    /// Returns the cached DockerClient for an explicitly enrolled endpoint.
    /// </summary>
    DockerClient GetClient(string? endpoint = null);

    Task WriteConsoleInputAsync(string endpoint, string containerId, ReadOnlyMemory<byte> input, CancellationToken token);
}
