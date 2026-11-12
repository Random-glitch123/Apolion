#Requires -Version 5.1
<#
.SYNOPSIS
Builds a release zip and the update-manifest.json used by Apolion's
delta updater.

.DESCRIPTION
The manifest records the exact compressed byte range of every file inside the
release zip. The updater downloads the tiny manifest first, hashes the locally
installed files, and then fetches only changed entries with HTTP Range requests.
If the manifest or range requests are unavailable, the updater automatically
falls back to the normal full-zip download.

The GitHub release must contain BOTH files produced by this script:
  - Apolion-<version>-win-x64.zip
  - update-manifest.json

.PARAMETER SkipBuild
Use an existing publish/build output instead of running dotnet publish.

.PARAMETER SourceDirectory
Directory to package. Defaults to a fresh dotnet publish output.

.PARAMETER ValidateOnly
Validates an existing zip/manifest pair without building anything.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\Package-Release.ps1

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\Package-Release.ps1 `
  -SkipBuild -SourceDirectory bin\Debug\net9.0-windows\win-x64

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\Package-Release.ps1 `
  -ValidateOnly -ZipPath artifacts\release\Apolion-1.0.1-win-x64.zip `
  -ManifestPath artifacts\release\update-manifest.json
#>

[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [string]$SourceDirectory,
    [switch]$SkipBuild,
    [switch]$ValidateOnly,
    [string]$ZipPath,
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$script:ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:ProjectFile = Join-Path $script:ProjectRoot 'Apolion.csproj'

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($Bytes)
    }
    finally {
        $sha.Dispose()
    }

    return (-join ($hash | ForEach-Object { $_.ToString('x2') }))
}

function Get-FileSha256Hex {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ZipEntries {
    <#
      Reads the zip central directory and returns each file's compression
      method, CRC, sizes, local header offset and compressed-data offset.
      Zip64 is intentionally rejected: a normal Apolion release is far below
      the 4 GB / 65535-entry limits.
    #>
    param([string]$Path)

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)

    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        try {
            $length = $stream.Length
            $tailLength = [Math]::Min(65557, $length)
            $stream.Position = $length - $tailLength
            $tail = $reader.ReadBytes([int]$tailLength)

            $eocdOffset = -1
            for ($index = $tail.Length - 22; $index -ge 0; $index--) {
                if ([BitConverter]::ToUInt32($tail, $index) -eq 0x06054B50) {
                    $eocdOffset = $index
                    break
                }
            }

            if ($eocdOffset -lt 0) {
                throw "End of central directory not found in '$Path'."
            }

            $entryCount = [BitConverter]::ToUInt16($tail, $eocdOffset + 10)
            $centralDirectorySize = [BitConverter]::ToUInt32($tail, $eocdOffset + 12)
            $centralDirectoryOffset = [BitConverter]::ToUInt32($tail, $eocdOffset + 16)

            if ($entryCount -eq 0xFFFF -or
                $centralDirectorySize -eq 0xFFFFFFFF -or
                $centralDirectoryOffset -eq 0xFFFFFFFF) {
                throw "Zip64 archives are not supported by this packaging script."
            }

            $entries = [System.Collections.Generic.List[object]]::new()
            $stream.Position = [long]$centralDirectoryOffset

            for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
                $signature = $reader.ReadUInt32()
                if ($signature -ne 0x02014B50) {
                    throw "Invalid central directory signature at entry $entryIndex."
                }

                $null = $reader.ReadUInt16() # version made by
                $null = $reader.ReadUInt16() # version needed
                $null = $reader.ReadUInt16() # flags
                $method = $reader.ReadUInt16()
                $null = $reader.ReadUInt16() # modification time
                $null = $reader.ReadUInt16() # modification date
                $crc32 = $reader.ReadUInt32()
                $compressedSize = $reader.ReadUInt32()
                $uncompressedSize = $reader.ReadUInt32()
                $nameLength = $reader.ReadUInt16()
                $extraLength = $reader.ReadUInt16()
                $commentLength = $reader.ReadUInt16()
                $null = $reader.ReadUInt16() # disk number start
                $null = $reader.ReadUInt16() # internal attributes
                $null = $reader.ReadUInt32() # external attributes
                $localHeaderOffset = $reader.ReadUInt32()

                $nameBytes = $reader.ReadBytes($nameLength)
                $name = [System.Text.Encoding]::UTF8.GetString($nameBytes).Replace('\', '/')
                if ($extraLength -gt 0) { $null = $reader.ReadBytes($extraLength) }
                if ($commentLength -gt 0) { $null = $reader.ReadBytes($commentLength) }

                if ($compressedSize -eq 0xFFFFFFFF -or
                    $uncompressedSize -eq 0xFFFFFFFF -or
                    $localHeaderOffset -eq 0xFFFFFFFF) {
                    throw "Zip64 file entries are not supported by this packaging script."
                }

                if (-not $name.EndsWith('/')) {
                    $returnPosition = $stream.Position
                    $stream.Position = [long]$localHeaderOffset
                    $localSignature = $reader.ReadUInt32()
                    if ($localSignature -ne 0x04034B50) {
                        throw "Invalid local header signature for '$name'."
                    }

                    $null = $reader.ReadUInt16() # version needed
                    $null = $reader.ReadUInt16() # flags
                    $null = $reader.ReadUInt16() # method (duplicate of central directory)
                    $null = $reader.ReadUInt16() # modification time
                    $null = $reader.ReadUInt16() # modification date
                    $null = $reader.ReadUInt32() # crc (duplicate)
                    $null = $reader.ReadUInt32() # compressed size (duplicate)
                    $null = $reader.ReadUInt32() # uncompressed size (duplicate)
                    $localNameLength = $reader.ReadUInt16()
                    $localExtraLength = $reader.ReadUInt16()

                    $dataOffset = [long]$localHeaderOffset + 30 + $localNameLength + $localExtraLength
                    $stream.Position = $returnPosition

                    $entries.Add([pscustomobject]@{
                        Name             = $name
                        Method           = [int]$method
                        Crc32            = [uint32]$crc32
                        CompressedSize   = [long]$compressedSize
                        UncompressedSize = [long]$uncompressedSize
                        DataOffset       = $dataOffset
                    })
                }
            }

            return $entries.ToArray()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Read-CompressedBytes {
    param(
        [string]$Path,
        [long]$Offset,
        [long]$Size
    )

    if ($Size -gt [int]::MaxValue) {
        throw "Compressed entry is too large for this validation script."
    }

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)

    try {
        $stream.Position = $Offset
        $buffer = [byte[]]::new([int]$Size)
        $totalRead = 0

        while ($totalRead -lt $buffer.Length) {
            $read = $stream.Read($buffer, $totalRead, $buffer.Length - $totalRead)
            if ($read -le 0) {
                throw "Unexpected end of zip while reading a compressed entry."
            }
            $totalRead += $read
        }

        return ,$buffer
    }
    finally {
        $stream.Dispose()
    }
}

function Expand-CompressedBytes {
    param(
        [byte[]]$Data,
        [int]$Method
    )

    if ($Method -eq 0) {
        return ,$Data
    }

    if ($Method -ne 8) {
        throw "Unsupported zip compression method $Method."
    }

    $input = [System.IO.MemoryStream]::new($Data)
    $output = [System.IO.MemoryStream]::new()
    $deflate = [System.IO.Compression.DeflateStream]::new(
        $input,
        [System.IO.Compression.CompressionMode]::Decompress)

    try {
        $deflate.CopyTo($output)
        return ,$output.ToArray()
    }
    finally {
        $deflate.Dispose()
        $output.Dispose()
        $input.Dispose()
    }
}

function Test-UpdateManifest {
    param(
        [string]$ArchivePath,
        [string]$ManifestPath
    )

    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        throw "Zip not found: $ArchivePath"
    }
    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        throw "Manifest not found: $ManifestPath"
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1) {
        throw "Unsupported manifest schema: $($manifest.schema)"
    }
    if (-not $manifest.files -or @($manifest.files).Count -eq 0) {
        throw "Manifest contains no files."
    }

    $zipLength = (Get-Item -LiteralPath $ArchivePath).Length
    $checked = 0

    foreach ($file in @($manifest.files)) {
        $relativePath = [string]$file.path
        try {
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath -match '(^|[\\/])\.\.([\\/]|$)') {
            throw "Unsafe manifest path: $relativePath"
        }

        $offset = [long]$file.offset
        $compressedSize = [long]$file.compressedSize
        $expectedSize = [long]$file.size

        if ($offset -lt 0 -or $compressedSize -lt 0 -or
            ($offset + $compressedSize) -gt $zipLength) {
            throw "Manifest range is outside the archive for '$relativePath'."
        }

        $compressed = Read-CompressedBytes -Path $ArchivePath -Offset $offset -Size $compressedSize
        $data = Expand-CompressedBytes -Data $compressed -Method ([int]$file.method)

        if ($data.Length -ne $expectedSize) {
            throw "Size mismatch for '$relativePath': expected $expectedSize, got $($data.Length)."
        }

        $actualHash = Get-Sha256Hex -Bytes $data
        if ($actualHash -ne ([string]$file.sha256).ToLowerInvariant()) {
            throw "SHA-256 mismatch for '$relativePath'."
        }

        $checked++
        }
        catch {
            throw "Validation failed for '$relativePath': $($_.Exception.Message)"
        }
    }

    $archiveHash = Get-FileSha256Hex -Path $ArchivePath
    if (-not [string]::IsNullOrWhiteSpace($manifest.archiveSha256) -and
        $archiveHash -ne ([string]$manifest.archiveSha256).ToLowerInvariant()) {
        throw "Archive SHA-256 in the manifest does not match the zip."
    }

    Write-Host "Validation passed: $checked file(s), archive $([Math]::Round($zipLength / 1MB, 2)) MB." -ForegroundColor Green
}

function Get-ProjectVersion {
    [xml]$project = Get-Content -LiteralPath $script:ProjectFile -Raw
    $candidate = $project.Project.PropertyGroup |
        ForEach-Object { $_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw "Could not read <Version> from $script:ProjectFile."
    }

    return ([string]$candidate).Trim().TrimStart('v', 'V')
}

# ── Validation-only mode ────────────────────────────────────────────────
if ($ValidateOnly) {
    if ([string]::IsNullOrWhiteSpace($ZipPath)) {
        throw "-ValidateOnly requires -ZipPath."
    }
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        $ManifestPath = Join-Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($ZipPath))) 'update-manifest.json'
    }

    Write-Step "Validating update package"
    Test-UpdateManifest -ArchivePath ([System.IO.Path]::GetFullPath($ZipPath)) -ManifestPath ([System.IO.Path]::GetFullPath($ManifestPath))
    return
}

# ── Build / package mode ────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $script:ProjectRoot 'artifacts\release'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion
}
$Version = $Version.Trim().TrimStart('v', 'V')

if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    if ($SkipBuild) {
        throw "-SkipBuild requires -SourceDirectory."
    }

    $SourceDirectory = Join-Path $OutputDirectory "publish\$Runtime"
    if (Test-Path -LiteralPath $SourceDirectory) {
        Remove-Item -LiteralPath $SourceDirectory -Recurse -Force
    }

    Write-Step "Publishing Apolion $Version ($Configuration / $Runtime)"
    & dotnet publish $script:ProjectFile `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -o $SourceDirectory `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
else {
    $SourceDirectory = [System.IO.Path]::GetFullPath($SourceDirectory)
    if (-not (Test-Path -LiteralPath $SourceDirectory)) {
        throw "Source directory not found: $SourceDirectory"
    }
}

$appExe = Join-Path $SourceDirectory 'Apolion.exe'
if (-not (Test-Path -LiteralPath $appExe)) {
    throw "Apolion.exe was not found in '$SourceDirectory'."
}

# ── Build and include the dedicated updater ──────────────────────────────
Write-Step "Publishing Apolion Updater"
$updaterProject = Join-Path $script:ProjectRoot 'Apolion.Updater\Apolion.Updater.csproj'
$updaterOutput = Join-Path $SourceDirectory 'Apolion Updater.exe'
& dotnet publish $updaterProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o (Split-Path $updaterOutput) `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for Apolion Updater failed with exit code $LASTEXITCODE."
}

