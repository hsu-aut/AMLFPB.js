using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aml.Editor.Plugin.FPB.Diagnostics;

namespace Aml.Editor.Plugin.FPB.Views;

/// <summary>
/// Diagram changes of one process view that were not applied when its file was
/// closed (B25). <see cref="IhHash"/> is the hash of the InstanceHierarchy the
/// changes were made against; a different hash on restore means the file
/// changed meanwhile.
/// </summary>
public sealed record PendingEntry(
    string FileName,
    string IhId,
    string IhLabel,
    DateTime SavedAt,
    string IhHash,
    string Snapshot);

/// <summary>
/// Keeps <see cref="PendingEntry"/>s on disk, one file per AML file and process
/// view (B25). The editor unloads a document before it tells the plugin, so the
/// changes cannot be applied any more; they are kept here and offered when the
/// same file is opened again.
///
/// The key is what the document itself says: the file name of the CAEX file
/// (without its folder, the editor resets the path of the plugin before it
/// unloads) and the ID of the InstanceHierarchy. Two different files with the
/// same name and the same hierarchy ID would share an entry; the offer names
/// the date, and the user can discard it.
/// </summary>
public sealed class PendingStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _directory;

    public PendingStore(string directory) => _directory = directory;

    /// <summary>Next to settings.json: %APPDATA%\AutomationMLEditor\FpbPlugin\pending</summary>
    public static PendingStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutomationMLEditor", "FpbPlugin", "pending"));

    /// <summary>The key of a file and process view, or null when the file has no name.</summary>
    public static string? KeyOf(string? fileName, string? ihId)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "" : Path.GetFileName(fileName.Trim());
        if (name.Length == 0) return null;
        var ih = (ihId ?? "").Trim().TrimStart('{').TrimEnd('}');
        return (name + "|" + ih).ToLowerInvariant();
    }

    public void Save(PendingEntry entry)
    {
        var path = PathOf(entry.FileName, entry.IhId);
        if (path == null) return;
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, JsonSerializer.Serialize(entry, Json), Encoding.UTF8);
    }

    public PendingEntry? Find(string? fileName, string? ihId)
    {
        var path = PathOf(fileName, ihId);
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<PendingEntry>(File.ReadAllText(path, Encoding.UTF8));
            return entry is { Snapshot.Length: > 0 } ? entry : null;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Saved diagram changes in {path} cannot be read: {ex.Message}");
            return null;
        }
    }

    public void Remove(string? fileName, string? ihId)
    {
        var path = PathOf(fileName, ihId);
        if (path == null) return;
        try { File.Delete(path); }
        catch (Exception ex) { PluginLog.Warn($"Saved diagram changes in {path} cannot be removed: {ex.Message}"); }
    }

    private string? PathOf(string? fileName, string? ihId)
    {
        var key = KeyOf(fileName, ihId);
        if (key == null) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(_directory, hash + ".json");
    }
}
