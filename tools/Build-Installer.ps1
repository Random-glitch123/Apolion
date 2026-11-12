#Requires -Version 5.1
<#
.SYNOPSIS
Builds the Apolion Inno Setup installer (Apolion-<version>-setup.exe).

.DESCRIPTION
1. Runs `dotnet publish` to produce the self-contained win-x64 app output.
2. Copies the publish output to a staging folder.
3. Compiles `tools\Apolion.iss` with Inno Setup (ISCC.exe) to produce the setup .exe.

The installer targets %LOCALAPPDATA%\Apolion\ and runs with
PrivilegesRequired=lowest, so no admin elevation is ever required.

Output: artifacts\release\Apolion-<version>-setup.exe

.PARAMETER Version
Version to embed. Defaults to the <Version> in Apolion.csproj.

.PARAMETER Configuration
Release or Debug. Default: Release.

.PARAMETER SkipPublish
Skip dotnet publish and use an existing staging folder at
bin\<Configuration>\net9.0-windows\win-x64\publish-staging.

.PARAMETER InnoSetupPath
Full path to ISCC.exe. If omitted, the script looks for Inno Setup on PATH
and in the default Program Files location.
#>

[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipPublish,
    [string]$InnoSetupPath
)

$ErrorActionPreference = 'Stop'

$script:ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:ProjectFile = Join-Path $script:ProjectRoot 'Apolion.csproj'
$script:ArtifactsDir = Join-Path $script:ProjectRoot 'artifacts\release'
$script:StagingDir = Join-Path $script:ProjectRoot 'bin\publish-staging'

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
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

function Find-Iscc {
    param([string]$HintPath)

    # 1. Explicit hint
    if ($HintPath -and (Test-Path -LiteralPath $HintPath)) {
        return $HintPath
    }

    # 2. On PATH
    $iscc = Get-Command 'iscc' -ErrorAction SilentlyContinue
    if ($iscc) {
        return $iscc.Source
    }

    # 3. Default Inno Setup install locations
    $defaultLocations = @(
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 5\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
    )
    foreach ($loc in $defaultLocations) {
        if (Test-Path -LiteralPath $loc) {
            return $loc
        }
    }

    return $null
}

# ── Resolve version ───────────────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion
}
$Version = $Version.Trim().TrimStart('v', 'V')

Write-Step "Building Apolion installer v$Version ($Configuration)"

# ── Publish ───────────────────────────────────────────────────────────────────
if ($SkipPublish) {
    $PublishSource = Join-Path $script:ProjectRoot "bin\$Configuration\net9.0-windows\win-x64"
    if (-not (Test-Path -LiteralPath $PublishSource)) {
        throw "-SkipPublish requires a publish output at '$PublishSource'. Run without -SkipPublish first."
    }
} else {
    if (Test-Path -LiteralPath $script:StagingDir) {
        Remove-Item -LiteralPath $script:StagingDir -Recurse -Force
    }

    Write-Step "Publishing Apolion ($Configuration / win-x64 / self-contained)"
    & dotnet publish $script:ProjectFile `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -o $script:StagingDir `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
    $PublishSource = $script:StagingDir
}

# ── Build and include the dedicated updater ──────────────────────────────
Write-Step "Publishing Apolion Updater"
$updaterProject = Join-Path $script:ProjectRoot 'Apolion.Updater\Apolion.Updater.csproj'
$updaterOutput = Join-Path $PublishSource 'Apolion Updater.exe'
& dotnet publish $updaterProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o (Split-Path $updaterOutput) `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for Apolion Updater failed with exit code $LASTEXITCODE."
}

# Verify updater was created with correct name
if (-not (Test-Path -LiteralPath $updaterOutput)) {
    $found = Get-ChildItem -Path (Split-Path $updaterOutput) -Filter "Apolion Updater*" -Recurse | Select-Object -First 1
    if ($found) {
        Move-Item -LiteralPath $found.FullName -Destination $updaterOutput -Force
    }
    else {
        throw "Apolion Updater.exe was not created at '$updaterOutput'."
    }
}

# ── Output directory ──────────────────────────────────────────────────────────
New-Item -ItemType Directory -Path $script:ArtifactsDir -Force | Out-Null

# ── Find Inno Setup compiler ──────────────────────────────────────────────────
$iscc = Find-Iscc -HintPath $InnoSetupPath
if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup compiler (ISCC.exe) was not found." -ForegroundColor Yellow
    Write-Host "The .iss script is ready at tools\Apolion.iss"
    Write-Host "Install Inno Setup from https://jrsoftware.org and re-run this script."
    Write-Host ""
    Write-Host "Installer source (not compiled):"
    Write-Host "  Script : tools\Apolion.iss"
    Write-Host "  Staging: $PublishSource"
    return
}

Write-Step "Compiling installer with Inno Setup"
$issScript = Join-Path $script:ProjectRoot 'tools\Apolion.iss'
$compileArgs = @(
    "/Q",                          # quiet
    "/F=$script:ArtifactsDir",    # output directory
    "/O=$script:ArtifactsDir",    # output dir (same)
    "/DMyAppVersion=$Version",    # pass version as define override
    "$issScript"
)

& $iscc $compileArgs
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$setupExe = Join-Path $script:ArtifactsDir "Apolion-$Version-setup.exe"
if (Test-Path -LiteralPath $setupExe) {
    Write-Host ""
    Write-Host "Installer ready: $setupExe" -ForegroundColor Green
    Write-Host "Size: $([Math]::Round((Get-Item $setupExe).Length / 1MB, 2)) MB" -ForegroundColor Green
}
else {
    Write-Host ""
    Write-Host "Installer compiled but expected output not found: $setupExe" -ForegroundColor Yellow
    Get-ChildItem -Path $script:ArtifactsDir | Format-Table Name, Length -AutoSize
}
