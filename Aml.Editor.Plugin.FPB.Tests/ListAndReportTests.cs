// 031: the findings list shows the catalog code and reads in the language of
// the plugin; the report covers the whole file, gives each finding its code
// and names what it does not contain.

using System.Text.Json.Nodes;
using Aml.Editor.Plugin.FPB.Localization;
using Aml.Editor.Plugin.FPB.Validation;
using Aml.Editor.Plugin.FPB.Views;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class ListAndReportTests
{
    private const string Erhitzen = "9ca400c0-a373-48c0-9f13-37ce196cee29";
    private const string ToPruefen = "d5dc692d-61ac-4b4b-aea5-3d3044345aa7";

    /// <summary>Temperieren with the flow into "Prüfen" starting at the operator "Erhitzen".</summary>
    private static CAEXDocument OperatorToOperator()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine("TestData", "Temperieren.json")))!.AsArray();
        json.OfType<JsonObject>().Where(e => e["process"] != null)
            .SelectMany(e => e["elementDataInformation"]!.AsArray().OfType<JsonObject>())
            .First(d => (string?)d["id"] == ToPruefen)["sourceRef"] = Erhitzen;
        return FpbJsonToCaex.Convert(json.ToJsonString()).Value;
    }

    private static string Bare(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');

    // 031 run in the real editor: an operator without a name showed its id in the
    // column "Element"; the web app names such elements by their kind
    [Fact]
    public void An_element_without_a_name_is_named_by_its_kind()
    {
        var doc = CAEXDocument.LoadFromString(File.ReadAllText(Path.Combine(FpbJs(), "tests", "fixtures", "model-check", "names.aml")));
        var row = FindingRow.From(new ValidationFinding("VDI3682.OCL.ElementHasName", ValidationSeverity.Warning, "m", "{9833d9a9-1b88-4bfb-91b3-7ab5e2617045}"), doc);

        Assert.Equal("(process operator without a name)", row.ElementLabel);
        Assert.Equal("(state without a name)", FindingRow.UnnamedLabel("VDI_FPD_SystemUnitClassLib/FPD_Energy"));
        Assert.Equal("(technical resource without a name)", FindingRow.UnnamedLabel("VDI_FPD_SystemUnitClassLib/FPD_TechnicalResource"));
        Assert.Equal("(element without a name)", FindingRow.UnnamedLabel("VDI_FPD_SystemUnitClassLib/FPD_SystemLimit"));
    }

    private static string FpbJs()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "FPB.JS");
            if (Directory.Exists(Path.Combine(path, "tests", "fixtures", "model-check"))) return path;
        }
        throw new DirectoryNotFoundException("FPB.JS/tests/fixtures/model-check was not found above the test folder.");
    }

    [Fact]
    public void The_rule_column_shows_the_catalog_code()
    {
        Assert.Equal("F1", FindingRow.From(new ValidationFinding("VDI3682.OCL.RefObjResolvable", ValidationSeverity.Error, "m", "x"), null).Rule);
        Assert.Equal("C3", FindingRow.FromPage(new PageFinding("C3", "error", "m", "e", "p"), null, null).Rule);
        Assert.Equal("VDI3682.OCL.Unclassified", FindingRow.From(new ValidationFinding("VDI3682.OCL.Unclassified", ValidationSeverity.Warning, "m", "x"), null).Rule);
    }

    [Fact]
    public void An_OCL_finding_reads_in_the_language_of_the_plugin()
    {
        var doc = OperatorToOperator();
        var c3 = Vdi3682Validator.ValidateStructured(doc).First(f => f.RuleId == "VDI3682.OCL.NoProcessOperatorToProcessOperatorFlow");
        var german = L.German;
        try
        {
            L.German = true;
            var message = FindingRow.From(c3, doc).Message;
            Assert.Contains("verbindet zwei Prozessoperatoren", message);
            Assert.DoesNotContain("violates", message);
        }
        finally
        {
            L.German = german;
        }
        Assert.DoesNotContain("violates", c3.Message);
    }

    [Fact]
    public void An_invariant_without_its_own_template_still_reads_as_a_sentence()
    {
        var f = Vdi3682OclRuleSet.Finding("VDI3682.OCL.SomethingNew", ValidationSeverity.Warning, "Tank", "t1");
        Assert.Equal("Element 'Tank' breaks rule SomethingNew.", f.Message);
        Assert.NotNull(f.Template);
    }

    [Fact]
    public void Of_C1_and_C3_for_one_flow_only_C3_stays()
    {
        var findings = new[]
        {
            new ValidationFinding("VDI3682.OCL.FlowEndpointsTyped", ValidationSeverity.Error, "c1", "{x}"),
            new ValidationFinding("VDI3682.OCL.NoProcessOperatorToProcessOperatorFlow", ValidationSeverity.Error, "c3", "x"),
            new ValidationFinding("VDI3682.OCL.FlowEndpointsTyped", ValidationSeverity.Error, "c1", "y"),
        };

        var kept = FindingsMerge.MostSpecific(findings).Select(f => (CatalogueCodes.CodeOf(f.RuleId), Bare(f.TargetIeId))).ToList();

        Assert.Equal(new[] { ("C3", "x"), ("C1", "y") }, kept);
    }

    [Fact]
    public void The_report_names_the_rules_it_does_not_contain()
    {
        var report = ConformanceReportBuilder.Build(OperatorToOperator(), DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
        Assert.Equal(new[] { "B1", "B2", "B3", "B6", "E5", "F5", "F8", "F9" }, report.NotIncluded);
        Assert.Contains("\"not_included\"", report.ToJson());
    }

    [Fact]
    public void The_report_gives_each_finding_its_code_and_one_per_cause()
    {
        var report = ConformanceReportBuilder.Build(OperatorToOperator(), DateTimeOffset.Parse("2026-11-06T00:00:00Z"));

        var flow = report.Findings.Where(f => Bare(f.TargetIeId) == ToPruefen).Select(f => f.Code).ToList();
        Assert.Contains("C3", flow);
        Assert.DoesNotContain("C1", flow);
        Assert.All(report.Findings.Where(f => f.RuleId.StartsWith("VDI3682.") && f.RuleId != "VDI3682.OCL.Unclassified"), f => Assert.NotNull(f.Code));
        Assert.Equal(report.Findings.Count(f => f.Severity == "Error"), report.Totals.Errors);
    }
}
