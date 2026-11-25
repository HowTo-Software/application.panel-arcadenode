using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerPanel.API.Models;

namespace ServerPanel.API.Services;

internal static class PterodactylEggParser
{
    public static Egg Parse(JsonElement input, Guid nestId)
    {
        var manifest = input.GetRawText();
        using var document = ParseDocument(manifest);
        var root = document.RootElement;
        var version = Text(Object(root, "meta"), "version");
        if (version is not ("PTDL_v1" or "PTDL_v2"))
            throw new ValidationException("Expected a PTDL_v1 or PTDL_v2 egg export.");

        var images = ReadImages(root, version);
        var config = Object(root, "config");
        var installation = Object(Object(root, "scripts"), "installation");
        return new Egg
        {
            NestId = nestId,
            Name = RequiredText(root, "name", 100),
            Description = Text(root, "description", 4000) ?? "",
            DockerImage = images.Values.First(),
            DockerImages = JsonSerializer.Serialize(images),
            StartupCommand = RequiredText(root, "startup"),
            ConfigFiles = JsonConfig(config, "files"),
            ConfigStartup = JsonConfig(config, "startup"),
            ConfigStop = Text(config, "stop"),
            InstallScript = Text(installation, "script"),
            InstallContainer = Text(installation, "container"),
            InstallEntrypoint = Text(installation, "entrypoint"),
            Variables = JsonSerializer.Serialize(ReadVariables(root)),
            PterodactylManifest = manifest,
            PterodactylSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)))
        };
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            throw new ValidationException("Egg JSON is invalid or contains duplicate properties.");
        }
    }

    private static Dictionary<string, string> ReadImages(JsonElement root, string version)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        if (version == "PTDL_v2")
        {
            var source = Object(root, "docker_images");
            if (source.ValueKind == JsonValueKind.Object)
            {
                foreach (var image in source.EnumerateObject())
                    images.Add(image.Name, Image(image.Value));
            }
        }
        else if (Property(root, "images") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } legacy)
        {
            var values = legacy.ValueKind switch
            {
                JsonValueKind.Array => legacy.EnumerateArray().ToArray(),
                JsonValueKind.Object => legacy.EnumerateObject().Select(image => image.Value).ToArray(),
                _ => throw Invalid("images")
            };
            foreach (var value in values)
            {
                var image = Image(value);
                images.TryAdd(image, image);
            }
        }
        else
        {
            var image = Image(Property(root, "image"));
            images.Add(image, image);
        }

        if (images.Count == 0) throw Invalid("docker_images");
        return images;
    }

    private static string Image(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 512 } image ||
            image.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw Invalid("Docker image");
        return image;
    }

    private static List<EggVariable> ReadVariables(JsonElement root)
    {
        var variables = Property(root, "variables");
        if (variables.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [];
        if (variables.ValueKind != JsonValueKind.Array) throw Invalid("variables");

        var result = new List<EggVariable>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in variables.EnumerateArray())
        {
            var name = RequiredText(variable, "env_variable", 128);
            if (!char.IsAsciiLetter(name[0]) && name[0] != '_' ||
                name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || !names.Add(name))
                throw Invalid("env_variable");

            var defaultValue = Property(variable, "default_value");
            result.Add(new EggVariable
            {
                Name = RequiredText(variable, "name"),
                EnvVariable = name,
                Description = Text(variable, "description") ?? "",
                DefaultValue = defaultValue.ValueKind switch
                {
                    JsonValueKind.Undefined or JsonValueKind.Null => "",
                    JsonValueKind.String => defaultValue.GetString()!,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => defaultValue.GetRawText(),
                    _ => throw Invalid("default_value")
                },
                UserViewable = Flag(variable, "user_viewable"),
                UserEditable = Flag(variable, "user_editable"),
                Rules = Text(variable, "rules")
            });
        }
        return result;
    }

    private static bool Flag(JsonElement source, string field) => Property(source, field) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined } => false,
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt32(out var number) && number is 0 or 1 => number == 1,
        _ => throw Invalid(field)
    };

    private static string? JsonConfig(JsonElement source, string field)
    {
        var value = Property(source, field);
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return value.GetRawText();
        var json = Text(source, field);
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var parsed = ParseDocument(json);
        if (parsed.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null))
            throw Invalid(field);
        return json;
    }

    private static JsonElement Object(JsonElement source, string field) => Property(source, field) switch
    {
        { ValueKind: JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null } value => value,
        _ => throw Invalid(field)
    };

    private static JsonElement Property(JsonElement source, string field) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(field, out var value) ? value : default;

    private static string? Text(JsonElement source, string field, int limit = 65536) => Property(source, field) switch
    {
        { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } value when value.GetString() is { } text && text.Length <= limit => text,
        _ => throw Invalid(field)
    };

    private static string RequiredText(JsonElement source, string field, int limit = 65536)
    {
        var value = Text(source, field, limit);
        return string.IsNullOrWhiteSpace(value) ? throw Invalid(field) : value;
    }

    private static ValidationException Invalid(string field) => new($"Invalid or missing egg field: {field}.");
}
