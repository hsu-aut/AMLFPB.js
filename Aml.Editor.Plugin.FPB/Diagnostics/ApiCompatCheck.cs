// Plugin-Robustness B.3 — startup self-test.
//
// Runs once during plugin construction. Probes every reflection-based
// integration point and writes a compact report to the log so any silent
// API drift (e.g. SaveAMLCommand renamed, DataContext layout changed) is
// visible without the user having to attempt the affected action.

using System.Reflection;
using System.Text;
using System.Windows;
using Aml.Editor.Plugin.Contracts;

namespace Aml.Editor.Plugin.FPB.Diagnostics;

public static class ApiCompatCheck
{
    public sealed class CheckResult
    {
        public string Name { get; init; } = "";
        public bool Ok { get; init; }
        public string Detail { get; init; } = "";
    }

    /// <summary>
    /// Returns one <see cref="CheckResult"/> per probe. Pure / side-effect-free
    /// so the host can show them anywhere (log, UI banner, CI report).
    /// </summary>
    public static List<CheckResult> Run()
    {
        var results = new List<CheckResult>
        {
            CheckINotifyAMLDocumentLoad(),
            CheckApplicationThemeEnum(),
            CheckEditorSaverReflectionPath(),
            CheckSchemaConstantsResolve(),
        };
        return results;
    }

    public static string FormatReport(IEnumerable<CheckResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Compatibility report ===");
        foreach (var r in results)
            sb.AppendLine($"  {(r.Ok ? "✓" : "✗")} {r.Name} — {r.Detail}");
        return sb.ToString();
    }

    private static CheckResult CheckINotifyAMLDocumentLoad()
    {
        try
        {
            var t = typeof(INotifyAMLDocumentLoad);
            var ok = t.GetMethod("DocumentLoaded") != null
                  && t.GetMethod("DocumentUnLoaded") != null
                  && t.GetMethod("ApplicationClose") != null
                  && t.GetEvent("IsDocumentLoaded") != null;
            return new CheckResult { Name = "INotifyAMLDocumentLoad members", Ok = ok,
                Detail = ok ? "all four members present" : "one or more members missing" };
        }
        catch (Exception ex) { return new CheckResult { Name = "INotifyAMLDocumentLoad", Ok = false, Detail = ex.Message }; }
    }

    private static CheckResult CheckApplicationThemeEnum()
    {
        try
        {
            var names = Enum.GetNames(typeof(ApplicationTheme));
            var hasDark  = names.Any(n => n.Contains("Dark", StringComparison.OrdinalIgnoreCase));
            var hasLight = names.Any(n => n.Contains("Light", StringComparison.OrdinalIgnoreCase));
            return new CheckResult { Name = "ApplicationTheme enum",
                Ok = hasDark && hasLight,
                Detail = hasDark && hasLight
                    ? $"contains {names.Length} value(s) incl. Dark/Light"
                    : "missing Dark or Light variant" };
        }
        catch (Exception ex) { return new CheckResult { Name = "ApplicationTheme", Ok = false, Detail = ex.Message }; }
    }

    private static CheckResult CheckEditorSaverReflectionPath()
    {
        try
        {
            var mw = Application.Current?.MainWindow;
            if (mw == null) return new CheckResult { Name = "Editor save reflection",
                Ok = false, Detail = "MainWindow not yet available (run on UI ready)" };
            var vm = mw.DataContext;
            if (vm == null) return new CheckResult { Name = "Editor save reflection",
                Ok = false, Detail = "MainWindow.DataContext is null" };

            // Keep this list in sync with EditorSaver.CandidateProperties.
            // Skipping SaveCurrentAMLFileCommand would produce a false-
            // positive "Editor save reflection: degraded" on editors that
            // only expose that fourth name.
            foreach (var prop in new[] { "SaveAMLCommand", "SaveCommand", "SaveActiveDocumentCommand", "SaveCurrentAMLFileCommand" })
            {
                if (vm.GetType().GetProperty(prop,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic) != null)
                    return new CheckResult { Name = "Editor save reflection",
                        Ok = true, Detail = $"{vm.GetType().Name}.{prop} resolved" };
            }
            return new CheckResult { Name = "Editor save reflection",
                Ok = false, Detail = $"no save-command property on {vm.GetType().FullName}" };
        }
        catch (Exception ex) { return new CheckResult { Name = "Editor save reflection", Ok = false, Detail = ex.Message }; }
    }

    private static CheckResult CheckSchemaConstantsResolve()
    {
        try
        {
            var processSuc = FpbMapper.Conversion.FpbMappings.ElementToSuc[FpbMapper.Conversion.FpbTypes.Process];
            var version    = FpbMapper.Conversion.FpbMappings.LibNames.Version;
            return new CheckResult { Name = "Mapper schema constants", Ok = true,
                Detail = $"FpbMappings + FpbTypes resolve (lib version {version})" };
        }
        catch (Exception ex) { return new CheckResult { Name = "Mapper schema constants", Ok = false, Detail = ex.Message }; }
    }
}
