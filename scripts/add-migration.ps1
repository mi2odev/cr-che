<#
.SYNOPSIS
    Adds a new EF Core migration after you changed an entity or the DbContext.
.EXAMPLE
    ./scripts/add-migration.ps1 AddChildNickname
.NOTES
    Nothing else to do afterwards: the application applies pending migrations by itself at startup
    (Database.MigrateAsync), so there is no manual "database update" step.
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')]
    [string]$Name
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    # Infrastructure is its own startup project: it contains a design-time DbContext factory,
    # so the WPF project is not needed to create migrations.
    dotnet ef migrations add $Name `
        --project src/Creche.Infrastructure `
        --startup-project src/Creche.Infrastructure `
        --output-dir Persistence/Migrations
    if ($LASTEXITCODE -ne 0) { throw 'dotnet ef migrations add failed.' }

    Write-Host "Migration '$Name' added. Review the generated file, then run ./scripts/test.ps1." -ForegroundColor Green
}
finally {
    Pop-Location
}
