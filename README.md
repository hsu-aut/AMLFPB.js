# AMLFPB.js

A plugin for the [AutomationML Editor](https://www.automationml.org/) that
embeds the [FPB.js](https://github.com/hamiedNabizada/FPB.js) modeller. It
allows editing of VDI 3682 Formalised Process Description (FPD) models stored
in AutomationML (CAEX 3.0) files directly inside the editor.

This is research-quality software. APIs are not yet stable.

## Features

* Renders one viewer tab per FPD InstanceHierarchy in the active document.
* Bidirectional live synchronisation between the AML tree and the diagram
  (about a two second latency for tree edits).
* Creation of new InstanceHierarchies via "+ New Process" and via JSON import.
* Structural validation against VDI 3682 Blatt 2 on every load and update.
* Confirmation dialog when an update would change a large number of elements
  or connections.
* Verbose diagnostics log under `%TEMP%\fpb-plugin\fpb-plugin-debug.log`.

## Requirements

* AutomationML Editor 6.4.0 or newer
* Windows with WebView2 runtime installed
* For building from source: .NET 8 SDK and Node.js 18+

## Installation

1. Download the latest `Aml.Editor.Plugin.FPB.<version>.nupkg` from the
   releases page.
2. In the AutomationML Editor, open the PlugIn Manager, add a local source
   pointing at the folder that contains the `.nupkg`, and install the package.
3. Restart the editor.

## Build from source

The plugin depends on two sibling repositories:

* [`fpb-aml-mapper`](https://github.com/hsu-aut/fpb-aml-mapper) — CAEX 3.0 ↔
  FPB.js JSON conversion library (.NET, Aml.Engine).
* [`FPB.js`](https://github.com/hamiedNabizada/FPB.js) — diagram-js based
  modeller library.

All three repositories need to be cloned next to each other:

```
<parent>/
├── fpb-aml-editor-plugin/
├── fpb-aml-mapper/
└── FPB.JS/
```

Build steps:

```powershell
cd FPB.JS
npm install
npm run build

cd ..\fpb-aml-editor-plugin
dotnet build Aml.Editor.Plugin.FPB.sln -c Release
```

The resulting `.nupkg` is written to
`build\Plugins\Aml.Editor.Plugin.FPB\Release\`.

## Usage

After installing the plugin and restarting the editor, open any AML document.
Each FPD InstanceHierarchy in the document appears as a tab inside the
"FPB.js Viewer" plugin tab.

Editing flow:

* Changes made in the FPB.js viewer are buffered. Click **Update
  InstanceHierarchy** in the tab toolbar to write them into the CAEX
  document. Save the document with `Ctrl+S` to persist them to disk.
* Changes made in the AML tree are picked up automatically by the viewer
  after a few seconds. The **Refresh from AML** button forces an immediate
  refresh.
* **Export JSON** writes the current IH content to a `.json` file.
* The editor toolbar offers **+ New Process** (creates an empty FPD IH) and
  **Import FPB.js** (loads a `.json` file into a new IH).

## Architecture

```
AutomationML Editor                AMLFPB.js                          FPB.js
                                   ─────────                          ──────
DocumentLoaded         ─►  FpbPlugin
ChangeSelectedObject   ─►  ├── IhView (one per IH)
                           │   ├── WebView2  ◄── importJSON ◄────────  Diagram
                           │   ├── Bridge                              + UI
                           │   ├── pending snapshot
                           │   └── hash-poll live sync
                           │
                           └── calls the mapper:
                               ├── FpbJsonToCaex.UpdateInPlace     ──► CAEX
                               ├── FpbJsonToCaex.ImportInto
                               ├── CaexToFpbJson.Convert            ◄── CAEX
                               └── Vdi3682Validator
```

The plugin itself only handles tab lifecycle, the host-to-JavaScript bridge,
diagnostics, and editor integration. The CAEX conversion and validation are
implemented in `fpb-aml-mapper`. The diagram surface, palette, layer panel,
and properties panel are provided by `FPB.js`.

## Known limitations

Two pieces of functionality use reflection or polling because they are not
exposed by the editor's plugin API:

* The auto-save trigger looks up `SaveAMLCommand` on
  `Application.Current.MainWindow.DataContext`. If the editor renames or
  restructures it, the plugin falls back to manual `Ctrl+S`.
* The AML-tree to viewer synchronisation polls a SHA-256 of the IH XML every
  two seconds, because Aml.Engine has no public change event.

Both code paths fail safely; failures are written to the log file and the
manual workflow keeps working.

## Configuration

Settings are persisted in
`%APPDATA%\AutomationMLEditor\FpbPlugin\settings.json`:

| Key | Default | Effect |
|---|---|---|
| `debug_logging` | `false` | Forwards the JavaScript console to the log file. |
| `auto_save_after_update` | `false` | Triggers the editor's save command after each Update InstanceHierarchy. |
| `run_vdi_validation` | `true` | Runs VDI 3682 structural checks automatically. |
| `confirm_large_updates` | `true` | Shows a confirmation dialog when an update would touch many elements or connections. |
| `update_safety_threshold` | `5` | Threshold for the confirmation dialog. |

The boolean toggles are exposed in the plugin's Diagnostics tab. The
threshold is currently edited in the JSON file directly.

## License

[MIT](LICENSE).
