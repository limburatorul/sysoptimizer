<#
    Publishes Sysoptimizer and wraps it in an installer.

    Output: dist\Sysoptimizer-<version>-setup.exe

    The version comes from the csproj alone, so a release is bumped in exactly one place.
#>
[CmdletBinding()]
param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Sysoptimizer.csproj'
$publishDir = Join-Path $root 'dist\app'

[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }
Write-Host "Sysoptimizer $version" -ForegroundColor Cyan

# Self-contained, single-file: the installer just needs the one exe, and the target needs no .NET runtime.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$size = (Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("  published: {0:N0} MB in dist\app" -f $size) -ForegroundColor DarkGray

if ($SkipInstaller) { return }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found - install Inno Setup 6" }

& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\Sysoptimizer.iss') /Q
if ($LASTEXITCODE -ne 0) { throw "installer failed" }

$setup = Join-Path $root "dist\Sysoptimizer-$version-setup.exe"
Write-Host ("  installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
