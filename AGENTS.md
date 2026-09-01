# AGENTS.md

Guidance for AI coding agents working in this repository. Assumes no prior knowledge of the project.

## Project Overview

**CF Rez Manager** is a Windows WPF desktop tool for browsing, searching, previewing, extracting, and repacking LithTech / CrossFire `.rez` resource archives. It can also preview loose resource files in scanned folders. The same executable doubles as a command-line batch tool (OBJ/MTL export, CFG scan, CFG decode, standalone file preview).

- User-facing documentation: `README.md` (Chinese, primary) and `README.en.md` (English). Keep both in sync when changing documented behavior.
- Current version: **1.2.4** (bumped in `CFRezManager.csproj` `Version`/`AssemblyVersion`/`FileVersion`).
- GitHub Releases are bilingual (Chinese + English).

## Tech Stack

- **Language/Framework:** C# 12 / .NET 8, WPF (`UseWPF`), Windows Forms interop (`UseWindowsForms`, aliased as `Forms` where needed).
- **Target framework:** `net8.0-windows7.0` (`Nullable` and `ImplicitUsings` enabled). Single assembly, single namespace `CFRezManager` (file-scoped namespaces everywhere).
- **NuGet dependencies** (see `CFRezManager.csproj`):
  - `Fmod5Sharp` — FSB5 audio stream decoding for FMOD `.bank` files.
  - `NVorbis` + `NAudio` — OGG/MP3/WAV decode and playback.
  - `SharpCompress` — archive/compression helpers (LZMA etc.).
  - `GrindCore.SharpCompress` — referenced with `Aliases="GrindCoreSharpCompress"` (Zstandard support for CrossFire BIN images); access it via `extern alias` where used.
  - `System.Text.Encoding.CodePages` — legacy code pages for text resources.
- **Auxiliary script:** `tools/generate_readme_marquees.py` (Python 3 + Pillow) regenerates the animated contributor/supporter GIFs under `assets/readme/`. Not part of the build.

## Build, Run, and Test Commands

```powershell
dotnet build .\CFRezManager.csproj                    # Debug build
dotnet run --project .\CFRezManager.csproj            # launch GUI
dotnet publish .\CFRezManager.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish\out
```

Debug output lands in `bin\Debug\net8.0-windows7.0\CFRezManager.exe`. `assets\*.png` and `licenses\*.txt` are copied to output with `PreserveNewest`.

**There is no automated test project.** No test framework, no `dotnet test` target, no CI test step. Validation is manual: run the GUI or CLI against real REZ archives/extracted game assets, and `dotnet build` must succeed with no new warnings. When a decoder changes, re-verify against actual sample files (past releases cite full-sample validation, e.g. 2,671 BIN image samples). Game asset folders (`Pak-CF/`, `Extracted/`, `Output/`, `tools_downloads/`, `_analysis/`) are gitignored local scratch data — never commit them.

## Runtime Architecture

Entry point is `App/App.xaml.cs` `OnStartup`, which dispatches on command-line args:

- `--export-obj` → `Commands/LithTechObjExportCommand.cs` (LithTech model → OBJ/MTL + `_textures` folder).
- `--scan-cfg` → `Commands/CfgScanCommand.cs` (CFG texture-reference scan, TXT/CSV reports).
- `--decode-cfg` → `Commands/CfgDecodeCommand.cs` (CFG decode retry, failure classification).
- `--preview <path>` (see `Commands/PreviewTool.cs`) → open a standalone preview window for one file.
- No args → the main browser window (`UI/MainWindow.xaml(.cs)`).

CLI invocations switch `ShutdownMode` to `OnExplicitShutdown` and return an exit code.

### Module map

