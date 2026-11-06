// The catalog id (VDI 3682 rule catalog, e.g. "D4") of every rule the plugin's
// check can report, so the findings list knows which of them FPB.js checks
// itself (016f E3, V25).

using Aml.Editor.Plugin.FPB.Validation;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class CatalogueCodesTests
{
    [Fact]
    public void EveryRuleOfThePluginIsKnown()
    {
        var ids = Vdi3682Validator.DefaultRules.Select(r => r.Id)
            .Concat(Vdi3682OclRuleSet.LoadRuleSpecs(includeHardcodedCovered: true).Select(s => s.Id))
            .Distinct()
            .ToList();
        Assert.NotEmpty(ids);
        var unknown = ids.Where(id => !CatalogueCodes.KnownRuleIds.Contains(id)).ToList();
        Assert.True(unknown.Count == 0, "unknown rule ids: " + string.Join(", ", unknown));
    }

    [Theory]
    [InlineData("VDI3682.OCL.LongNameMandatory", "D7")]
    [InlineData("VDI3682.OCL.UniqueIdentifiers", "D1")]
    [InlineData("VDI3682.OCL.NoDuplicateConnections", "C5")]
    [InlineData("VDI3682.OCL.SystemLimitCardinality", "A2")]
    [InlineData("VDI3682.InternalLinkInterface", "C6")]
    [InlineData("VDI3682.RefObjIntegrity", "F1")]
    public void CodeOfNamesTheCatalogRule(string ruleId, string code)
    {
        Assert.Equal(code, CatalogueCodes.CodeOf(ruleId));
    }

    [Theory]
    [InlineData("VDI3682.OCL.UnclassifiedElement")]
    [InlineData("VDI3682.OCL")]
    [InlineData("something else")]
    public void RulesOutsideTheCatalogHaveNoCode(string ruleId)
    {
        Assert.Null(CatalogueCodes.CodeOf(ruleId));
    }
}
