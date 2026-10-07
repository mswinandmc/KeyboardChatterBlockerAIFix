<#
.SYNOPSIS
    Builds KeyboardChatterBlocker and lays out a ready-to-install UnityModManager mod folder.

.DESCRIPTION
    Produces dist/KeyboardChatterBlocker/{Info.json, KeyboardChatterBlocker.dll} and, unless
    -NoZip is used, dist/KeyboardChatterBlocker.v<version>.zip. Copy the folder to
    <A Dance of Fire and Ice>/Mods/KeyboardChatterBlocker or install the zip through UMM.

.PARAMETER GameDir
    Root of the A Dance of Fire and Ice installation used for the reference assemblies.

.PARAMETER CompilerPath
    Optional path to csc.exe; see build.ps1.
#>
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice",
    [string]$CompilerPath = "",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$dist = Join-Path $repo "dist"
$staging = Join-Path $dist "KeyboardChatterBlocker"

$buildArguments = @{ GameDir = $GameDir; SourceDir = $repo; OutDir = $staging }
if ($CompilerPath) { $buildArguments.CompilerPath = $CompilerPath }
& (Join-Path $repo "build.ps1") @buildArguments

Copy-Item (Join-Path $dist "Info.json") (Join-Path $staging "Info.json") -Force

$info = Get-Content (Join-Path $dist "Info.json") -Raw | ConvertFrom-Json
Write-Output "Packaged KeyboardChatterBlocker $($info.Version) for game $($info.GameVersion)"

if (-not $NoZip) {
    $zip = Join-Path $dist "KeyboardChatterBlocker.v$($info.Version).zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip
    Write-Output "Created $zip"
}
