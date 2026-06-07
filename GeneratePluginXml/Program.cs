using System.Runtime.Serialization;
using System.Xml;

// Generate PlugInManager6.xml with correct DataContract serialization
// and verify round-trip deserialization works

var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var pluginsDir = Path.Combine(appData, "AutomationMLEditor", "PlugIns6", "Aml.Editor.Plugin.FPB");
var xmlPath = Path.Combine(appData, "AutomationMLEditor", "PlugInManager6.xml");

var model = new PluginDataModel
{
    ManagedPlugIns = new List<PluginViewModel>
    {
        new PluginViewModel
        {
            Name = "FPB.JS Import/Export",
            Author = "VDI 3682 Project",
            Description = "Import/Export between FPB.JS JSON and AutomationML FPD structures (VDI 3682)",
            Version = "v1.0.0",
            DirPath = pluginsDir,
            FilePath = Path.Combine(pluginsDir, "Aml.Editor.Plugin.FPB.dll"),
            FullName = "Aml.Editor.Plugin.FPB, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            Id = "Aml.Editor.Plugin.FPB",
            IsInstalled = true,
            MarkedToDelete = false,
            MarkedToUpdate = false,
            Owner = "VDI 3682 Project",
            Source = new PluginSourceViewModel
            {
                IsActive = true,
                Name = "FPB.js-AML-Mapper",
                SourceLocation = @"C:\Dev\02_VDI3682\AML\fpb-aml-mapper\editor-plugin\Aml.Editor.Plugin.FPB\build\Plugins\Aml.Editor.Plugin.FPB\Release"
            }
        }
    },
    PlugInSources = new List<PluginSourceViewModel>
    {
        new PluginSourceViewModel { IsActive = true, Name = "All", SourceLocation = null },
        new PluginSourceViewModel { IsActive = true, Name = "PublicSource", SourceLocation = "https://api.nuget.org/v3/index.json" },
        new PluginSourceViewModel { IsActive = true, Name = "FPB.js-AML-Mapper", SourceLocation = @"C:\Dev\02_VDI3682\AML\fpb-aml-mapper\editor-plugin\Aml.Editor.Plugin.FPB\build\Plugins\Aml.Editor.Plugin.FPB\Release" },
    },
    SelectedPluginSourceName = "FPB.js-AML-Mapper",
    Version = "7.0"
};

// === STEP 1: Write XML ===
var writeSettings = new XmlWriterSettings
{
    Indent = true,
    ConformanceLevel = ConformanceLevel.Document
};

var serializer = new DataContractSerializer(typeof(PluginDataModel));
using (var output = File.Open(xmlPath, FileMode.Create, FileAccess.ReadWrite))
using (var writer = XmlWriter.Create(output, writeSettings))
{
    serializer.WriteObject(writer, model);
}
Console.WriteLine($"Written to: {xmlPath}");

// === STEP 2: Read back (mimicking editor's XmlReaderWriter.ReadObject) ===
PluginDataModel? readBack = null;
using (var stream = new FileStream(xmlPath, FileMode.Open))
using (var reader = XmlDictionaryReader.CreateTextReader(stream, new XmlDictionaryReaderQuotas()))
{
    readBack = new DataContractSerializer(typeof(PluginDataModel)).ReadObject(reader, true) as PluginDataModel;
}

if (readBack == null)
{
    Console.WriteLine("ERROR: Deserialization returned null!");
    return;
}

// === STEP 3: Verify values ===
Console.WriteLine($"\nManagedPlugIns count: {readBack.ManagedPlugIns?.Count ?? 0}");
Console.WriteLine($"PlugInSources count: {readBack.PlugInSources?.Count ?? 0}");
Console.WriteLine($"Version: {readBack.Version}");
Console.WriteLine($"SelectedPluginSourceName: {readBack.SelectedPluginSourceName}");

