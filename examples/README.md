# Showcase-AMLs

Drei AML-Dokumente die das FPB-Plugin am praktischen Beispiel zeigen — Live-Demo, Slide-Material und Mapper-Round-Trip-Stress-Test in einem.

| Datei | Domäne | Layer | Multi-IH | Custom-Attribute |
|---|---|---|---|---|
| `Showcase-A-WaermetauscherMitRegelung.aml` | Verfahrenstechnik (Wärmeübertragung mit Druckregelung) | 4 | FPD_Heating + Plant_Control_Signals (Cross-IH-Links auf PIC_4711, TT_8042) | SollTemperatur, AuslegungsLeistung, PlattenAnzahl, MaxStellgeschwindigkeit |
| `Showcase-B-PharmaChargeUndReinigung.aml` | Pharma (Charge + CIP-Reinigung, geteilter Mixer) | 3 | FPD_Batch_Production + FPD_Cleaning, beide nutzen `Mixer_1` (gleiche `uniqueIdent`) | SafetyClass, BatchId, EquipmentId, LastCleaningStatus, Genauigkeit, Drehzahl |
| `Showcase-C-CncRobotikMontage.aml` | Maschinenbau (CNC-Fräsen + Robotik-Montage) | 3 (zwei Layer-2-Branches) | FPD_Assembly + FPD_Fasteners_Stock | MaxDrehzahl, WerkzeugAufnahme, MaxTraglast, Reichweite, Anziehmoment, Zykluszeit |

## Generieren

Die AMLs werden aus dem Mapper-Test-Projekt programmatisch erzeugt — kein Hand-XML, jeder Build kann sie identisch reproduzieren:

```powershell
cd c:\Dev\02_VDI3682\AML\fpb-aml-mapper\dotnet
dotnet test FpbMapper.Tests --filter "FullyQualifiedName~Showcase"
```

Die Generator-Klasse `FpbMapper.Tests.Showcases.ShowcaseBuilder` baut FPB.js-Format-JSON, der bestehende `FpbJsonToCaex.Convert` macht daraus CAEX, die Cross-IH-Konstrukte werden anschließend per `Aml.Engine` API ergänzt. Output-Pfad ist dieses Verzeichnis (überschreibbar via env `SHOWCASE_OUTPUT_DIR`).

## Coverage-Artefakte

`vdi3682-coverage.csv` und `vdi3682-coverage.json` listen die 60 Regeln des VDI-3682-Blatt-3-Regelkatalogs gegen die heute ausgelieferte Validator-Infrastruktur:

| Bucket | Anzahl |
|---|---|
| `PURE_OCL_LIVE` | 26 |
| `PHASE2_OCL_PENDING` | 4 |
| `OPEN_PLUGIN_GEOMETRY` | 11 |
| `OPEN_MAPPER_SCHEMA` | 4 |
| `OPEN_PHASE2_OR_INFORMATIVE` | 15 |
| **Gesamt** | **60** |

Quelle für die Slide auf der AML-Konferenz (FEAT-16). Regeneriert sich beim Test-Lauf — Drift zwischen Snapshot-Tabelle und shipping OCL-Files würde die `CoverageReportTests` zum Failen bringen.

## Spec / Hintergrund

Detail-Konzept inkl. Decomposition-Plan, Demo-Punkte pro Showcase und Bauplan: → `FPB.JS_Docs/Geplante-Features/022-Showcase-AMLs.md`.
