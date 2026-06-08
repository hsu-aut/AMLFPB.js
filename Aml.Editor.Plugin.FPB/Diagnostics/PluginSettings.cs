// Tiny key-value bag persisted as JSON under
//   %APPDATA%\AutomationMLEditor\FpbPlugin\settings.json
//
// Survives plugin reloads, editor restarts, and version upgrades. We keep the
// schema deliberately small so a forward-incompatible change can be handled by
// just deleting the file (the plugin starts with defaults).

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aml.Editor.Plugin.FPB.Diagnostics;

public sealed class PluginSettings
{
    // ── Persisted fields ───────────────────────────────────────────────
    [JsonPropertyName("debug_logging")]            public bool DebugLogging { get; set; }
    [JsonPropertyName("auto_save_after_update")]   public bool AutoSaveAfterUpdate { get; set; }
    [JsonPropertyName("run_vdi_validation")]       public bool RunVdiValidation { get; set; } = true;
    /// <summary>If true, ask for confirmation before Update InstanceHierarchy when the
    /// snapshot would add or remove more than <see cref="UpdateSafetyThreshold"/>
    /// elements or connections. Guards against the v1.5.0 phantom-pending class of
    /// data-loss bugs where a stray snapshot caused dozens of spurious adds/removes.</summary>
    [JsonPropertyName("confirm_large_updates")]    public bool ConfirmLargeUpdates { get; set; } = true;
    [JsonPropertyName("update_safety_threshold")]  public int  UpdateSafetyThreshold { get; set; } = 5;

    /// <summary>
    /// When > 0, warn the user before applying a pending snapshot that is older
    /// than this many minutes. Defense against stale snapshots that lingered
    /// across a long modelling break. Set to 0 to disable.
    /// </summary>
    [JsonPropertyName("pending_age_warning_minutes")] public int PendingAgeWarningMinutes { get; set; } = 30;

    /// <summary>
    /// VDI 3682 validator rule IDs the user has switched off (matching
    /// <c>IValidationRule.Id</c>, e.g. <c>"VDI3682.ProcessOperatorIdentification"</c>).
    /// </summary>
    [JsonPropertyName("disabled_validation_rules")] public List<string> DisabledValidationRuleIds { get; set; } = new();

    /// <summary>
    /// Lowest severity the validator surfaces. <c>"Info"</c> (all), <c>"Warning"</c>,
    /// or <c>"Error"</c>. Defaults to Info so nothing is hidden.
    /// </summary>
    [JsonPropertyName("validation_min_severity")]   public string ValidationMinSeverity { get; set; } = "Info";

    // ── Persistence plumbing ────────────────────────────────────────────

    private static readonly object _gate = new();
    private const string Subdir   = "AutomationMLEditor";
    private const string Folder   = "FpbPlugin";
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Subdir, Folder, FileName);

    /// <summary>Load settings; missing file or malformed JSON → fresh defaults.</summary>
    public static PluginSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new PluginSettings();
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<PluginSettings>(json, _json)
                       ?? new PluginSettings();
            }
            catch
            {
                return new PluginSettings();
            }
        }
    }

    /// <summary>Write the current values to disk. Best-effort: failures are swallowed.</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, _json));
            }
            catch
            {
                // Couldn't persist — runtime state still holds. Next session
                // re-asks the defaults; minor cosmetic loss only.
            }
        }
    }
}
