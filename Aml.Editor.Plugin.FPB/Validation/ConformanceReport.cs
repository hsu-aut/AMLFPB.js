using System.Text.Json;
using System.Text.Json.Serialization;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;

namespace Aml.Editor.Plugin.FPB.Validation;

/// <summary>
/// Machine-readable summary of a VDI 3682 validation pass. Suitable for CI
/// uploads, paper appendices, audit logs. The shape is intentionally flat
/// (per-finding rows + a small summary block) so downstream consumers can
/// stream over it without schema validation.
/// </summary>
public sealed class ConformanceReport
{
    [JsonPropertyName("schema")]              public string Schema { get; init; } = "amlfpbjs.vdi3682.conformance/v1";
    [JsonPropertyName("generated_at")]        public string GeneratedAt { get; init; } = "";
    [JsonPropertyName("library_version")]     public string LibraryVersion { get; init; } = "";
    [JsonPropertyName("document_summary")]    public DocumentSummary Document { get; init; } = new();
    [JsonPropertyName("rule_set")]            public List<RuleDescriptor> RuleSet { get; init; } = new();
    [JsonPropertyName("findings")]            public List<FindingRow> Findings { get; init; } = new();
    [JsonPropertyName("totals")]              public TotalsBlock Totals { get; init; } = new();

    public sealed class DocumentSummary
    {
        [JsonPropertyName("instance_hierarchy_count")] public int InstanceHierarchyCount { get; init; }
        [JsonPropertyName("fpd_ih_count")]             public int FpdInstanceHierarchyCount { get; init; }
        [JsonPropertyName("process_count")]            public int ProcessCount { get; init; }
        [JsonPropertyName("process_operator_count")]   public int ProcessOperatorCount { get; init; }
    }

    public sealed class RuleDescriptor
    {
        [JsonPropertyName("id")]      public string Id { get; init; } = "";
        [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    }

    public sealed class FindingRow
    {
        [JsonPropertyName("rule_id")]   public string RuleId { get; init; } = "";
        [JsonPropertyName("severity")]  public string Severity { get; init; } = "";
        [JsonPropertyName("message")]   public string Message { get; init; } = "";
        [JsonPropertyName("target_ie")] public string? TargetIeId { get; init; }
    }

    public sealed class TotalsBlock
    {
        [JsonPropertyName("errors")]   public int Errors { get; init; }
        [JsonPropertyName("warnings")] public int Warnings { get; init; }
        [JsonPropertyName("infos")]    public int Infos { get; init; }
        [JsonPropertyName("clean")]    public bool Clean { get; init; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });
}

/// <summary>
/// Build a <see cref="ConformanceReport"/> from a CAEX document.
/// </summary>
public static class ConformanceReportBuilder
{
    /// <summary>
    /// Build a report using the default rule set. <paramref name="generatedAt"/>
    /// is injected so callers control reproducibility (tests can fix it; runtime
    /// passes <see cref="DateTimeOffset.UtcNow"/>).
    /// </summary>
    public static ConformanceReport Build(CAEXDocument doc, DateTimeOffset generatedAt, ValidationOptions? options = null)
    {
        options ??= ValidationOptions.Default;
        var findings = Vdi3682Validator.ValidateStructured(doc, options);

        var ihCount   = doc?.CAEXFile?.InstanceHierarchy.Count ?? 0;
        var fpdIhList = doc != null
            ? CaexToFpbJson.FindFpdInstanceHierarchies(doc).ToList()
            : new List<InstanceHierarchyType>();
        var processCount  = 0;
        var poCount       = 0;
        foreach (var ih in fpdIhList)
        {
            foreach (var proc in ih.InternalElement.Where(ie =>
                ie.RefBaseSystemUnitPath == FpbMappings.ElementToSuc[FpbTypes.Process]))
            {
                processCount++;
                poCount += proc.InternalElement.Count(ie =>
                    FpbMappings.SucToElement.GetValueOrDefault(ie.RefBaseSystemUnitPath ?? "", "") == FpbTypes.ProcessOperator);
            }
        }

        return new ConformanceReport
        {
            GeneratedAt = generatedAt.ToString("o"),
            LibraryVersion = FpbMappings.LibNames.Version,
            Document = new ConformanceReport.DocumentSummary
            {
                InstanceHierarchyCount   = ihCount,
                FpdInstanceHierarchyCount = fpdIhList.Count,
                ProcessCount              = processCount,
                ProcessOperatorCount      = poCount,
            },
            RuleSet = Vdi3682Validator.DefaultRules.Select(r => new ConformanceReport.RuleDescriptor
            {
                Id      = r.Id,
                Enabled = !options.IsRuleDisabled(r.Id),
            }).ToList(),
            Findings = findings.Select(f => new ConformanceReport.FindingRow
            {
                RuleId   = f.RuleId,
                Severity = f.Severity.ToString(),
                Message  = f.Message,
                TargetIeId = f.TargetIeId,
            }).ToList(),
            Totals = new ConformanceReport.TotalsBlock
            {
                Errors   = findings.Count(f => f.Severity == ValidationSeverity.Error),
                Warnings = findings.Count(f => f.Severity == ValidationSeverity.Warning),
                Infos    = findings.Count(f => f.Severity == ValidationSeverity.Info),
                Clean    = findings.Count == 0,
            },
        };
    }
}
