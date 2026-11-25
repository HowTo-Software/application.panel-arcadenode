using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ServerPanel.API.Models;

namespace ServerPanel.API.Services;

public sealed class EggRuntimeProfiles(IConfiguration configuration, ICartridgeService cartridges)
{
    public Cartridge? ResolveCartridge(string? digest)
    {
        if (digest is not { Length: 64 } || !digest.All(char.IsAsciiHexDigit)) return null;
        var id = configuration[$"EggRuntimes:{digest.ToUpperInvariant()}:CartridgeId"];
        return string.IsNullOrWhiteSpace(id) ? null : cartridges.GetCartridgeById(id);
    }

    internal Runtime? Resolve(string digest, string? variablesJson)
    {
        var cartridge = ResolveCartridge(digest);
        if (cartridge is null) return null;
        var section = configuration.GetSection($"EggRuntimes:{digest.ToUpperInvariant()}");
        var mappings = section.GetSection("VariableMap").GetChildren().ToArray();
        var fixedValues = section.GetSection("FixedValues").GetChildren().ToArray();
        var destinations = new Dictionary<string, Variable>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, CartridgeVariable>(StringComparer.Ordinal);
        if (mappings.Length + fixedValues.Length > 100) throw InvalidProfile();

        foreach (var target in cartridge.Variables)
        {
            AddAlias(target.EnvVariable, target);
            AddAlias(target.Id, target);
        }

        var sourceVariables = mappings.Length == 0 || variablesJson is null ? [] : JsonSerializer.Deserialize<EggVariable[]>(variablesJson) ?? [];
        foreach (var binding in mappings)
        {
            var source = sourceVariables.SingleOrDefault(variable => variable.EnvVariable == binding.Key);
            if (source is null || binding.Value is not { } name || binding.GetChildren().Any()) throw InvalidProfile();
            var target = FindEditableTarget(name);
            AddAlias(source.EnvVariable, target);
            var visible = source.UserViewable && target.UserViewable;
            var editable = visible && source.UserEditable;
            var value = NormalizeValue(target, source.DefaultValue);
            if (!destinations.TryAdd(target.EnvVariable, new Variable(target, value, visible, editable,
                source.Name, source.Description, source.EnvVariable))) throw InvalidProfile();
        }

        foreach (var entry in fixedValues)
        {
            var target = FindEditableTarget(entry.Key);
            if (entry.Value is not { } value || entry.GetChildren().Any()) throw InvalidProfile();
            var visible = target.UserViewable && target.UI?.Component != "password";
            if (!destinations.TryAdd(target.EnvVariable, new Variable(target, NormalizeValue(target, value), visible,
                false, target.Name, target.Description))) throw InvalidProfile();
        }
        return new Runtime(cartridge, destinations, aliases);

        CartridgeVariable FindEditableTarget(string name) => aliases.TryGetValue(name, out var target) &&
            target.EnvVariable == name && target.UserEditable ? target : throw InvalidProfile();

        void AddAlias(string name, CartridgeVariable target)
        {
            if (aliases.TryGetValue(name, out var existing) && existing != target) throw InvalidProfile();
            aliases[name] = target;
        }
    }

    private static ValidationException InvalidProfile() => new("The approved egg runtime has an invalid variable mapping or fixed value.");

    private static string NormalizeValue(CartridgeVariable target, string value)
    {
        if (value.Length > 4096 || value.Any(char.IsControl)) throw InvalidProfile();
        return (target.Type, value) switch
        {
            ("boolean", "1") => "true",
            ("boolean", "0") => "false",
            _ => value
        };
    }

    internal sealed class Runtime(Cartridge cartridge, Dictionary<string, Variable> destinations,
        Dictionary<string, CartridgeVariable> aliases)
    {
        public Cartridge Cartridge { get; } = cartridge;

        public IEnumerable<RuntimeVariable> DescribeVariables() => destinations.Values.Select(rule => new RuntimeVariable(
            rule.Target.EnvVariable, rule.Visible ? rule.Name : null, rule.Visible ? rule.Description : null,
            rule.Visible ? rule.Default : null, rule.Visible, rule.Editable));

        public Dictionary<string, object> ResolveValues(Dictionary<string, object>? requested)
        {
            if (destinations.Count == 0) return requested ?? [];
            if (requested is { Count: > 100 }) throw new ValidationException("Too many egg variables.");
            var values = DefaultValues();
            if (requested is null) return values;

            var supplied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in requested)
            {
                if (!aliases.TryGetValue(key, out var target) || !supplied.Add(target.EnvVariable))
                    throw new ValidationException("Unknown or repeated egg variable.");
                if (!destinations.TryGetValue(target.EnvVariable, out var rule))
                {
                    values.Add(target.EnvVariable, value);
                    continue;
                }
                if (!rule.Editable) throw new ValidationException("This egg variable is managed by the approved profile.");
                var text = value switch
                {
                    string literal => literal,
                    JsonElement { ValueKind: JsonValueKind.String } json => json.GetString()!,
                    JsonElement { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } json => json.GetRawText(),
                    _ => throw new ValidationException("Provide a scalar value for the egg variable.")
                };
                values[target.EnvVariable] = NormalizeValue(target, text);
            }
            return values;
        }

        public Dictionary<string, object> ResolveMigratedValues(Dictionary<string, string?> exported, Dictionary<string, object>? overrides)
        {
            var bindings = destinations.Values.Where(rule => rule.SourceVariable is not null)
                .ToDictionary(rule => rule.SourceVariable!, StringComparer.Ordinal);
            if (exported.Count != bindings.Count || overrides is { Count: > 100 }) throw InvalidProfile();

            var values = DefaultValues();
            foreach (var (source, saved) in exported)
            {
                if (saved is null || !bindings.TryGetValue(source, out var rule)) throw InvalidProfile();
                values[rule.Target.EnvVariable] = NormalizeValue(rule.Target, saved);
            }
            if (overrides is null) return values;

            foreach (var (name, value) in overrides)
            {
                if (!aliases.TryGetValue(name, out var target) || !values.TryAdd(target.EnvVariable, value)) throw InvalidProfile();
            }
            return values;
        }

        private Dictionary<string, object> DefaultValues() =>
            destinations.ToDictionary(pair => pair.Key, pair => (object)pair.Value.Default, StringComparer.Ordinal);
    }

    internal sealed record Variable(CartridgeVariable Target, string Default, bool Visible, bool Editable, string Name, string Description,
        string? SourceVariable = null);
    internal sealed record RuntimeVariable(string EnvVariable, string? Name, string? Description, string? DefaultValue, bool UserViewable, bool UserEditable);
}
