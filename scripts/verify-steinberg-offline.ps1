param(
    [string]$ResultsDirectory = (Join-Path $env:TEMP 'SteinbergFoleyMcpPreflight')
)

# Offline/source preflight: no Cubase/Nuendo mutations and no GitHub Actions.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $root 'CubaseCompleteMcp.sln'
$testProject = Join-Path $root 'tests/CubaseCompleteMcp.Server.Tests/CubaseCompleteMcp.Server.Tests.csproj'
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
$reportPath = Join-Path $ResultsDirectory 'steinberg-offline-verification.json'
$report = [ordered]@{
    status = 'NOT_RUN'
    utc = (Get-Date).ToUniversalTime().ToString('o')
    repo = $root
    branch = ''
    head = ''
    dotnet_version = ''
    restore = 'NOT_RUN'
    build = 'NOT_RUN'
    mcp_tests = 'NOT_RUN'
    official_xsd_test = 'NOT_RUN'
    host_cubase_nuendo = 'NOT_RUN'
    github_actions = 'NOT_USED'
    message = ''
}

try {
    if (-not (Test-Path $solution) -or -not (Test-Path $testProject)) {
        throw 'Solution or test project not found.'
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        $report.status = 'ENVIRONMENT_BLOCKED'
        throw 'The .NET SDK is missing. Install the 8.0 SDK matching global.json, then rerun.'
    }
    $report.dotnet_version = (& dotnet --version | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'dotnet --version failed.' }

    if (Get-Command git -ErrorAction SilentlyContinue) {
        $report.branch = (& git -C $root branch --show-current | Select-Object -First 1)
        $report.head = (& git -C $root rev-parse HEAD | Select-Object -First 1)
    }

    # Honor NuGet lockfiles. Restore may need downloads if packages are not cached.
    & dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { $report.restore = 'FAIL'; throw 'dotnet restore failed.' }
    $report.restore = 'PASS'

    & dotnet build $solution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { $report.build = 'FAIL'; throw 'dotnet build failed.' }
    $report.build = 'PASS'

    & dotnet test $testProject -c Release --no-build --no-restore --filter 'FullyQualifiedName~Steinberg' --logger 'trx;LogFileName=steinberg-preflight.trx' --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) {
        $report.mcp_tests = 'FAIL'
        $report.official_xsd_test = 'FAIL_OR_NOT_RUN'
        throw 'One or more Steinberg MCP tests or pinned DAWproject XSD validations failed.'
    }
    $report.mcp_tests = 'PASS'
    $report.official_xsd_test = 'PASS'
    $report.status = 'PASS_OFFLINE_ONLY'
    $report.message = 'Offline .NET build and MCP contract/XSD tests passed. Cubase/Nuendo real import is NOT_RUN.'
}
catch {
    if ($report.status -ne 'ENVIRONMENT_BLOCKED') { $report.status = 'FAIL_OR_BLOCKED' }
    $report.message = $_.Exception.Message
}
finally {
    ($report | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host ('Result: ' + $report.status)
    Write-Host ('Report: ' + $reportPath)
}
if ($report.status -ne 'PASS_OFFLINE_ONLY') { exit 1 }
