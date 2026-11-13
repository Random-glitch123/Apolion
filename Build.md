# Building Apolion

This document covers every build step, what each tool produces, and how to make a full release.

## Build tools

| Tool | Purpose | Output |
|------|---------|--------|
| `tools\Package-Release.ps1` | Publish the app, zip it, write `update-manifest.json`, validate the pair. | `artifacts\release\Apolion-<version>-win-x64.zip` + `update-manifest.json` |
| `tools\Build-Installer.ps1` | Publish the app and compile the Inno Setup installer. | `artifacts\release\Apolion-<version>-setup.exe` |
| `tools\Apolion.iss` | Inno Setup script (source for the installer). | — |

## Requirements

- Windows PowerShell 5.1 or newer.
- .NET 9 SDK (`dotnet` on PATH).
- Inno Setup 5 or 6 (`ISCC.exe` on PATH or installed to Program Files) — only needed for the installer `.exe`, not for the portable zip.
- GitHub CLI (`gh`) — optional, only if you want to upload releases from the command line.

## Codec-enabled CEF runtime (required for video)

CefSharp's stock CEF binaries are built without proprietary codecs, so H.264/AAC
video (Instagram, YouTube, most MP4 sites) cannot play. Apolion therefore uses a
codec-enabled CEF runtime that is byte-compatible with CefSharp 154.0.340:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Fetch-CefCodecs.ps1
```

The script downloads a pinned CEF `154.0.34` build (compiled with
`proprietary_codecs=true` and `ffmpeg_branding="Chrome"`), verifies its SHA-256,
and extracts the required files to `third_party\cef-codecs\`. That folder is
ignored by git; run the script once per clone. If it is missing, the build emits
a warning and video playback will fail with "Sorry, we're having trouble playing
this video."

`Apolion.csproj` copies the runtime into the build output
(`ApplyCodecEnabledCef`) and into `dotnet publish` output
(`ApplyCodecEnabledCefPublish`). Keep the `CefSharp.*` package versions at
`154.0.340` when updating the runtime: CEF, CefSharp and the codec build must all
match.

> **Licensing:** H.264/AAC are covered by patent pools and this third-party CEF
> build ships without a patent license. Review the upstream project and your
> obligations before redistributing Apolion. See
> `third_party\cef-codecs\PROVENANCE.txt` after running the script.

### Debug media diagnostics

Debug builds include `MediaDiagnostics` (compiled out of Release via `#if DEBUG`).
It logs codec probes, `<video>` playback state and media failures to
`%LOCALAPPDATA%\Apolion\Logs\debug.log` with `[MEDIA-DIAG]`, `[MEDIA-STATE]` and
`[MEDIA-ERROR]` prefixes. To inspect a live page through the Chrome DevTools
Protocol:

```powershell
$env:APOLION_MEDIA_DEBUG = '1'
dotnet run -c Debug
# then open http://127.0.0.1:9222/json
```

Release builds contain none of these strings or hooks.

### Build configurations

`dotnet build -c <name>` accepts any configuration name. `Debug` and `Release`
are conventions, not an enum: only the `Debug` configuration defines the
`DEBUG` compiler symbol. A name such as `Test` still builds successfully; it
outputs to `bin\Test` and does not define `DEBUG`, so the media diagnostics above
are absent. Release additionally disables PDB generation in `Apolion.csproj`.

## 1. Portable zip (existing flow — unchanged)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Package-Release.ps1
```

This publishes `Apolion.csproj` (Release, win-x64, self-contained), creates the zip, generates `update-manifest.json` with per-file SHA-256/CRC-32/byte ranges, and validates everything.

Output goes to `artifacts\release\`:

```
artifacts\release\
  Apolion-1.0.1-win-x64.zip
  update-manifest.json
```

## 2. Installer .exe (new — no admin)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Build-Installer.ps1
```

This publishes the app, copies it to a staging folder, and compiles `tools\Apolion.iss` with Inno Setup.

Output:

```
artifacts\release\Apolion-1.0.1-setup.exe
```

The installer:
- Installs per-user to `%LOCALAPPDATA%\Apolion\` — **no admin elevation ever** (`PrivilegesRequired=lowest`).
- Shows the EULA license page (acceptance required to install).
- Creates a Start Menu shortcut and an automatic uninstaller.
- Launches Apolion after install (optional, can be cancelled).

If Inno Setup isn't found, the script stops after publishing and tells you where the `.iss` script and staging folder are. Install Inno Setup (per-user is fine, no admin needed) and re-run.

## 3. Full release

For each release, ship all three files on GitHub:

1. Bump `<Version>` in `Apolion.csproj`.
2. Run the zip builder:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Package-Release.ps1
   ```
3. Run the installer builder:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Build-Installer.ps1
   ```
4. Create a GitHub release whose **name** contains the version (e.g. `Apolion v1.0.1`).
5. Upload all three assets:
   - `Apolion-<version>-win-x64.zip`
   - `Apolion-<version>-setup.exe`
   - `update-manifest.json`

## 4. Validate an existing package

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Package-Release.ps1 `
  -ValidateOnly `
  -ZipPath artifacts\release\Apolion-1.0.1-win-x64.zip `
  -ManifestPath artifacts\release\update-manifest.json
```
