# FPB.JS Integration ins AML Editor Plugin — Plan & Spike-Ergebnisse

> Stand: 2026-06-06. Plugin v1.0.6 ist die Baseline (Mapper als ProjectReference, drei Bug-Fixes).
> Dieses Dokument hält den Plan für die Anzeige von FPB.JS-Diagrammen direkt im AML Editor fest,
> inkl. Phase-0-Spike-Ergebnissen.

## Ziel

FPB.JS (das Browser-Diagramm-Frontend) direkt im AML Editor anzeigen, idealerweise mit Rück-Synchronisation
(Änderungen im Diagramm landen im CAEXDocument). Ersetzt den heutigen "Export FPB.JS"-Toolbar-Workflow
durch ein eingebettetes Live-Panel.

## Tech-Stack

- **WebView2** (`Microsoft.Web.WebView2` NuGet) als WPF-Control im Plugin.
- **Virtual-Host-Mapping** (`SetVirtualHostNameToFolderMapping`) statt `file://` — umgeht Browser-Security
  für ES-Modules und mime-type-Probleme.
- **FPB.JS-Lib-Build** (`dist/fpbjs.esm.js`) lokal embedded — Offline-fähig, deterministische Version.
- **JS↔C#-Bridge** via `postMessage` / `WebMessageReceived`.

## Architektur-Skizze

```
AML Editor (WPF)
└── FpbPlugin (Aml.Editor.Plugin.Contract)
    ├── DockContent / DockContentMaximized        ← großes Panel
    │   └── WebView2 (Microsoft.Web.WebView2)
    │       └── https://fpbjs.local/index.html    ← Virtual Host
    │           └── createFpbModeler({ container })
    │               ├── importJSON(data)            ← from C#
    │               ├── on('changed', toJSON)       → to C#
    │               └── on('process.switched', id)  → to C#
    │
    └── Bridge: AML ⇄ FpbMapper.Conversion ⇄ JSON ⇄ WebMessage
```

## Datei-Struktur im Plugin

```
Aml.Editor.Plugin.FPB/
├── FpbPlugin.xaml                 (UI: TabControl mit Status + WebView)
├── FpbPlugin.xaml.cs              (existing + WebView-Wiring)
├── Bridge/
│   ├── FpbWebView.cs              (WebView2-Lifecycle, C#↔JS messaging)
│   └── BridgeMessage.cs           (DTOs: Ready / ImportJSON / Changed / Error)
└── fpbjs-assets/                  (← Build-Step kopiert aus FPB.JS\dist\)
    ├── index.html                 (Bootstrap-Wrapper, neu schreiben)
    ├── fpbjs.esm.js               (aus dist/, unverändert)
    └── css/                       (aus dist/css/)
```

## index.html (Skizze)

```html
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <link rel="stylesheet" href="./css/fpbjs.css">
  <style>html,body{margin:0;height:100%}#canvas{height:100%}</style>
</head>
<body>
  <div id="canvas"></div>
  <script type="module">
    import { createFpbModeler } from './fpbjs.esm.js';
    const fpb = await createFpbModeler({ container: document.getElementById('canvas') });

    window.chrome.webview.addEventListener('message', e => {
      const msg = e.data;
      if (msg.type === 'importJSON') {
        try { fpb.importJSON(msg.data); }
        catch (err) { post({ type: 'error', message: String(err) }); }
      }
    });

    fpb.on('changed', () => post({ type: 'changed', data: fpb.toJSON() }));
    fpb.on('process.switched', e => post({ type: 'processSwitched', id: e.selectedProcess?.id }));

    function post(msg) { window.chrome.webview.postMessage(msg); }
    post({ type: 'ready' });
  </script>
</body>
</html>
```

## Phasen-Aufteilung

### Phase 1: Read-only Anzeige (~3-4h)
- WebView2 NuGet hinzufügen, korrekt bundeln (siehe Spike-Erkenntnisse unten)
- `FpbWebView`-Bridge + `index.html` schreiben
- `INotifyAMLDocumentLoad`-Hook → `CaexToFpbJson.Convert` → `ImportJson(json)`
- DockPosition: `DockContent` oder `DockContentMaximized`
- Aktuelles Status-Panel als zweiter Tab beibehalten
- **Akzeptanz**: Öffne `.aml` mit FPD-Inhalt → Diagramm erscheint im FPB.JS-Tab

