# Run LATER, from the project root, non-elevated.
# Replaces the unverified pinned versions in ContosoPizza.csproj with whatever
# is current on NuGet, then builds.
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
Push-Location $project
try {
    $packages = @(
        'OpenTelemetry.Extensions.Hosting',
        'OpenTelemetry.Instrumentation.AspNetCore',
        'OpenTelemetry.Instrumentation.Http',
        'OpenTelemetry.Instrumentation.Runtime',
        'OpenTelemetry.Exporter.Console'
    )
    foreach ($p in $packages) {
        dotnet add package $p
        if ($LASTEXITCODE -ne 0) { throw "dotnet add package $p failed" }
    }
    dotnet build
} finally { Pop-Location }
