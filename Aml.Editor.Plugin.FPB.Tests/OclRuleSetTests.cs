// 031: one rule set. The model rules belong to FPB.js and OCL; the OCL rules
// follow the verdict decided in 031 (D3 to D5 merged into D7, D8 "missing or
// empty", I3 only inside the FPD, A3 and A4 only for a process that has begun).

using System.Text.Json.Nodes;
using Aml.Editor.Plugin.FPB.Validation;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class OclRuleSetTests
{
    private const string Erhitzen = "9ca400c0-a373-48c0-9f13-37ce196cee29";
    private const string Pruefen = "9833d9a9-1b88-4bfb-91b3-7ab5e2617045";
    private const string ToPruefen = "d5dc692d-61ac-4b4b-aea5-3d3044345aa7";

    private static CAEXDocument Temperieren(Action<JsonArray>? change = null)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine("TestData", "Temperieren.json")))!.AsArray();
        change?.Invoke(json);
        return FpbJsonToCaex.Convert(json.ToJsonString()).Value;
    }

    private static JsonObject Data(JsonArray model, string id) =>
        model.OfType<JsonObject>().Where(e => e["process"] != null)
            .SelectMany(e => e["elementDataInformation"]!.AsArray().OfType<JsonObject>())
            .First(d => (string?)d["id"] == id);

    private static List<ValidationFinding> Check(CAEXDocument doc) => Vdi3682Validator.ValidateStructured(doc).ToList();

    private static string Bare(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');

    private static IEnumerable<InternalElementType> All(IEnumerable<InternalElementType> elements) =>
        elements.SelectMany(ie => new[] { ie }.Concat(All(ie.InternalElement)));

    private static InternalElementType Element(CAEXDocument doc, string id) =>
        doc.CAEXFile.InstanceHierarchy.SelectMany(ih => All(ih.InternalElement)).First(ie => Bare(ie.ID) == id);

    /// <summary>The oracle input of FPB.js: Showcase A with a plant hierarchy linked from outside.</summary>
    private static string MixedAml()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "FPB.JS", "tests", "unit", "aml", "oracle", "inputs", "generated", "mixed.aml");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("FPB.JS/tests/unit/aml/oracle/inputs/generated/mixed.aml was not found above the test folder.");
    }

    [Fact]
    public void An_element_without_any_name_gets_one_D7_warning_and_no_D3_to_D5()
    {
        var doc = Temperieren();
        var op = Element(doc, Erhitzen);
        op.Name = "";
        foreach (var a in op.Attribute.Where(a => a.Name == IdentificationSchema.AttributeName).SelectMany(a => a.Attribute))
            if (a.Name is IdentificationSchema.ShortName or IdentificationSchema.LongName) a.Value = "";

        var mine = Check(doc).Where(f => Bare(f.TargetIeId) == Erhitzen).ToList();

        Assert.Single(mine, f => f.RuleId == "VDI3682.OCL.LongNameMandatory" && f.Severity == ValidationSeverity.Warning);
        Assert.DoesNotContain(mine, f => f.RuleId is "VDI3682.OCL.ProcessOperatorNamed" or "VDI3682.OCL.StateNamed"
            or "VDI3682.OCL.TechnicalResourceNamed" or "VDI3682.ProcessOperatorIdentification");
    }

    [Fact]
    public void An_empty_version_counts_as_missing_for_D8()
    {
        var doc = Temperieren(m =>
        {
            foreach (var d in m.OfType<JsonObject>().Where(e => e["process"] != null)
                         .SelectMany(e => e["elementDataInformation"]!.AsArray().OfType<JsonObject>())
                         .Where(d => d["identification"] is JsonObject))
            {
                d["identification"]!["versionNumber"] = "1";
                d["identification"]!["revisionNumber"] = "0";
            }
        });
        // as a file from another tool may hold it: the attribute there, its value empty
        var version = Element(doc, Erhitzen).Attribute.First(a => a.Name == IdentificationSchema.AttributeName).Attribute.First(a => a.Name == IdentificationSchema.VersionNumber);
        version.Value = "";

        var d8 = Check(doc).Where(f => f.RuleId == "VDI3682.OCL.VersionRevisionPresent").Select(f => Bare(f.TargetIeId)).ToList();

        Assert.Contains(Erhitzen, d8);
        Assert.DoesNotContain(Pruefen, d8);
    }

    [Fact]
    public void A_link_to_an_element_outside_the_FPD_is_no_I3()
    {
        var doc = CAEXDocument.LoadFromFile(MixedAml());

        Assert.DoesNotContain(Check(doc), f => f.RuleId == "VDI3682.OCL.SourceTargetSameProcess");
    }

    [Fact]
    public void A_link_between_FPD_elements_of_two_processes_is_still_I3()
    {
        var doc = Temperieren();
        // a flow out of "Prüfen" (top layer) into "Vorwärmen" (decomposition of "Erhitzen")
        var top = Element(doc, Pruefen);
        var below = Element(doc, "c9dc0159-5e01-4139-a98a-141a10e4e4c4");
        var process = (InternalElementType)top.CAEXParent!;
        while (!process.RefBaseSystemUnitPath?.EndsWith("FPD_Process") ?? true)
            process = (InternalElementType)process.CAEXParent!;
        var link = process.InternalLink.Append("AcrossLayers");
        link.RefPartnerSideA = top.ExternalInterface.First(i => i.RefBaseClassPath?.EndsWith("FlowOut") ?? false).ID;
        link.RefPartnerSideB = below.ExternalInterface.First(i => i.RefBaseClassPath?.EndsWith("FlowIn") ?? false).ID;

        Assert.Contains(Check(doc), f => f.RuleId == "VDI3682.OCL.SourceTargetSameProcess");
    }

    [Fact]
    public void An_empty_process_gets_no_A3_or_A4()
    {
        var doc = Temperieren();
        FpbJsonToCaex.CreateEmptyFpdInstanceHierarchy(doc, "Empty_IH", "Empty");

        Assert.DoesNotContain(Check(doc), f => f.RuleId is "VDI3682.OCL.StateMinimumCardinality" or "VDI3682.OCL.ProcessOperatorMinimumCardinality"
            or "VDI3682.StateCardinality" or "VDI3682.ProcessOperatorCardinality");
    }

    [Fact]
    public void A_flow_between_two_operators_is_C3_from_OCL_and_no_plugin_rule()
    {
        var doc = Temperieren(m => Data(m, ToPruefen)["sourceRef"] = Erhitzen);

        var findings = Check(doc);

        Assert.Contains(findings, f => f.RuleId == "VDI3682.OCL.NoProcessOperatorToProcessOperatorFlow" && f.Severity == ValidationSeverity.Error);
        Assert.DoesNotContain(findings, f => f.RuleId == "VDI3682.FlowEndpointTyping");
    }

    [Fact]
    public void The_plugin_keeps_only_its_AML_rules()
    {
        Assert.Equal(new[] { "VDI3682.InternalLinkInterface", "VDI3682.RefObjIntegrity" },
            Vdi3682Validator.DefaultRules.Select(r => r.Id).Order().ToArray());
    }
}
