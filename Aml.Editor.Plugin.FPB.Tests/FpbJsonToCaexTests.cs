using FpbMapper.Conversion;
using FpbMapper.Conversion.Models;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class FpbJsonToCaexTests
{
    private static string GetTestDataPath(string filename) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", filename);

    [Fact]
    public void ParseFpbJson_ExtractsProjectAndProcessEntries()
    {
        var json = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var (project, entries) = FpbJsonParser.Parse(json);

        Assert.Equal("fpb:Project", project.Type);
        Assert.NotEmpty(project.EntryPoint);
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void Convert_CreatesValidCaexDocument()
    {
        var json = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var document = FpbJsonToCaex.Convert(json).Value;

        Assert.NotNull(document);
        var caexFile = document.CAEXFile;

        Assert.Single(caexFile.InstanceHierarchy);

        Assert.Contains(caexFile.InterfaceClassLib, l => l.Name == "VDI_FPD_InterfaceClassLib");
        Assert.Contains(caexFile.SystemUnitClassLib, l => l.Name == "VDI_FPD_SystemUnitClassLib");
        Assert.Contains(caexFile.AttributeTypeLib, l => l.Name == "VDI_FPD_AttributeTypeLib");
        Assert.Contains(caexFile.ExternalReference, e => e.Alias == FpbMapper.Conversion.DiagramInterchangeLibrary.Alias);
    }

    [Fact]
    public void Convert_CreatesProcessWithElements()
    {
        var json = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var document = FpbJsonToCaex.Convert(json).Value;

        var ih = document.CAEXFile.InstanceHierarchy.First();
        var topProcess = ih.InternalElement.First();

        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_Process", topProcess.RefBaseSystemUnitPath);
        Assert.True(topProcess.InternalElement.Count() > 1);
        Assert.True(topProcess.InternalLink.Any());
    }

    [Fact]
    public void Convert_SystemLimitHasViewInformation()
    {
        var json = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var document = FpbJsonToCaex.Convert(json).Value;

        var ih = document.CAEXFile.InstanceHierarchy.First();
        var topProcess = ih.InternalElement.First();

        var systemLimit = topProcess.InternalElement
            .FirstOrDefault(ie => ie.RefBaseSystemUnitPath == "VDI_FPD_SystemUnitClassLib/FPD_SystemLimit");

        Assert.NotNull(systemLimit);
        Assert.NotNull(systemLimit.Attribute["ViewInformation"]);
    }

    [Fact]
    public void Convert_ObjectsHaveIdentificationAndView()
    {
        var json = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var document = FpbJsonToCaex.Convert(json).Value;

        var ih = document.CAEXFile.InstanceHierarchy.First();
        var topProcess = ih.InternalElement.First();

        var po = topProcess.InternalElement
            .FirstOrDefault(ie => ie.RefBaseSystemUnitPath == "VDI_FPD_SystemUnitClassLib/FPD_ProcessOperator");

        Assert.NotNull(po);
        Assert.NotNull(po.Attribute["Identification"]);
        Assert.NotNull(po.Attribute["ViewInformation"]);
    }

    [Fact]
    public void Convert_FlowsCreateExternalInterfacesAndLinks()
    {
        var json = File.ReadAllText(GetTestDataPath("Temperieren.json"));
        var document = FpbJsonToCaex.Convert(json).Value;

        var ih = document.CAEXFile.InstanceHierarchy.First();
        var topProcess = ih.InternalElement.First();

        var totalInterfaces = topProcess.InternalElement.Sum(ie => ie.ExternalInterface.Count());
        Assert.True(totalInterfaces > 0, "Should have ExternalInterfaces for flows");

        Assert.True(topProcess.InternalLink.Any(), "Should have InternalLinks connecting interfaces");
    }
}
