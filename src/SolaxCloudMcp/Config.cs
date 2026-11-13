using System.Globalization;

namespace SolaxCloudMcp;

/// <summary>Raised when an environment variable is missing or has a bad value.</summary>
public class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Configuration from environment variables.</summary>
public static class Config
{
    /// <summary>An empty value counts as not set (docker-compose passes "" for unset variables).</summary>
    public static string? Env(string name, string? fallback = null) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    static bool Flag(string name) => (Env(name) ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes";

    public static double Double(string name, string? fallback = null) => Parse(name, fallback, s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture));

    public static int Int(string name, string? fallback = null) => Parse(name, fallback, s => int.Parse(s.Trim(), CultureInfo.InvariantCulture));

    static T Parse<T>(string name, string? fallback, Func<string, T> parse)
    {
        var raw = Env(name, fallback) ?? throw new ConfigException($"{name} is not set");
        try
        {
            return parse(raw);
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new ConfigException($"{name} has an invalid value '{raw}'", e);
        }
    }

    public static string ClientId() => Env("SOLAX_CLIENT_ID") ?? throw new ConfigException(
        "SOLAX_CLIENT_ID environment variable not set. " +
        "Register an OAuth2 application at https://developer.solaxcloud.com/ " +
        "and obtain your Client ID from the Application page.");

    public static string ClientSecret() => Env("SOLAX_CLIENT_SECRET") ?? throw new ConfigException(
        "SOLAX_CLIENT_SECRET environment variable not set. " +
        "Register an OAuth2 application at https://developer.solaxcloud.com/ " +
        "and obtain your Client Secret from the Application page.");

    /// <summary>The inverter serial number, NOT the WiFi dongle registration number of the legacy API.</summary>
    public static string? DefaultDeviceSn() => Env("SOLAX_DEVICE_SN");

    /// <summary>Whether the MCP server exposes tools that change inverter settings. Off by default, so an
    /// LLM (e.g. via prompt injection) cannot reconfigure the inverter unless the user opted in.</summary>
    public static bool IsWriteEnabled() => Flag("SOLAX_ALLOW_WRITE");

    public static bool IsSolcastConfigured() => Env("SOLCAST_API_KEY") is not null && SolcastResourceIds().Count > 0;

    public static string SolcastApiKey() => Env("SOLCAST_API_KEY") ?? throw new ConfigException(
        "SOLCAST_API_KEY environment variable not set. " +
        "Create a (free hobbyist) account at https://toolkit.solcast.com.au/ and copy the API key.");

    /// <summary>Solcast rooftop site IDs (comma-separated, e.g. east and west arrays).</summary>
    public static List<string> SolcastResourceIds() =>
        (Env("SOLCAST_RESOURCE_IDS") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>How long a Solcast response is reused (default 180 min). Hobbyist accounts allow ~10 calls/day.</summary>
    public static int SolcastCacheMinutes() => Int("SOLCAST_CACHE_MINUTES", "180");

    public static string? SolcastCacheFile() => Env("SOLCAST_CACHE_FILE");

    public static string? DecisionsFile() => Env("DECISIONS_FILE");

    public static string? SettingsFile() => Env("SETTINGS_FILE");

    public static string SolarTimezone() => Env("SOLAR_TIMEZONE", "UTC")!;

    public static bool IsAutomationEnabled() => Flag("AUTOMATION_ENABLED");

    public static bool IsExportEnabled() => Flag("EXPORT_ENABLED");
}