if (readBack.ManagedPlugIns != null)
{
    foreach (var p in readBack.ManagedPlugIns)
    {
        Console.WriteLine($"\n--- Plugin ---");
        Console.WriteLine($"  Name: {p.Name}");
        Console.WriteLine($"  Id: {p.Id}");
        Console.WriteLine($"  IsInstalled: {p.IsInstalled}");
        Console.WriteLine($"  MarkedToDelete: {p.MarkedToDelete}");
        Console.WriteLine($"  MarkedToUpdate: {p.MarkedToUpdate}");
        Console.WriteLine($"  FilePath: {p.FilePath}");
        Console.WriteLine($"  DirPath: {p.DirPath}");
        Console.WriteLine($"  Version: {p.Version}");
        Console.WriteLine($"  Author: {p.Author}");
        Console.WriteLine($"  Description: {p.Description}");
        Console.WriteLine($"  Source: {p.Source?.Name} ({p.Source?.SourceLocation})");
    }
}

// === STEP 4: Simulate editor's RemoveAll logic ===
var countBefore = readBack.ManagedPlugIns?.Count ?? 0;
readBack.ManagedPlugIns?.RemoveAll(p => !p.IsInstalled);
var countAfter = readBack.ManagedPlugIns?.Count ?? 0;
Console.WriteLine($"\nRemoveAll(!IsInstalled): {countBefore} -> {countAfter}");

if (countAfter == 0 && countBefore > 0)
    Console.WriteLine("WARNING: Plugin was removed! IsInstalled was false after deserialization.");
else if (countAfter > 0)
    Console.WriteLine("OK: Plugin survived RemoveAll filter.");

// === STEP 5: Print the actual XML for inspection ===
Console.WriteLine("\n=== Generated XML ===");
Console.WriteLine(File.ReadAllText(xmlPath));


// ===== DataContract classes matching Aml.Editor.PlugInManager.ViewModels =====

[DataContract(Name = "PluginDataModel",
    Namespace = "http://schemas.datacontract.org/2004/07/Aml.Editor.PlugInManager.ViewModels")]
public class PluginDataModel
{
    [DataMember]
    public List<PluginViewModel> ManagedPlugIns { get; set; } = new();

    [DataMember]
    public List<PluginSourceViewModel> PlugInSources { get; set; } = new();

    [DataMember]
    public string? SelectedPluginSourceName { get; set; }

    [DataMember]
    public string? Version { get; set; }
}

[DataContract(Name = "PluginViewModel",
    Namespace = "http://schemas.datacontract.org/2004/07/Aml.Editor.PlugInManager.ViewModels")]
public class PluginViewModel
{
    [DataMember]
    public string? DirPath { get; set; }

    [DataMember]
    public string? FilePath { get; set; }

    [DataMember]
    public string? FullName { get; set; }

    [DataMember]
    public string? Id { get; set; }

    [DataMember]
    public bool IsInstalled { get; set; }

    [DataMember]
    public bool MarkedToDelete { get; set; }

    [DataMember]
    public bool MarkedToUpdate { get; set; }

    // In the real editor, this is PluginViewModel type, not string.
    // But when null, both serialize as i:nil="true" identically.
    [DataMember]
    public PluginViewModel? NewVersion { get; set; }

    [DataMember]
    public string? Owner { get; set; }

    [DataMember]
    public PluginSourceViewModel? Source { get; set; }

    [DataMember(Name = "Name", IsRequired = true, Order = 1)]
    public string Name { get; set; } = "";

    [DataMember(Name = "Author", IsRequired = true, Order = 2)]
    public string Author { get; set; } = "";

    [DataMember(Name = "Description", IsRequired = true, Order = 3)]
    public string Description { get; set; } = "";

    [DataMember(Name = "Version", IsRequired = true, Order = 4)]
    public string Version { get; set; } = "";
}

[DataContract(Name = "PluginSourceViewModel",
    Namespace = "http://schemas.datacontract.org/2004/07/Aml.Editor.PlugInManager.ViewModels")]
public class PluginSourceViewModel
{
    [DataMember]
    public bool IsActive { get; set; }

    [DataMember]
    public string? Name { get; set; }

    [DataMember]
    public string? SourceLocation { get; set; }
}
