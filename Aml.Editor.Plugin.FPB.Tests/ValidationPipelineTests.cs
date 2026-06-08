using System.Text.Json;
using Aml.Editor.Plugin.FPB.Validation;
using Aml.Editor.Plugin.FPB.Validation.Vdi3682Rules;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class ValidationPipelineTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    private static CAEXDocument BuildDoc() =>
        FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value;

    [Fact]
    public void DefaultRules_HaveStableIds()
    {
        var ids = Vdi3682Validator.DefaultRules.Select(r => r.Id).ToList();
        Assert.Contains("VDI3682.SystemLimitCardinality", ids);
        Assert.Contains("VDI3682.StateCardinality", ids);
        Assert.Contains("VDI3682.ProcessOperatorCardinality", ids);
        Assert.Contains("VDI3682.ProcessOperatorIdentification", ids);
        Assert.Contains("VDI3682.RefObjIntegrity", ids);
        Assert.Contains("VDI3682.InternalLinkInterface", ids);
        Assert.Contains("VDI3682.FlowEndpointTyping", ids);
        Assert.Equal(ids.Distinct().Count(), ids.Count); // no dupes
    }

    [Fact]
    public void DisabledRuleId_IsExcludedFromPass()
    {
        var doc = BuildDoc();
        var allFindings = Vdi3682Validator.ValidateStructured(doc);
        var disabled = new ValidationOptions
        {
            DisabledRuleIds = new HashSet<string> { "VDI3682.ProcessOperatorIdentification" },
        };
        var fewer = Vdi3682Validator.ValidateStructured(doc, disabled);

        Assert.DoesNotContain(fewer, f => f.RuleId == "VDI3682.ProcessOperatorIdentification");
        // Other findings (if any) remain.
        var otherRuleIds = allFindings.Where(f => f.RuleId != "VDI3682.ProcessOperatorIdentification").Select(f => f.RuleId).Distinct().ToList();
        foreach (var rid in otherRuleIds)
            Assert.Contains(fewer, f => f.RuleId == rid);
    }

    [Fact]
    public void MinimumSeverity_FiltersOutLowerLevels()
    {
        var doc = BuildDoc();
        var errorsOnly = Vdi3682Validator.ValidateStructured(doc, new ValidationOptions
        {
            MinimumSeverity = ValidationSeverity.Error,
        });
        Assert.All(errorsOnly, f => Assert.Equal(ValidationSeverity.Error, f.Severity));
    }

    [Fact]
    public void ConformanceReport_RoundtripsThroughJson()
    {
        var doc = BuildDoc();
        var report = ConformanceReportBuilder.Build(doc, DateTimeOffset.Parse("2026-06-08T00:00:00Z"));
        var json = report.ToJson();
        Assert.Contains("\"amlfpbjs.vdi3682.conformance/v1\"", json);
        Assert.Contains("\"library_version\":", json);
        Assert.Contains("\"rule_set\":", json);
        Assert.Contains("\"findings\":", json);
        Assert.Contains("\"totals\":", json);

        using var parsed = JsonDocument.Parse(json);
        var totals = parsed.RootElement.GetProperty("totals");
        Assert.True(totals.TryGetProperty("errors", out _));
        Assert.True(totals.TryGetProperty("warnings", out _));
        Assert.True(totals.TryGetProperty("infos", out _));
        Assert.True(totals.TryGetProperty("clean", out _));
    }

    [Fact]
    public void ConformanceReport_CountsMatchValidation()
    {
        var doc = BuildDoc();
        var findings = Vdi3682Validator.ValidateStructured(doc);
        var report = ConformanceReportBuilder.Build(doc, DateTimeOffset.Parse("2026-06-08T00:00:00Z"));
        Assert.Equal(findings.Count(f => f.Severity == ValidationSeverity.Error), report.Totals.Errors);
        Assert.Equal(findings.Count(f => f.Severity == ValidationSeverity.Warning), report.Totals.Warnings);
        Assert.Equal(findings.Count(f => f.Severity == ValidationSeverity.Info), report.Totals.Infos);
        Assert.Equal(findings.Count == 0, report.Totals.Clean);
    }

    [Fact]
    public void Validator_BackCompatStringApi_StillWorks()
    {
        var doc = BuildDoc();
        // Legacy API used by older callers (and the IhView log path).
        var lines = Vdi3682Validator.Validate(doc);
        Assert.NotNull(lines);
        foreach (var line in lines) Assert.False(string.IsNullOrEmpty(line));
    }
}
