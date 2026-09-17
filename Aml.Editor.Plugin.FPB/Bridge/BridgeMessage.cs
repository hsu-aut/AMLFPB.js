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
    public const string SelectElement   = "selectElement";
}

/// <summary>
/// Payload posted from the host to ask the viewer to select an element and scroll
/// it into view. <see cref="Id"/> is the element id as it appears in the FPB.JS
/// model (the bare uniqueIdent for AML-sourced documents).
/// </summary>
public sealed record SelectElementMessage([property: JsonPropertyName("type")] string Type,
                                          [property: JsonPropertyName("id")] string Id)
{
    public static SelectElementMessage For(string id) => new(JsMessageType.SelectElement, id);
}
