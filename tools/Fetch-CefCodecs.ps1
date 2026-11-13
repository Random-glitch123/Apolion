#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads the codec-enabled CEF runtime used by Apolion.

.DESCRIPTION
    CefSharp's official CEF binaries are built without proprietary codecs, so
    H.264/AAC video (Instagram, YouTube, most MP4 sites) cannot play. This script
    downloads a matching CEF 154.0.34 build compiled with:
        proprietary_codecs=true
        ffmpeg_branding="Chrome"
        is_official_build=true

    The files are extracted to third_party\cef-codecs and copied into the build
    output by the ApplyCodecEnabledCef target in Apolion.csproj.

    The runtime is pinned by SHA-256 and is ignored by git; run this script once
    per clone (and after cleaning third_party).

.PARAMETER Force
    Re-download and replace an existing runtime even if it is already valid.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Destination = Join-Path $RepoRoot 'third_party\cef-codecs'

$CefVersion = '154.0.34'
$Tag = 'cef-154.0.34-chromium-154.0.8037.98'
$AssetName = 'loopy-windows-x86_64_minimal.tar.bz2'
$DownloadUrl = "https://github.com/ashwardy/loopy-cef-builds/releases/download/$Tag/$AssetName"

# Pinned hashes. If the upstream release changes, update both the URL and hashes.
$ArchiveSha256 = 'F7BE9487A5325E60107AC69E9759CBD6E8723633446DF2C68EC8F4708230B40C'
$LibCefSha256 = '08108F062EC1C2E0FBDAAC481CD7C24C5F58D96C8F24BE2E94A0B5ED729F4EEF'

$CacheRoot = Join-Path $env:TEMP 'Apolion\cef-codecs'
$ExtractRoot = Join-Path $CacheRoot $Tag
$ArchivePath = Join-Path $CacheRoot $AssetName
$ExtractedRuntime = Join-Path $ExtractRoot 'loopy-windows-x86_64_minimal'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Copy-RuntimeFile([string]$Source, [string]$Target) {
    $directory = Split-Path -Parent $Target
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Copy-Item -LiteralPath $Source -Destination $Target -Force
}

$existingLibCef = Join-Path $Destination 'libcef.dll'
if (-not $Force -and (Test-Path -LiteralPath $existingLibCef)) {
    if ((Get-Sha256 $existingLibCef) -eq $LibCefSha256) {
        Write-Host "Codec-enabled CEF $CefVersion is already present in third_party\cef-codecs." -ForegroundColor Green
        return
    }

    Write-Host 'Existing CEF runtime does not match the pinned build; replacing it.' -ForegroundColor Yellow
}

New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null

# Reuse an already downloaded/extracted archive when possible.
$archiveValid = (Test-Path -LiteralPath $ArchivePath) -and ((Get-Sha256 $ArchivePath) -eq $ArchiveSha256)
$extractedValid = (Test-Path -LiteralPath (Join-Path $ExtractedRuntime 'Release\libcef.dll')) -and
                  ((Get-Sha256 (Join-Path $ExtractedRuntime 'Release\libcef.dll')) -eq $LibCefSha256)

if (-not $archiveValid) {
    Write-Host "Downloading CEF $CefVersion with H.264/AAC support..." -ForegroundColor Cyan
    Write-Host "  $DownloadUrl"

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -L --fail --retry 3 --silent --show-error -o $ArchivePath $DownloadUrl
        if ($LASTEXITCODE -ne 0) {
            throw "curl failed with exit code $LASTEXITCODE."
        }
    }
    else {
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $ArchivePath -UseBasicParsing
    }

    $actual = Get-Sha256 $ArchivePath
    if ($actual -ne $ArchiveSha256) {
        Remove-Item -LiteralPath $ArchivePath -Force -ErrorAction SilentlyContinue
        throw "CEF archive hash mismatch. Expected $ArchiveSha256 but got $actual."
    }
}

if (-not $extractedValid) {
    if (Test-Path -LiteralPath $ExtractRoot) {
        Remove-Item -LiteralPath $ExtractRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ExtractRoot -Force | Out-Null

    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if (-not $tar) {
        throw 'tar.exe is required to extract the CEF archive (included with Windows 10 1803+).'
    }

    Write-Host 'Extracting CEF runtime...' -ForegroundColor Cyan
    & $tar.Source -xjf $ArchivePath -C $ExtractRoot
    if ($LASTEXITCODE -ne 0) {
        throw "tar failed with exit code $LASTEXITCODE."
    }
}

$release = Join-Path $ExtractedRuntime 'Release'
$resources = Join-Path $ExtractedRuntime 'Resources'

$required = @(
    (Join-Path $release 'libcef.dll'),
    (Join-Path $release 'chrome_elf.dll'),
    (Join-Path $resources 'resources.pak'),
    (Join-Path $resources 'chrome_100_percent.pak'),
    (Join-Path $resources 'chrome_200_percent.pak'),
    (Join-Path $resources 'locales\en-US.pak')
)

foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "The CEF archive is missing an expected file: $file"
    }
}

if ((Get-Sha256 (Join-Path $release 'libcef.dll')) -ne $LibCefSha256) {
    throw 'Extracted libcef.dll does not match the pinned hash.'
}

# Only the files that actually differ from the official CefSharp/CEF runtime are
# copied. d3dcompiler_47.dll, icudtl.dat and v8_context_snapshot.bin are
# byte-identical between the two builds.
Copy-RuntimeFile (Join-Path $release 'libcef.dll') (Join-Path $Destination 'libcef.dll')
Copy-RuntimeFile (Join-Path $release 'chrome_elf.dll') (Join-Path $Destination 'chrome_elf.dll')
Copy-RuntimeFile (Join-Path $resources 'resources.pak') (Join-Path $Destination 'resources.pak')
Copy-RuntimeFile (Join-Path $resources 'chrome_100_percent.pak') (Join-Path $Destination 'chrome_100_percent.pak')
Copy-RuntimeFile (Join-Path $resources 'chrome_200_percent.pak') (Join-Path $Destination 'chrome_200_percent.pak')
Copy-RuntimeFile (Join-Path $resources 'locales\en-US.pak') (Join-Path $Destination 'locales\en-US.pak')

$provenance = @"
Codec-enabled CEF runtime for Apolion
=====================================

CEF version : $CefVersion+g14c5a08+chromium-154.0.8037.98
Source      : $DownloadUrl
Archive     : $AssetName
SHA-256     : $ArchiveSha256
libcef.dll  : $LibCefSha256

Build configuration (per upstream release notes):
  proprietary_codecs=true
  ffmpeg_branding="Chrome"
  is_official_build=true

Compatible with CefSharp 154.0.340 (CEF 154.0.34).

This is an unofficial third-party CEF build. H.264/AAC are covered by patent
pools; the build ships without a patent license. Review the upstream project and
your licensing obligations before redistributing Apolion.
"@

Set-Content -LiteralPath (Join-Path $Destination 'PROVENANCE.txt') -Value $provenance -Encoding UTF8

Write-Host ''
Write-Host "Codec-enabled CEF $CefVersion installed to third_party\cef-codecs." -ForegroundColor Green
Write-Host 'Rebuild Apolion; the ApplyCodecEnabledCef target copies it into bin\<Configuration>.' -ForegroundColor Green
