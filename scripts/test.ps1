<#
.SYNOPSIS
    Builds the solution and runs every test (xUnit, real in-memory SQLite).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet test Creche.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    Pop-Location
}
