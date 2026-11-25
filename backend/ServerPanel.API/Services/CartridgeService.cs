using System.Text.Json;
using System.Text.RegularExpressions;
using ServerPanel.API.Models;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Services;

public interface ICartridgeService
{
    /// <summary>
    /// Get all available cartridges
    /// </summary>
    IEnumerable<Cartridge> GetAllCartridges();

    /// <summary>
    /// Get a cartridge by its ID
    /// </summary>
    Cartridge? GetCartridgeById(string id);

    /// <summary>
    /// Get cartridges by category
    /// </summary>
    IEnumerable<Cartridge> GetCartridgesByCategory(string category);

    /// <summary>
    /// Reload cartridges from disk
    /// </summary>
    void ReloadCartridges();

    /// <summary>
    /// Process variable substitution in a string
    /// </summary>
    string SubstituteVariables(string template, Dictionary<string, string> variables);

    /// <summary>
    /// Build environment variables for a server from cartridge and user values
    /// </summary>
    Dictionary<string, string> BuildEnvironmentVariables(Cartridge cartridge, Dictionary<string, object> userValues, int port,
        IReadOnlyDictionary<string, int>? namedPorts = null, bool forRestore = false);

    /// <summary>
    /// Validate user-provided variable values against cartridge definitions
    /// </summary>
    (bool IsValid, List<string> Errors) ValidateVariables(Cartridge cartridge, Dictionary<string, object> values, bool forRestore = false);

    /// <summary>
    /// Get the startup command with variables substituted
    /// </summary>
    string GetStartupCommand(Cartridge cartridge, Dictionary<string, string> environment);
}

public class CartridgeService : ICartridgeService
{
    private readonly ILogger<CartridgeService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, Cartridge> _cartridges = new();
    private readonly JsonSerializerOptions _jsonOptions;

    public CartridgeService(ILogger<CartridgeService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        LoadCartridges();
    }

