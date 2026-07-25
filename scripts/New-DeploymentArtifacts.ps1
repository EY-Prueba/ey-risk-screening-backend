[CmdletBinding()]
param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\deployment'
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$bundlePath = Join-Path $resolvedOutput 'efbundle'
$sqlPath = Join-Path $resolvedOutput 'migrations-idempotent.sql'
$infrastructureProject = Join-Path $repositoryRoot 'src\EyRiskScreening.Infrastructure\EyRiskScreening.Infrastructure.csproj'
$startupProject = Join-Path $repositoryRoot 'src\EyRiskScreening.Api\EyRiskScreening.Api.csproj'
$lockFileSnapshots = @{}
Get-ChildItem `
    -Path (Join-Path $repositoryRoot 'src') `
    -Filter 'packages.lock.json' `
    -Recurse `
    -File |
    ForEach-Object {
        $lockFileSnapshots[$_.FullName] =
            [System.IO.File]::ReadAllBytes($_.FullName)
    }

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

Push-Location $repositoryRoot
try {
    Write-Host 'Restoring the pinned local EF Core tool.'
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet tool restore failed.'
    }

    Write-Host 'Restoring locked application dependencies.'
    dotnet restore $startupProject --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet restore failed.'
    }

    Write-Host 'Building the migration projects in Release.'
    dotnet build $startupProject --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet build failed.'
    }

    Write-Host "Generating the Linux x64 self-contained EF migration bundle at $bundlePath."
    dotnet ef migrations bundle `
        --project $infrastructureProject `
        --startup-project $startupProject `
        --configuration Release `
        --no-build `
        --self-contained `
        --target-runtime linux-x64 `
        --output $bundlePath `
        --force
    if ($LASTEXITCODE -ne 0) {
        throw 'EF migration bundle generation failed.'
    }

    Write-Host "Generating the idempotent SQL migration script at $sqlPath."
    dotnet ef migrations script `
        --project $infrastructureProject `
        --startup-project $startupProject `
        --configuration Release `
        --no-build `
        --idempotent `
        --output $sqlPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Idempotent SQL generation failed.'
    }

    Write-Host 'Deployment artifacts generated. No migration was executed.'
}
finally {
    foreach ($snapshot in $lockFileSnapshots.GetEnumerator()) {
        [System.IO.File]::WriteAllBytes(
            $snapshot.Key,
            [byte[]] $snapshot.Value)
    }

    Write-Host 'Restored package lock files after bundle generation.'
    Pop-Location
}
