using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ServerPanel.API.Services;

internal sealed class PterodactylServerMappings(Dictionary<long, Guid> nodes,
    Dictionary<(long EggId, string Image), PterodactylServerMappings.Profile> profiles)
{
    internal const int MaximumBytes = 256 * 1024;

    public Guid TargetNode(long sourceNodeId) => nodes.GetValueOrDefault(sourceNodeId);

    public Profile? TargetProfile(long sourceEggId, string? sourceImage) =>
        sourceImage is null ? null : profiles.GetValueOrDefault((sourceEggId, sourceImage));

    public static async Task<PterodactylServerMappings> ReadAsync(Stream source, JsonSerializerOptions options, CancellationToken token)
    {
        var plan = await JsonSerializer.DeserializeAsync<Plan>(source, options, token);
        if (plan is not { SchemaVersion: 1, Nodes.Length: <= 1024, Profiles.Length: > 0 and <= 512 }) throw InvalidPlan();

        var nodes = new Dictionary<long, Guid>();
        foreach (var item in plan.Nodes)
        {
            if (item is not { SourceNodeId: > 0 } || item.TargetNodeId == Guid.Empty || !nodes.TryAdd(item.SourceNodeId, item.TargetNodeId))
                throw InvalidPlan();
        }

        var profiles = new Dictionary<(long, string), Profile>();
        foreach (var item in plan.Profiles)
        {
            if (item is not { SourceEggId: > 0, SourceImage.Length: > 0 and <= 512, SourceVariables.Length: <= 100 } ||
                item.TargetEggId == Guid.Empty || item.SourceImage.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw InvalidPlan();
            var variables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in item.SourceVariables)
            {
                if (name is not { Length: > 0 and <= 128 } || (!char.IsAsciiLetter(name[0]) && name[0] != '_') ||
                    name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || !variables.Add(name)) throw InvalidPlan();
            }
            if (!profiles.TryAdd((item.SourceEggId, item.SourceImage), new Profile(item.TargetEggId, variables))) throw InvalidPlan();
        }
        return new PterodactylServerMappings(nodes, profiles);
    }

    private static ValidationException InvalidPlan() => new("Use a version 1 migration mapping with unique nodes and explicit egg, image and variable selections.");

    internal sealed record Profile(Guid TargetEggId, HashSet<string> SourceVariables);
    private sealed record Plan(int SchemaVersion, NodeMapping?[] Nodes, EggMapping?[] Profiles);
    private sealed record NodeMapping(long SourceNodeId, Guid TargetNodeId);
    private sealed record EggMapping(long SourceEggId, string SourceImage, Guid TargetEggId, string[] SourceVariables);
}
