using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// Messages received from the embedded FPB.JS WebView (parsed from JSON).
/// Mirrors the shapes posted by fpbjs-assets/index.html.
/// </summary>
public class JsToHostMessage
{
    [JsonPropertyName("type")]   public string Type { get; set; } = "";
    [JsonPropertyName("message")]public string? Message { get; set; }
    [JsonPropertyName("id")]     public string? Id { get; set; }
    [JsonPropertyName("url")]    public string? Url { get; set; }
    [JsonPropertyName("theme")]  public string? Theme { get; set; }
    [JsonPropertyName("level")]  public string? Level { get; set; }

    /// <summary>Raw payload for "changed" messages (FPB.JS toJSON output).</summary>
    [JsonPropertyName("data")]   public JsonElement Data { get; set; }
}

/// <summary>Type tags posted by JS — kept as constants to avoid stringly-typed comparisons in the host.</summary>
public static class JsMessageType
{
    public const string Ready           = "ready";
    public const string Imported        = "imported";
    public const string Changed         = "changed";
    public const string ProcessSwitched = "processSwitched";
    public const string Error           = "error";
    public const string Log             = "log";

    // Host → JS message types (envelope.type)
    public const string ImportJSON      = "importJSON";
    public const string SetTheme        = "setTheme";
}
