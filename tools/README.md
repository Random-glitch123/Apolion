# Release packaging

`Package-Release.ps1` builds the two assets the updater needs:

| File | Purpose |
| --- | --- |
| `Apolion-<version>-win-x64.zip` | Full portable build (the large download). |
| `update-manifest.json` | Tiny index of every file in the zip, including its SHA-256 and exact compressed byte range. |

The updater downloads the manifest first, hashes the installed files, and then
fetches only the changed zip entries with HTTP `Range` requests. If the manifest
is missing or invalid, the server ignores ranges, or the saving is too small, it
falls back to the full zip automatically. Either way the result is verified
before it is applied.

## Requirements

- Windows PowerShell 5.1 or newer.
- .NET 9 SDK for a normal build. `-SkipBuild` works without it.
- GitHub CLI only if you want to upload from the command line.

The version comes from `<Version>` in `Apolion.csproj` unless `-Version`
is passed. Bump that value before packaging a real release.

## Build a release

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Package-Release.ps1
```

Output defaults to `artifacts\release\`:

```text
artifacts\release\
  Apolion-1.0.1-win-x64.zip
  update-manifest.json
```

Useful options:

```powershell
# Package an existing build instead of running dotnet publish.
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Package-Release.ps1 `
  -SkipBuild -SourceDirectory bin\Debug\net9.0-windows\win-x64

# Choose the version and output folder explicitly.
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Package-Release.ps1 `
  -Version 1.0.1 -OutputDirectory artifacts\release-1.0.1
```

The script creates the zip, writes the manifest, and then validates every entry
before it reports success. Do not edit the zip after packaging: the manifest
records exact byte offsets and the archive hash.

## Validate an existing package

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Package-Release.ps1 `
  -ValidateOnly `
  -ZipPath artifacts\release\Apolion-1.0.1-win-x64.zip `
  -ManifestPath artifacts\release\update-manifest.json
```

`-ManifestPath` is optional in validation mode; it defaults to
`update-manifest.json` next to the zip. Validation checks:

- Every manifest offset and compressed size lies inside the archive.
- Every entry decompresses to the expected size and SHA-256.
- The archive SHA-256 matches `archiveSha256`.
- Paths are relative and cannot escape the install directory.

## Publish the GitHub release

1. Bump `<Version>` in `Apolion.csproj`.
2. Run the packaging script.
3. Create a GitHub release whose **name** contains the version, for example
   `Apolion v1.0.1`. The updater extracts the version from the release name;
   the tag itself can be any string (e.g. `Release` or `v1.0.1`).
4. Upload **both** `Apolion-<version>-win-x64.zip` and
   `update-manifest.json` as release assets.
5. Mark the release as a prerelease only when it should not reach stable users.
   Prereleases are skipped unless the installed version is itself a prerelease
   or `APOLION_ALLOW_PRERELEASE=1` is set.
6. Leave drafts unpublished. Draft releases are always ignored.

With the GitHub CLI:

```powershell
gh release create v1.0.1 `
  artifacts\release\Apolion-1.0.1-win-x64.zip `
  artifacts\release\update-manifest.json `
  --repo Random-glitch123/Apolion `
  --title "Apolion v1.0.1" `
  --notes-file changelog.md
```

Keep the zip name in the form `Apolion-<version>-win-x64.zip`. The updater
prefers x64 assets and rejects arm64, x86-only, PDB, source, debug, patch and
delta archives.

## What an update touches

The updater replaces application files and intentionally preserves user data:

- Settings, language choice, saved tabs and profile data stay in place.
- CEF cache directories are deleted so stale Chromium data cannot break startup.
- `eula_accepted.dat` is deleted so a changed EULA is shown again.

Obsolete files that are no longer part of the package are listed and removed by
the batch updater after the copy completes.

## Troubleshooting

- **Manifest not found:** the release only has the zip. The updater still works
  but downloads the full archive. Upload `update-manifest.json` to fix it.
- **Validation failed:** the zip was rebuilt, renamed or modified after the
  manifest was generated. Re-run the packaging script.
- **Update log:** failed apply steps are appended to
  `%TEMP%\Apolion_Update.log`.
- **Staging folder:** downloads are staged in `%TEMP%\Apolion_Update`
  and cleaned up after the update.