### Phase 2: Rück-Sync (~3-4h, mit ungelöstem Architektur-Problem)
- `OnDiagramChanged`-Event: Plugin empfängt JSON aus FPB.JS
- **`ImportInto` ist falsch für Edit-Sync** — hängt neue IH an statt bestehende zu ersetzen.
  Brauchen separate Methode `ReplaceFpdIn(CAEXDocument, FpbProject, entries)` die die
  existing FPD-IH identifiziert und ersetzt.
- **Heuristik "Welche IH ist die FPD-IH?"**: "alle IHs mit ≥1 FPD_Process" oder
  Project.name-Match. Ungelöst.
- **Editor-Save-Integration: ungelöst** (`MarkDirty` existiert nicht in Contract v4.3.0).
  Workarounds siehe unten.

### Phase 3: Konflikt-Handling + Selection-Sync (~2-3h)
- Parallel-Edits AML-Tree ↔ FPB.JS — wer gewinnt?
  - Option A: Plugin sperrt AML-Tree (radikal)
  - Option B: Diff + Merge-Dialog (komplex)
  - Option C: Last-write-wins mit Warning-Toast (pragmatisch)
- `ChangeSelectedObject` → `fpb.select(id)` (Achtung: ID-Format-Mismatch `{xxx}` ↔ `xxx`)
- FPB.JS-Selektion → AML-Tree fokussieren (Editor-API ungeprüft)
- **Undo/Redo**: Editor-CommandStack umgangen. Bewusste Lücke vorerst.

---

## Phase-0-Spike-Ergebnisse (2026-06-06)

### Was Phase 1 tragfähig macht ✓

| Erkenntnis | Quelle | Folge |
|---|---|---|
| `DockContent`, `DockContentMaximized`, `DockTop/Left/Right`, `Floating` existieren | `Aml.Editor.Plugin.Contract.dll` v4.3.0 (Stringtable) | Plugin kann zentrales Hauptpanel werden — kein UX-Kompromiss mit DockBottom |
| `INotifyAMLDocumentLoad` | Contract DLL | Hook für initialen Push |
| `INotifyAMLDocumentSaved` | Contract DLL | Hook für Persistenz-Sync |
| `INotifyViewActivation` | Contract DLL | Lazy WebView-Init beim Tab-Aktivieren |
| WebView2 NuGet hat alle DLLs | NuGet-Cache `microsoft.web.webview2/1.0.3967.48/` | 3 managed (Core, Wpf, .winmd) + 1 native (`runtimes/win-x64/native/WebView2Loader.dll`) |

### Was nicht-trivial ist (Lösung bekannt) ⚠️

| Problem | Lösung |
|---|---|
| Standard `<PackageReference Include="Microsoft.Web.WebView2" />` emittiert NuGet-Dep im nuspec → PlugIn Manager crasht beim Install ("Unable to resolve dependency") | Gleicher Trick wie heute mit `FpbMapper.Conversion`: `PrivateAssets=all` + explizite `<None Include="$(OutputPath)WebView2*.dll" Pack="true" PackagePath="lib\$(TargetFramework)" />` |
| Native `WebView2Loader.dll` muss neben managed DLLs landen, nicht in `runtimes/win-x64/native/` | Flach in `lib/$(TargetFramework)/` packen. Plugin lädt aus einem Ordner. |

### Echter Architektur-Schmerzpunkt ✗

**`MarkDirty` / `SetModified` / `INotifyDocumentChanged` existieren NICHT** in `Aml.Editor.Plugin.Contract` v4.3.0.

Bedeutet: Wenn Plugin das `_currentDocument` via FPB.JS-Änderungen modifiziert, weiß der Editor das nicht.
- Save-Button im Editor persistiert die Änderungen nicht
- Editor-Tree zeigt veralteten Stand
- User-Erwartung "Strg+S speichert" gilt nicht

**Workarounds für Phase 2:**

1. **Auto-Save (pragmatisch)**: Plugin schreibt direkt auf Platte bei FPB.JS-`changed`
   (mit Debounce). Funktioniert sicher. Tradeoff: User verliert die "geändert vs.
   nicht-gespeichert"-Kontrolle.
2. **CAEX-Engine-Events**: Vielleicht reagiert der Editor-Tree auf direkte CAEXDocument-Änderungen
   via Aml.Engine-Events. Empirisch zu testen.
3. **Upstream-Feature-Request**: `INotifyDocumentChanged` o.ä. bei den Aml.Editor-Maintainern
   anregen. Längerfristig.

### Aml.Editor.Plugin.Contract API-Auszug (für Referenz)

**DockPositionEnum-Werte:**
- `DockBottom`
- `DockContent`
- `DockContentMaximized`
- `DockLeft`
- `DockRight`
- `DockTop`
- `Floating`

