<#
.SYNOPSIS
    Builds KeyboardChatterBlocker (ADOFAI 3.4.0 port) against the assemblies that ship with the game.

.DESCRIPTION
    The mod targets the Unity/Mono profile used by the game, so the references are taken from
    <GameDir>\A Dance of Fire and Ice_Data\Managed instead of .NET Framework reference assemblies.
    UnityModManager, 0Harmony, SkyHook and RDTools are referenced from that same folder.

    Three compilers are tried, in order:
      1. Roslyn csc.exe (fast, no SDK required) - pass -CompilerPath, drop the toolset into
         tools\roslyn, or use -DownloadCompiler to fetch Microsoft.Net.Compilers.Toolset.
      2. dotnet build on KeyboardChatterBlocker.csproj (needs a .NET SDK).
      3. msbuild on KeyboardChatterBlocker.csproj (needs Visual Studio / Build Tools).

.PARAMETER GameDir
    Root of the A Dance of Fire and Ice installation (3.4.0).

.PARAMETER OutDir
    Directory the compiled KeyboardChatterBlocker.dll is written to.

.PARAMETER CompilerPath
    Explicit path to csc.exe.

.PARAMETER DownloadCompiler
    Download the Roslyn toolset into tools\roslyn when no compiler is found.
#>
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice",
    [string]$SourceDir = "",
    [string]$OutDir = "",
    [string]$CompilerPath = "",
    [switch]$DownloadCompiler
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
if (-not $SourceDir) { $SourceDir = $repo }
if (-not $OutDir) { $OutDir = Join-Path $repo "dist\KeyboardChatterBlocker" }

$managed = Join-Path $GameDir "A Dance of Fire and Ice_Data\Managed"
if (-not (Test-Path $managed)) {
    throw "Could not find the game's Managed folder at '$managed'. Pass -GameDir <path to A Dance of Fire and Ice>."
}

$references = @(
    "mscorlib.dll",
    "netstandard.dll",
    "System.dll",
    "System.Core.dll",
    "System.Xml.dll",
    "UnityEngine.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.IMGUIModule.dll",
    "UnityEngine.InputLegacyModule.dll",
    "UnityEngine.TextRenderingModule.dll",
    "Assembly-CSharp.dll",
    "Assembly-CSharp-firstpass.dll",
    "Newtonsoft.Json.dll",
    "RDTools.dll",
    "SkyHook.Unity.dll",
    "UnityModManager\UnityModManager.dll",
    "UnityModManager\0Harmony.dll"
) | ForEach-Object { Join-Path $managed $_ }

foreach ($reference in $references) {
    if (-not (Test-Path $reference)) {
        throw "Missing reference assembly: $reference (is this an A Dance of Fire and Ice 3.4.0 install?)"
    }
}

$sources = @()
foreach ($folder in @("KeyboardChatterBlocker", "KeyboardChatterBlocker.Languages")) {
    $path = Join-Path $SourceDir $folder
    if (Test-Path $path) {
        $sources += Get-ChildItem -Path $path -Recurse -Filter *.cs
    }
}
$sources = $sources | Where-Object { $_.FullName -notmatch "\\obj\\|\\bin\\" } | Select-Object -ExpandProperty FullName
if (-not $sources) {
    throw "No C# sources found under '$SourceDir'."
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$output = Join-Path $OutDir "KeyboardChatterBlocker.dll"
$project = Join-Path $repo "KeyboardChatterBlocker.csproj"

function Find-Csc {
    if ($CompilerPath -and (Test-Path $CompilerPath)) { return $CompilerPath }
    $local = Join-Path $repo "tools\roslyn\tasks\net472\csc.exe"
    if (Test-Path $local) { return $local }
    $candidates = @()
    $nuget = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.net.compilers.toolset"
    if (Test-Path $nuget) {
        $candidates += Get-ChildItem -Path $nuget -Recurse -Filter csc.exe -ErrorAction SilentlyContinue
    }
    if (Test-Path (Join-Path $repo "tools")) {
        $candidates += Get-ChildItem -Path (Join-Path $repo "tools") -Recurse -Filter csc.exe -ErrorAction SilentlyContinue
    }
    $preferred = $candidates | Where-Object { $_.FullName -match "net472" } | Select-Object -First 1
    if ($preferred) { return $preferred.FullName }
    if ($candidates) { return ($candidates | Select-Object -First 1).FullName }
    return $null
}

function Get-Roslyn {
    $target = Join-Path $repo "tools\roslyn"
    New-Item -ItemType Directory -Force -Path (Join-Path $repo "tools") | Out-Null
    $zip = Join-Path $repo "tools\roslyn.zip"
    Write-Output "Downloading Microsoft.Net.Compilers.Toolset 4.14.0 ..."
    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.Net.Compilers.Toolset/4.14.0" -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $target -Force
    Remove-Item $zip -Force
    return (Join-Path $target "tasks\net472\csc.exe")
}

$csc = Find-Csc
if (-not $csc -and $DownloadCompiler) { $csc = Get-Roslyn }

if ($csc) {
    $arguments = @(
        "-target:library",
        "-langversion:preview",
        "-unsafe+",
        "-nostdlib+",
        "-noconfig",
        "-optimize+",
        "-deterministic+",
        "-nowarn:CS1701,CS1702",
        "-out:$output"
    ) + $sources + ($references | ForEach-Object { "-r:$_" })
    $compileOutput = & $csc @arguments 2>&1
    $compileOutput | ForEach-Object { Write-Output $_ }
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
    Write-Output "Built $output with $csc"
    return
}

$sdk = & dotnet --list-sdks 2>$null
if ($sdk) {
    & dotnet build $project -c Release -p:AdofaiDir="$GameDir" -p:OutputPath="$OutDir"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
    Write-Output "Built $output with dotnet build"
    return
}

$msbuild = Get-Command msbuild -ErrorAction SilentlyContinue
if ($msbuild) {
    & $msbuild.Source $project /p:Configuration=Release /p:AdofaiDir="$GameDir" /p:OutputPath="$OutDir"
    if ($LASTEXITCODE -ne 0) { throw "msbuild failed with exit code $LASTEXITCODE." }
    Write-Output "Built $output with msbuild"
    return
}

throw @"
No C# compiler found.
  - Install the .NET SDK or Visual Studio Build Tools, or
  - re-run with -DownloadCompiler to fetch the Roslyn toolset (about 20 MB), or
  - pass -CompilerPath <path to csc.exe>.
"@