    private void LoadCartridges()
    {
        _cartridges.Clear();

        // Look for cartridges directory relative to the app
        var cartridgesPath = _configuration["CartridgesPath"] ?? GetDefaultCartridgesPath();
        
        _logger.LogDebug("Loading cartridges from the configured directory");

        if (!Directory.Exists(cartridgesPath))
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.CartridgeDirectory);
            return;
        }

        // Each subdirectory is a cartridge
        foreach (var dir in Directory.GetDirectories(cartridgesPath))
        {
            var cartridgeFile = Path.Combine(dir, "cartridge.json");
            if (!File.Exists(cartridgeFile))
            {
                _logger.LogDebug("Skipping directory without cartridge.json");
                continue;
            }

            try
            {
                var json = File.ReadAllText(cartridgeFile);
                var cartridge = JsonSerializer.Deserialize<Cartridge>(json, _jsonOptions);

                if (cartridge != null && !string.IsNullOrEmpty(cartridge.Id))
                {
                    _cartridges[cartridge.Id] = cartridge;
                    _logger.LogDebug("Cartridge loaded");
                }
                else DiagnosticLog.Write(_logger, DiagnosticPoints.CartridgeLoad);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(_logger, DiagnosticPoints.CartridgeLoad, ex);
            }
        }

        _logger.LogInformation("Loaded {Count} cartridges", _cartridges.Count);
        if (_cartridges.Count == 0) DiagnosticLog.Write(_logger, DiagnosticPoints.CartridgeLoad, level: LogLevel.Warning);
    }

    private string GetDefaultCartridgesPath()
    {
        // Try multiple locations
        var locations = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "cartridges"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "cartridges"),
            "/app/cartridges",
            "./cartridges"
        };

        foreach (var location in locations)
        {
            var fullPath = Path.GetFullPath(location);
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "cartridges");
    }

    public IEnumerable<Cartridge> GetAllCartridges()
    {
        return _cartridges.Values.OrderBy(c => c.Category).ThenBy(c => c.Name);
    }

    public Cartridge? GetCartridgeById(string id)
    {
        return _cartridges.TryGetValue(id, out var cartridge) ? cartridge : null;
    }

    public IEnumerable<Cartridge> GetCartridgesByCategory(string category)
    {
        return _cartridges.Values
            .Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name);
    }

    public void ReloadCartridges()
    {
        _logger.LogInformation("Reloading cartridges...");
        LoadCartridges();
    }

    public string SubstituteVariables(string template, Dictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        // Match {{VARIABLE}} or {{VARIABLE|filter}} or {{VARIABLE|filter:arg}}
        var pattern = @"\{\{(\w+)(?:\|(\w+)(?::([^}]+))?)?\}\}";

        return Regex.Replace(template, pattern, match =>
        {
            var varName = match.Groups[1].Value;
            var filter = match.Groups[2].Success ? match.Groups[2].Value : null;
            var filterArg = match.Groups[3].Success ? match.Groups[3].Value : null;

            // Try to get the value
            if (!variables.TryGetValue(varName, out var value))
            {
                // Return original if not found
                return match.Value;
            }

            // Apply filters
            if (filter != null)
            {
                value = ApplyFilter(value, filter, filterArg);
            }

            return value ?? string.Empty;
        });
    }

    private string ApplyFilter(string value, string filter, string? arg)
    {
        return filter.ToLower() switch
        {
            "default" => string.IsNullOrEmpty(value) ? (arg ?? string.Empty) : value,
            "sanitize" => SanitizeForFilename(value),
            "upper" => value.ToUpperInvariant(),
            "lower" => value.ToLowerInvariant(),
            "base64" => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)),
            "trim" => value.Trim(),
            "quote" => $"\"{value}\"",
            _ => value
        };
    }

    private string SanitizeForFilename(string value)
    {
        // Replace spaces with underscores and remove invalid chars
        var sanitized = value.Replace(' ', '_');
        sanitized = Regex.Replace(sanitized, @"[^a-zA-Z0-9_-]", "");
        return sanitized;
    }

    public Dictionary<string, string> BuildEnvironmentVariables(Cartridge cartridge, Dictionary<string, object> userValues, int port,
        IReadOnlyDictionary<string, int>? namedPorts = null, bool forRestore = false)
    {
        var env = cartridge.SystemVariables is { } system ? new Dictionary<string, string>(system) : new();
        var templates = env.Keys.ToHashSet(StringComparer.Ordinal);
        void SetLiteral(string name, string value)
        {
            env[name] = value;
            templates.Remove(name);
        }

        // Add port variables
        var portIndex = 0;
        var defaultPrimaryPort = cartridge.Ports.Values.FirstOrDefault()?.Default;
        foreach (var (portName, portDef) in cartridge.Ports)
        {
            var envName = $"{portName.ToUpperInvariant()}_PORT";
            var portValue = namedPorts?.TryGetValue(portName, out var configured) == true ? configured :
                forRestore && portDef.UseDefaultOnRestore ? portDef.Default :
                portIndex == 0 || portDef.Default == defaultPrimaryPort ? port : port + portIndex;
            SetLiteral(envName, portValue.ToString());
            portIndex++;
        }

        // Add convenience port variable
        SetLiteral("GAME_PORT", port.ToString());
        SetLiteral("SERVER_PORT", port.ToString());

        // Add built-in variables
        SetLiteral("SERVER_MEMORY", "4096"); // Will be overridden by user values
        SetLiteral("TZ", "UTC");

        // Process cartridge variables with defaults
        foreach (var variable in cartridge.Variables)
        {
            var envName = variable.EnvVariable;
            if (forRestore && variable.FreshInstallOnly)
            {
                env.Remove(envName);
                templates.Remove(envName);
                continue;
            }
            
            // Check if user provided a value (try by Id first, then by EnvVariable since frontend sends values keyed by EnvVariable)
            if ((userValues.TryGetValue(variable.Id, out var userValue) || userValues.TryGetValue(variable.EnvVariable, out userValue)) && userValue != null)
            {
                SetLiteral(envName, ConvertValueToString(userValue, variable.Type));
            }
            else if (variable.Default != null)
            {
                SetLiteral(envName, ConvertValueToString(variable.Default, variable.Type));
            }
        }

        // Explicit migration bindings are authoritative over cartridge defaults and user-defined variables.
        if (namedPorts is not null)
            foreach (var (name, mappedPort) in namedPorts)
                SetLiteral(name.ToUpperInvariant() + "_PORT", mappedPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Expand only templates from the trusted cartridge, keeping imported and supplied values literal.
        foreach (var key in templates)
        {
            env[key] = SubstituteVariables(env[key], env);
        }

        return env;
    }

    private string ConvertValueToString(object value, string type)
    {
        return type.ToLower() switch
        {
            "boolean" => value switch
            {
                bool b => b.ToString().ToLower(),
                string s => s.ToLower(),
                _ => value.ToString()?.ToLower() ?? "false"
            },
            "integer" => value switch
            {
                int i => i.ToString(),
                long l => l.ToString(),
                double d => ((int)d).ToString(),
                JsonElement je when je.ValueKind == JsonValueKind.Number => je.ToString(),
                _ => value.ToString() ?? "0"
            },
            _ => value switch
            {
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? string.Empty,
                _ => value.ToString() ?? string.Empty
            }
        };
    }

    public (bool IsValid, List<string> Errors) ValidateVariables(Cartridge cartridge, Dictionary<string, object> values, bool forRestore = false)
    {
        var errors = new List<string>();
        if (values.Count > 100)
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.CartridgeVariables);
            return (false, new List<string> { "Too many cartridge variables." });
        }
        var allowed = cartridge.Variables.SelectMany(variable => new[] { variable.Id, variable.EnvVariable }).ToHashSet(StringComparer.Ordinal);
        if (values.Keys.Any(key => !allowed.Contains(key)))
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.CartridgeVariables);
            return (false, new List<string> { "Unknown cartridge variable." });
        }

        foreach (var variable in cartridge.Variables)
        {
            if (forRestore && variable.FreshInstallOnly) continue;
            // Try to get value by Id first, then by EnvVariable (frontend sends values keyed by EnvVariable)
            var hasValue = (values.TryGetValue(variable.Id, out var value) || values.TryGetValue(variable.EnvVariable, out value)) && value != null;
            if (!hasValue && variable.Default is not null) { value = variable.Default; hasValue = true; }
            var stringValue = hasValue ? ConvertValueToString(value!, variable.Type) : null;

            if (hasValue && !variable.UserEditable && stringValue != (variable.Default is null ? null : ConvertValueToString(variable.Default, variable.Type)))
            {
                errors.Add($"{variable.Name} is not editable");
                continue;
            }
            if (stringValue?.Length > 4096 || stringValue?.Any(c => c is '\0' or '\r' or '\n') == true)
            {
                errors.Add($"{variable.Name} is too long or contains control characters");
                continue;
            }
            if (hasValue && variable.Type == "integer" && !int.TryParse(stringValue, out _))
                errors.Add($"{variable.Name} must be an integer");
            if (hasValue && variable.Type == "boolean" && stringValue is not "true" and not "false")
                errors.Add($"{variable.Name} must be a boolean");
            // Check required
            if (variable.Required && (!hasValue || string.IsNullOrEmpty(stringValue)))
            {
                errors.Add($"{variable.Name} is required");
                continue;
            }

            // Skip validation if no value and not required
            if (!hasValue || string.IsNullOrEmpty(stringValue))
                continue;

            // Validate based on type and rules
            if (variable.Validation != null)
            {
                var validation = variable.Validation;

                // String length validation
                if (validation.MinLength.HasValue && stringValue.Length < validation.MinLength.Value)
                {
                    errors.Add($"{variable.Name} must be at least {validation.MinLength} characters");
                }

                if (validation.MaxLength.HasValue && stringValue.Length > validation.MaxLength.Value)
                {
                    errors.Add($"{variable.Name} must be at most {validation.MaxLength} characters");
                }

                // Numeric range validation
                if (variable.Type == "integer" && int.TryParse(stringValue, out var intValue))
                {
                    if (validation.Min.HasValue && intValue < validation.Min.Value)
                    {
                        errors.Add($"{variable.Name} must be at least {validation.Min}");
                    }

                    if (validation.Max.HasValue && intValue > validation.Max.Value)
                    {
                        errors.Add($"{variable.Name} must be at most {validation.Max}");
                    }
                }

                // Pattern validation
                if (!string.IsNullOrEmpty(validation.Pattern))
                {
                    try
                    {
                        if (!Regex.IsMatch(stringValue, validation.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                        {
                            errors.Add($"{variable.Name} has an invalid format");
                        }
                    }
                    catch (Exception ex) when (ex is RegexParseException or RegexMatchTimeoutException)
                    {
                        DiagnosticLog.Write(_logger, DiagnosticPoints.CartridgeVariables, ex);
                        errors.Add($"{variable.Name} validation could not be completed");
                    }
                }
            }

            // Validate select options
            if (variable.UI?.Options != null && variable.UI.Options.Count > 0)
            {
                var validValues = variable.UI.Options.Select(o => o.Value).ToList();
                if (!validValues.Contains(stringValue))
                {
                    errors.Add($"{variable.Name} must be one of: {string.Join(", ", validValues)}");
                }
            }
        }

        if (errors.Count > 0) DiagnosticLog.Write(_logger, DiagnosticPoints.CartridgeVariables);
        return (errors.Count == 0, errors);
    }

    public string GetStartupCommand(Cartridge cartridge, Dictionary<string, string> environment)
    {
        return SubstituteVariables(cartridge.Startup.Command, environment);
    }
}
