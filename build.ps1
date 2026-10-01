<#
.SYNOPSIS
  Builds PlayPollo and packs it into dist\PlayPollo_<version>.pext with Playnite's Toolbox.

.NOTES
  The plugin project compiles WPF XAML, which `dotnet build` can't do for net462, so this uses Visual Studio's
  MSBuild. Core and the tests build with plain `dotnet`.
#>
param(
    [string]$Configuration = 'Release',
    [string]$PlayniteDir = (Join-Path $env:LOCALAPPDATA 'Playnite')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild not found.' }

$toolbox = Join-Path $PlayniteDir 'Toolbox.exe'
if (-not (Test-Path $toolbox)) { throw "Toolbox.exe not found in $PlayniteDir. Pass -PlayniteDir." }

& dotnet test (Join-Path $root 'tests\PlayPollo.Tests') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

& $msbuild (Join-Path $root 'src\PlayPollo\PlayPollo.csproj') /restore /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# Stage only what Playnite needs. It already provides Playnite.SDK.dll.
$out = Join-Path $root "src\PlayPollo\bin\$Configuration\net462"
$stage = Join-Path $root 'dist\stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Get-ChildItem $out -File | Where-Object { $_.Name -notmatch '^Playnite\.SDK' -and $_.Extension -ne '.pdb' } |
    Copy-Item -Destination $stage

$dist = Join-Path $root 'dist'
Get-ChildItem $dist -Filter *.pext -ErrorAction SilentlyContinue | Remove-Item -Force
& $toolbox pack $stage $dist
if ($LASTEXITCODE -ne 0) { throw 'Toolbox pack failed.' }

Get-ChildItem $dist -Filter *.pext | ForEach-Object { Write-Host "Packed $($_.FullName)" }
