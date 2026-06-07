// Plugin-wide diagnostics. Logs go to:
//   1. The Status-Log TextBox in the plugin UI (always, INFO + WARN + ERROR).
//   2. A file under %TEMP%\fpb-plugin\ (always; also captures DEBUG when enabled).
//
// Users (and Claude) can read the file directly to investigate crashes without
// having to copy text out of the editor UI.
//
// Writer architecture: one StreamWriter is opened on first Init() and kept open
// for the lifetime of the process. AutoFlush is on so a crash still leaves the
// tail of the log on disk. Rollover closes the writer, archives the file, and
// reopens fresh — happens at most every 500 writes.

using System.IO;
using System.Text;

namespace Aml.Editor.Plugin.FPB.Diagnostics;

public static class PluginLog
{
    private static readonly object _gate = new();
    private static string? _filePath;
    private static StreamWriter? _writer;
    private static bool _debugEnabled;
    private static int _writesSinceRolloverCheck;

    private const long MaxBytes = 5 * 1024 * 1024;
    private const int RolloverCheckInterval = 500;
    private const string Subdir = "fpb-plugin";

    /// <summary>Fired for every emitted line — wire the Status-Log TextBox here.</summary>
    public static event Action<string>? OnLine;

    /// <summary>Absolute path of the active log file (empty until <see cref="Init"/>).</summary>
    public static string FilePath => _filePath ?? string.Empty;

    /// <summary>Directory hosting the log file. Useful for an "Open log folder" UI.</summary>
    public static string LogDirectory =>
        Path.Combine(Path.GetTempPath(), Subdir);

    public static bool DebugEnabled
    {
        get => _debugEnabled;
        set
        {
            if (_debugEnabled == value) return;
            _debugEnabled = value;
            Info($"Debug logging {(value ? "ENABLED" : "DISABLED")}.");
        }
    }

    /// <summary>
    /// Initialise the log file. Safe to call repeatedly — second and later calls
    /// just re-check the rollover threshold on the existing writer. Failure to
    /// open the writer is silently swallowed (UI logging still works).
    /// </summary>
    public static void Init()
    {
        lock (_gate)
        {
            if (_writer != null)
            {
                MaybeRollover_NoLock();
                return;
            }

            try
            {
                Directory.CreateDirectory(LogDirectory);
                var candidate = Path.Combine(LogDirectory, "fpb-plugin-debug.log");
                ArchiveIfTooLarge_NoLock(candidate);
                OpenWriter_NoLock(candidate);
                WriteLineLocked($"================== PLUGIN START {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==================");
            }
            catch
            {
                // Couldn't open the file: keep going, UI logging continues.
                CloseWriter_NoLock();
                _filePath = null;
            }
        }
    }

    /// <summary>
    /// Best-effort shutdown so the last few buffered lines hit disk on a planned
    /// plugin close. Safe to call multiple times; the writer is re-opened on the
    /// next <see cref="Init"/>.
    /// </summary>
    public static void Shutdown()
    {
        lock (_gate)
        {
            try { _writer?.Flush(); } catch { /* nothing to do */ }
            CloseWriter_NoLock();
        }
    }

    public static void Info(string msg)  => Emit("INFO ", msg);
    public static void Warn(string msg)  => Emit("WARN ", msg);

    public static void Error(string msg, Exception? ex = null)
    {
        if (ex == null)
        {
            Emit("ERROR", msg);
        }
        else
        {
            Emit("ERROR", msg);
            Emit("ERROR", "  " + ex.GetType().Name + ": " + ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                foreach (var line in ex.StackTrace.Split('\n'))
                    Emit("ERROR", "  " + line.TrimEnd('\r'));
            }
        }
    }

    /// <summary>Verbose breadcrumb — only written if <see cref="DebugEnabled"/> is true.</summary>
    public static void Debug(string msg)
    {
        if (!_debugEnabled) return;
        Emit("DEBUG", msg);
    }

    /// <summary>Bridge for log lines coming from the JS side (preserve original level).</summary>
    public static void FromJs(string level, string msg)
    {
        var tag = level?.ToUpperInvariant() switch
        {
            "ERROR" => "JS-ERR",
            "WARN"  => "JS-WRN",
            "DEBUG" => "JS-DBG",
            _        => "JS-LOG",
        };
        if ((tag == "JS-DBG" || tag == "JS-LOG") && !_debugEnabled) return;
        Emit(tag, SanitiseLine(msg));
    }

    /// <summary>
    /// Strip embedded CR/LF from arbitrary message text so a forwarded JS line cannot
    /// forge log-record framing (cosmetic, also helps grep stay sane).
    /// </summary>
    private static string SanitiseLine(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\r", "").Replace("\n", " ⏎ ");

    private static void Emit(string level, string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{ts}] [{level}] {msg}";
        lock (_gate)
        {
            WriteLineLocked(line);
        }
        try { OnLine?.Invoke(line); } catch { /* UI handler might throw, swallow */ }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Writer plumbing — every helper here must be called with _gate held.
    // ────────────────────────────────────────────────────────────────────────

    private static void OpenWriter_NoLock(string path)
    {
        // FileShare.Read so the user can open the file in Notepad/VSCode while
        // we keep writing. UTF8 *without* BOM keeps tail lines clean.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(false))
        {
            AutoFlush = true,   // so a crash still leaves the tail on disk
        };
        _filePath = path;
        _writesSinceRolloverCheck = 0;
    }

    private static void CloseWriter_NoLock()
    {
        try { _writer?.Dispose(); } catch { /* swallow */ }
        _writer = null;
    }

    private static void WriteLineLocked(string line)
    {
        if (_writer == null) return;
        try { _writer.WriteLine(line); }
        catch
        {
            // Disk full / file deleted under us / handle invalid — drop the writer
            // and try to re-open on the next Init() / Emit cycle.
            CloseWriter_NoLock();
            _filePath = null;
            return;
        }

        if (++_writesSinceRolloverCheck >= RolloverCheckInterval)
        {
            _writesSinceRolloverCheck = 0;
            MaybeRollover_NoLock();
        }
    }

    private static void MaybeRollover_NoLock()
    {
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;
        try
        {
            if (new FileInfo(_filePath).Length <= MaxBytes) return;

            var current = _filePath;
            CloseWriter_NoLock();
            ArchiveIfTooLarge_NoLock(current);
            OpenWriter_NoLock(current);
        }
        catch
        {
            // Rollover failure leaves the existing writer closed; next write will
            // re-open via the catch above.
            CloseWriter_NoLock();
        }
    }

    private static void ArchiveIfTooLarge_NoLock(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length <= MaxBytes) return;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var archive = Path.Combine(LogDirectory, $"fpb-plugin-debug.{stamp}.log");
            File.Move(path, archive);
        }
        catch
        {
            // Couldn't archive (file locked, no permission, …). Leave the existing
            // file in place — better one big log than no logs.
        }
    }
}