- `Archives/` — REZ format core: `RezArchiveReader` (parses header + encrypted directory tables into `RezArchive`/`RezDirectoryNode`/`RezFileNode`; supports CrossFire multi-volume archives like `rf017.rez` + `rf017_1.rez` + ... — each file entry's time slot carries the volume index, `DataOffset` is relative to that volume's start, and directory tables live in the base volume; read archive bytes via `RezArchiveReader.OpenFileData`, never `File.OpenRead(archive.FilePath)`), `RezArchiveWriter` (repacks folders; directory table re-encrypted, MD5s recomputed), `RezCrypto` (built-in XOR key table), `RezArchiveDirectoryCache` (disk cache of parsed directory trees, keyed by source path/size/mtime).
- `Explorer/` — `ExplorerItem` (unified view model for real folders/files and REZ nodes) and `ThumbnailDiskCache` (PNG thumbnail cache under `ThumbnailCache/` next to the executable, with legacy-cache migration).
- `Decoders/` — pure format decoders, grouped by kind:
  - `Compression/` shared `LzmaAloneDecoder` (normal, prefix, and streaming LZMA paths — reuse this, don't add parallel LZMA code).
  - `CrossFire/` LTC (XOR layer + LithTech decode), DAT, image BIN (16-byte header + Zstandard + BGRA32, plus CF10/XOR fallback), UI script BIN tables.
  - `Images/` DTX, TGA, DDS decoders and `DecodedImageExporter` (decodable images → PNG export).
  - `LithTech/` LTC native decoder, SPR sprites, world DAT; `LithTech/Models/` model decoding (`LtbModelParser` is the structural LTB parser — full header/pieces/skeleton/animation layout, preferred over the heuristic offset scanner in `LithTechModelDecoder` with silent fallback), part grouping, scene building, texture resolution (CFG index + DAT reference index), OBJ/MTL export, FBX 7.4 binary export (`LithTechFbxExporter` writes geometry/UVs/skeleton/skinning/animation stacks through the generic `FbxBinaryWriter`), thumbnail rendering.
  - `Audio/` audio metadata/Ogg→WAVE decoding and `DecodedAudioExporter` (LZMA-wrapped audio decompressed, Ogg → WAV on export), `Fmod/` (FMOD `.bank` + embedded FSB5), `Config/` (CFG text/binary-strip), `Text/`.
- `Preview/` — standalone preview windows: `Audio/` (player with track list, spectrum, FMOD bank progressive loading), `Image/`, `Model/` (Unity viewer embedding), `Text/`.
  - `Model/` hosts the model preview in an embedded Unity player process instead of WPF 3D: `UnityPreviewExporter` exports the decoded `LithTechModelDocument` to a temp OBJ/MTL/PNG set (via `LithTechObjExporter`) under `%TEMP%\CFRezManager\UnityPreview\<guid>\`, `UnityViewerHost` (`HwndHost`) launches the Unity exe with `-parentHWND` and forwards resize/focus, `UnityViewerLocator` resolves the exe. The Unity project lives outside this repo (currently `E:\UnityProject\RezView`, Unity 2022.3 + URP); its runtime scripts parse `--cfrez-model <path>` and load the OBJ with a free-fly camera. Build output goes to `tools\UnityModelViewer\CFRezModelViewer.exe`.
- `UI/` — `MainWindow` (browser, search index, export, repack), `SettingsWindow`, `ExportOptionsWindow` (per-format image export choice), `VirtualizingWrapPanel`.
- `App/` — startup glue: `UserSettings` (JSON at `%LocalAppData%\CFRezManager\settings.json`), `LocalizedText` (zh/en), `ThemeManager`/`AppTheme`, `WindowThemeHelper` (native title bar), `ImageExportOptions`.

### Data flow

`RezArchiveReader` → `ExplorerItem` tree → thumbnails via `Decoders/*` cached by `ThumbnailDiskCache` → double-click opens `Preview/*` windows; export uses `RezArchiveReader.ExtractFile` + `Decoders/Images/DecodedImageExporter` + `Decoders/Audio/DecodedAudioExporter`; repack uses `RezArchiveWriter`.

## Code Style Guidelines

- File-scoped `namespace CFRezManager;` in every file; nullable reference types on; implicit usings on.
- **Code, comments, and identifiers are in English.** UI strings are bilingual via `LocalizedText`: add a `["Key"] = ("中文", "English")` entry and read it with `LocalizedText.T("Key")` / `Format("Key", args)`. Windows that own local text (e.g. `SettingsWindow`, `MainWindow`) keep their own inline key→(Chinese, English) dictionaries — follow the local pattern of the window you touch.
- Theming is done by mutating `DynamicResource` brushes: light defaults live in `App.xaml`, dark overrides in `App/ThemeManager.cs`. New UI must bind `AppXxxBrush` keys as `DynamicResource`, never hardcode colors.
- WPF code-behind style (no MVVM framework); `MainWindow.xaml.cs` is large (~4k lines) — extend it carefully rather than restructuring it.
- Decoders are defensive by design: bounds-check every read, treat malformed data as "not decodable" and fall back to keeping the source bytes, never crash the app on bad input. Errors surface as localized messages.
- Settings persistence failures must be swallowed (see `UserSettings.Save`); cache migration/cleanup must never block startup.
- Keep changes minimal and match surrounding idioms; do not reformat or restructure unrelated code.

## External Tool Fallbacks and Environment Variables

Decoders prefer built-in paths and fall back to external executables found beside the app (`tools\` folder or `AppContext.BaseDirectory`) or via environment variables:

- `CFREZ_LTC_TO_LTA` → external LTC→LTA converter (fallback after built-in LTC decode).
- `CFREZ_MODEL_UNPACKER` / `tools\Model_Unpacker.exe` → LTB model conversion fallback.
- `tools\vgmstream\vgmstream-cli.exe` → FSB5 streams the built-in `Fmod5Sharp` path cannot decode.
- `CFREZ_UNITY_VIEWER` / `tools\UnityModelViewer\CFRezModelViewer.exe` → Unity model preview viewer (required for model preview; no built-in fallback).

External process calls have timeouts and best-effort temp-file cleanup — preserve that pattern.

## Release Process

- `.github/workflows/release.yml` runs on tags matching `v*`: `dotnet publish` (Release, `win-x64`, self-contained, single-file, compressed) on `windows-latest`, zips the output, and creates a GitHub Release with bilingual notes via `softprops/action-gh-release`.
- `Properties/PublishProfiles/FolderProfile.pubxml` is the Visual Studio folder-publish profile (Release, Any CPU).
- When releasing: bump the three version properties in `CFRezManager.csproj`, update both READMEs' changelog sections, and update the hardcoded release notes body in `release.yml` (it is not auto-generated).

## Security and Safety Considerations

- This tool parses untrusted binary game assets: keep all length/offset bounds checks intact, honor the existing maximum-decoded-size protections in the LZMA paths, and never execute decoded content.
- `RezCrypto.Keys` is a public, widely documented LithTech key table baked into the format — not a secret to protect.
- Do not read, commit, or transmit local game asset folders (`Pak-CF/`, `Extracted/`, `Output/`) or `tools_downloads/` — they are gitignored for a reason.
- The app writes only to user-chosen export paths, `%LocalAppData%\CFRezManager\settings.json`, `ThumbnailCache\` / `RezIndexCache\v1\` folders next to the executable, and the `%TEMP%\CFRezManager\UnityPreview\` temp folder (per-preview OBJ data, deleted on window close and swept on startup); keep new side effects within those locations.
