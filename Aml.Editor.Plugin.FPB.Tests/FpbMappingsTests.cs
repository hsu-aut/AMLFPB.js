using FpbMapper.Conversion;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public class FpbMappingsTests
{
    [Fact]
    public void ElementToSuc_ContainsAllExpectedTypes()
    {
        Assert.Equal(7, FpbMappings.ElementToSuc.Count);
        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_Product", FpbMappings.ElementToSuc["fpb:Product"]);
        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_Energy", FpbMappings.ElementToSuc["fpb:Energy"]);
        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_Information", FpbMappings.ElementToSuc["fpb:Information"]);
        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_ProcessOperator", FpbMappings.ElementToSuc["fpb:ProcessOperator"]);
        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_TechnicalResource", FpbMappings.ElementToSuc["fpb:TechnicalResource"]);
        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_SystemLimit", FpbMappings.ElementToSuc["fpb:SystemLimit"]);
        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_Process", FpbMappings.ElementToSuc["fpb:Process"]);
    }

    [Fact]
    public void SucToElement_IsReverseOfElementToSuc()
    {
        foreach (var (fpbType, sucPath) in FpbMappings.ElementToSuc)
        {
            Assert.True(FpbMappings.SucToElement.ContainsKey(sucPath));
            Assert.Equal(fpbType, FpbMappings.SucToElement[sucPath]);
        }
    }

    [Fact]
    public void FlowToInterface_ContainsAllFlowTypes()
    {
        Assert.Equal(4, FpbMappings.FlowToInterface.Count);
        Assert.Contains("fpb:Flow", FpbMappings.FlowToInterface.Keys);
        Assert.Contains("fpb:ParallelFlow", FpbMappings.FlowToInterface.Keys);
        Assert.Contains("fpb:AlternativeFlow", FpbMappings.FlowToInterface.Keys);
        Assert.Contains("fpb:Usage", FpbMappings.FlowToInterface.Keys);
    }

    [Fact]
    public void InterfaceToFlow_ReverseLookupWorks()
    {
        var (flowType, dir) = FpbMappings.InterfaceToFlow["VDI_FPD_InterfaceClassLib/FPD_FlowOut"];
        Assert.Equal("fpb:Flow", flowType);
        Assert.Equal("out", dir);

        (flowType, dir) = FpbMappings.InterfaceToFlow["VDI_FPD_InterfaceClassLib/FPD_FlowIn"];
        Assert.Equal("fpb:Flow", flowType);
        Assert.Equal("in", dir);
    }

    [Fact]
    public void Usage_HasSameInterfaceForBothDirections()
    {
        var (outPath, inPath) = FpbMappings.FlowToInterface["fpb:Usage"];
        Assert.Equal(outPath, inPath);
        Assert.Equal("VDI_FPD_InterfaceClassLib/FPD_Usage", outPath);
    }

    [Fact]
    public void ObjectTypes_ContainsSixTypes()
    {
        Assert.Equal(6, FpbMappings.ObjectTypes.Count);
        Assert.Contains("fpb:Product", FpbMappings.ObjectTypes);
        Assert.Contains("fpb:SystemLimit", FpbMappings.ObjectTypes);
    }

    [Fact]
    public void ConnectionTypes_ContainsFourTypes()
    {
        Assert.Equal(4, FpbMappings.ConnectionTypes.Count);
        Assert.Contains("fpb:Flow", FpbMappings.ConnectionTypes);
        Assert.Contains("fpb:Usage", FpbMappings.ConnectionTypes);
    }
}