**Implementierbare Interfaces (auszugsweise):**
- `IToolBarIntegration` (haben wir schon)
- `ISupportsThemes` (haben wir, leer)
- `INotifyAMLDocumentLoad`
- `INotifyAMLDocumentSaved`
- `INotifyViewActivation`
- `INotifyPropertyChanged` (Standard WPF)
- `IPluginLicense`

**PluginCommand-Properties (relevant für Toolbar-Buttons):**
- `CommandName` (String, identifier)
- `CommandButtonContent` (`FrameworkElement` — z. B. `TextBlock`. **Nicht** String, sonst leere Buttons)
- `Command` (`ICommand`)
- `CommandToolTip` (String)
- `CommandIcon` (`ImageSource`, optional)
- `IsCheckable` (bool)

## Bekannte Fallstricke (aus Session vom 2026-06-06)

| Fallstrick | Wie er heute gebissen hat |
|---|---|
| Plugin-DisplayName landet als WPF x:Name in Toolbars — `.` und `/` werfen `ArgumentException` | "FPB.JS Import/Export" → Crash. Lösung: nur Identifier-Zeichen verwenden ("FPB_js_Import_Export") |
| `PluginCommand.CommandButtonContent` ist `FrameworkElement`, nicht String | Ohne Setzen rendert die Toolbar **unsichtbar leere Buttons** |
| Plugin-Install-Cache: `%APPDATA%\AutomationMLEditor\PlugInManager6.xml` + `%LOCALAPPDATA%\AutomationML\AutomationMLEditor\config.xml` | Manuelles Editieren dieser Files zerschießt den State. Sauberer Weg: Uninstall + Install via PlugIn Manager UI. Source-Location muss auf den Ordner mit der `.nupkg` zeigen. |
| Plugin wird wirklich aus `%APPDATA%\AutomationMLEditor\PlugIns6\<Package>.<Version>\lib\<TFM>\` geladen — **nicht** aus `%APPDATA%\AutomationML\Plugins\` | "Plugins" mit "s" und ohne "Editor" ist ein anderer Ort, wird ignoriert |
| `<PluginActivation>`-Sektion in config.xml: Editor schreibt `<PLUGIN_<DisplayName>>True\|False</...>`. Bei `.`/`/` im DisplayName → XML-Element-Crash beim Save | Plugin-Identifier muss XML-Element-Name-konform sein |
| `ProjectReference` ohne `PrivateAssets=all` emittiert NuGet-Dependency im nuspec → PluginManager scheitert | `PrivateAssets=all` + Test-Projekt referenziert Mapper-csproj direkt (nicht transitiv) |
| `Microsoft.Web.WebView2` analog: gleicher Bug-Klasse | Spike-Ergebnis siehe oben |

## Mapper-Bugs gefixt (in FpbMapper.Conversion, Session 2026-06-06)

Alle drei in `CaexToFpbJson.cs`:

1. **`TechnicalResource` landete in `SystemLimit.elementsContainer`** — sollte stattdessen in
   `Process.elementsContainer`. FPB.JS-Importer rekursierte SL → TR doppelt konsumiert → Crash
   in `buildTRandUsage`.
2. **`Usage`-Flow analog** — gehört in Process-Container.
3. **Sub-Process `parent` war Self-Ref** (gleich Parent-PO-FPB-ID) — sollte auf die
   **Top-Process-FPB-ID** zeigen. FPB.JS Layer-Switch hing endlos. Fix benötigt
   Reverse-Lookup `PO AML-ID → Process AML-ID`.

Tests im Mapper sind **count-basiert** (Roundtrip preserves N elements) — semantische Bugs
wie Self-Refs fallen durch. Robustheit-Vorschlag: Test mit echter `VDI_FPD_DomainLibrary_v0.5.aml`
als Input + Assertions auf `parent`-Werte und `elementsContainer`-Inhalt.

## Versionierung

| Komponente | Version (Stand 2026-06-06) | Notiz |
|---|---|---|
| `Aml.Editor.Plugin.FPB` | 1.0.6 | Baseline mit Mapper-Bug-Fixes |
| `FpbMapper.Conversion` | (Mapper-Repo-Stand) | Plugin referenziert via ProjectReference |
| `FPB.JS` | 1.1.6 (laut package.json) | Lib-Build in `dist/` muss vor Plugin-Build aktuell sein |
| Online-Mapper (`aml.fpbjs.net`) | Deploy 2026-01-04 | **Veraltet**: enthält die heute gefixten Mapper-Bugs nicht |