# Verify updater was created with correct name
if (-not (Test-Path -LiteralPath $updaterOutput)) {
    # Try to find it (sometimes single-file publish has different behavior)
    $found = Get-ChildItem -Path (Split-Path $updaterOutput) -Filter "Apolion Updater*" -Recurse | Select-Object -First 1
    if ($found) {
        Move-Item -LiteralPath $found.FullName -Destination $updaterOutput -Force
    }
    else {
        throw "Apolion Updater.exe was not created at '$updaterOutput'."
    }
}

$archiveName = "Apolion-$Version-$Runtime.zip"
$archivePath = Join-Path $OutputDirectory $archiveName
$manifestPath = Join-Path $OutputDirectory 'update-manifest.json'

foreach ($oldPath in @($archivePath, $manifestPath)) {
    if (Test-Path -LiteralPath $oldPath) {
        Remove-Item -LiteralPath $oldPath -Force
    }
}

Write-Step "Creating $archiveName"
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $SourceDirectory,
    $archivePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

$entries = @(Get-ZipEntries -Path $archivePath)
if ($entries.Count -eq 0) {
    throw "The zip contains no files."
}

Write-Step "Writing update-manifest.json"
$files = [System.Collections.Generic.List[object]]::new()

foreach ($entry in $entries) {
    $sourcePath = Join-Path $SourceDirectory ($entry.Name.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Zip entry '$($entry.Name)' has no corresponding source file."
    }

    $sourceLength = (Get-Item -LiteralPath $sourcePath).Length
    if ($sourceLength -ne $entry.UncompressedSize) {
        throw "Source size mismatch for '$($entry.Name)'."
    }

    $files.Add([ordered]@{
        path           = [string]$entry.Name
        size           = [long]$entry.UncompressedSize
        sha256         = (Get-FileSha256Hex -Path $sourcePath)
        crc32          = [uint32]$entry.Crc32
        offset         = [long]$entry.DataOffset
        compressedSize = [long]$entry.CompressedSize
        method         = [int]$entry.Method
    })
}

$manifest = [ordered]@{
    schema        = 1
    version       = [string]$Version
    platform      = [string]$Runtime
    archiveName   = [string]$archiveName
    archiveSize   = [long](Get-Item -LiteralPath $archivePath).Length
    archiveSha256 = (Get-FileSha256Hex -Path $archivePath)
    files         = [object[]]$files.ToArray()
}

$json = $manifest | ConvertTo-Json -Depth 8
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($manifestPath, $json, $utf8NoBom)

Write-Step "Validating package"
Test-UpdateManifest -ArchivePath $archivePath -ManifestPath $manifestPath

Write-Host ""
Write-Host "Release package ready:" -ForegroundColor Green
Write-Host "  Zip      : $archivePath"
Write-Host "  Manifest : $manifestPath"
Write-Host ""
Write-Host "Upload BOTH files as assets on the GitHub release."
Write-Host "Name the release something like 'Apolion v$Version' so the updater can extract the version from the release name."
Write-Host "The updater will download the manifest and fetch only changed zip entries."
Write-Host "If the manifest is missing, it falls back to the full zip automatically."
