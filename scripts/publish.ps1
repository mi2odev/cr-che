<#
.SYNOPSIS
    Runs the tests, then publishes a self-contained Windows x64 build that needs no .NET on the target PC.
.PARAMETER SingleFile
    Produce one CrecheManager.exe (plus appsettings.json) instead of a folder of files.
.PARAMETER Output
    Destination folder. Default: ./publish
.PARAMETER SkipTests
    Publish without running the tests first (not recommended).
.EXAMPLE
    ./scripts/publish.ps1
.EXAMPLE
    ./scripts/publish.ps1 -SingleFile -Output D:\Release\Creche
#>
param(
    [switch]$SingleFile,
    [string]$Output,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'publish' }

Push-Location $root
try {
    if (-not $SkipTests) {
        Write-Host '== Running tests ==' -ForegroundColor Cyan
        dotnet test Creche.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed: nothing was published.' }
    }

    $publishArgs = @(
        'publish', 'src/Creche.App/Creche.App.csproj',
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishReadyToRun=true',
        '-o', $Output
    )
    if ($SingleFile) {
        $publishArgs += '-p:PublishSingleFile=true'
        # SQLite's native library is unpacked next to the exe at first start.
        $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
    }

    Write-Host "== Publishing to $Output ==" -ForegroundColor Cyan
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    Write-Host ''
    Write-Host "Done. Copy the folder '$Output' to the target PC and start CrecheManager.exe." -ForegroundColor Green
    Write-Host 'Data is created on first start under %LOCALAPPDATA%\CrecheManager (see appsettings.json to change it).'
}
finally {
    Pop-Location
}
