// The tests read the texts of the plugin in English, also on a German
// Windows: the language is set once when the test assembly loads; tests of
// the German set it themselves and put it back (016h F2).

using System.Runtime.CompilerServices;
using Aml.Editor.Plugin.FPB.Localization;

namespace Aml.Editor.Plugin.FPB.Tests;

internal static class TestLanguage
{
    [ModuleInitializer]
    internal static void English() => L.German = false;
}
