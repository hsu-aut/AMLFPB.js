using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aml.Editor.Plugin.FPB.Bridge;

/// <summary>
/// Messages received from the embedded FPB.JS WebView (parsed from JSON).
/// Mirrors the shapes posted by fpbjs-assets/index.html; the protocol is in
/// FPB.JS_Docs/Geplante-Features/Plaene/2026-11-05-016f-E3-Plugin.md.
/// </summary>
public class JsToHostMessage
{
    [JsonPropertyName("type")]   public string Type { get; set; } = "";
    [JsonPropertyName("message")]public string? Message { get; set; }
    [JsonPropertyName("id")]     public string? Id { get; set; }
    [JsonPropertyName("url")]    public string? Url { get; set; }
    [JsonPropertyName("theme")]  public string? Theme { get; set; }
    [JsonPropertyName("level")]  public string? Level { get; set; }

    /// <summary>Number of the import an "imported" answer belongs to.</summary>
    [JsonPropertyName("seq")]    public int? Seq { get; set; }

    /// <summary>Layer (process id) of a "selected" message.</summary>
    [JsonPropertyName("processId")] public string? ProcessId { get; set; }

    /// <summary>Raw payload for "changed", "save" and "imported" messages (FPB.JS toJSON output).</summary>
    [JsonPropertyName("data")]   public JsonElement Data { get; set; }

    /// <summary>"findings": the catalog rules FPB.js checks.</summary>
    [JsonPropertyName("rules")]  public JsonElement Rules { get; set; }

    /// <summary>"findings": the findings of FPB.js.</summary>
    [JsonPropertyName("items")]  public JsonElement Items { get; set; }
}

/// <summary>Type tags posted by JS — kept as constants to avoid stringly-typed comparisons in the host.</summary>
public static class JsMessageType
{
    public const string Ready           = "ready";
    public const string Imported        = "imported";
    public const string Changed         = "changed";
    public const string Save            = "save";
    public const string Selected        = "selected";
    public const string Findings        = "findings";
    public const string ProcessSwitched = "processSwitched";
    public const string Error           = "error";
    public const string Log             = "log";

    // Host → JS message types (envelope.type)
    public const string ImportJSON           = "importJSON";
    public const string SetTheme             = "setTheme";
    public const string Select               = "select";
    public const string SetReadOnly          = "setReadOnly";
    public const string SetValidationMarkers = "setValidationMarkers";
}

/// <summary>An import into the viewer, with the number the page answers with (016f E3, T18).</summary>
public sealed record ImportJsonMessage([property: JsonPropertyName("type")] string Type,
                                       [property: JsonPropertyName("seq")] int Seq,
                                       [property: JsonPropertyName("data")] JsonElement Data)
{
    public static ImportJsonMessage For(int seq, JsonElement data) => new(JsMessageType.ImportJSON, seq, data);
}

/// <summary>
/// Payload posted from the host to ask the viewer to select an element on a
/// layer and scroll it into view (016f E3, V26). <see cref="Id"/> is the
/// element id as it appears in the FPB.JS model (the bare uniqueIdent for
/// AML-sourced documents); without <see cref="ProcessId"/> the viewer looks
/// on the shown layer first.
/// </summary>
public sealed record SelectMessage([property: JsonPropertyName("type")] string Type,
                                   [property: JsonPropertyName("id")] string Id,
                                   [property: JsonPropertyName("processId")] string? ProcessId)
{
    public static SelectMessage For(string id, string? processId) => new(JsMessageType.Select, id, processId);
}

/// <summary>Payload turning the viewer's read-only mode on or off (FPB.js setReadOnly).</summary>
public sealed record SetReadOnlyMessage([property: JsonPropertyName("type")] string Type,
                                        [property: JsonPropertyName("readOnly")] bool ReadOnly)
{
    public static SetReadOnlyMessage For(bool readOnly) => new(JsMessageType.SetReadOnly, readOnly);
}

/// <summary>Payload showing or hiding the markers of the model check (FPB.js setValidationMarkers).</summary>
public sealed record SetValidationMarkersMessage([property: JsonPropertyName("type")] string Type,
                                                 [property: JsonPropertyName("show")] bool Show)
{
    public static SetValidationMarkersMessage For(bool show) => new(JsMessageType.SetValidationMarkers, show);
}
