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
