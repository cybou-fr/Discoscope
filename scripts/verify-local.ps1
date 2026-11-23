# Verifies the solution locally without cleaning any user files.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$runName = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $repository "artifacts/verification/$runName"

function Invoke-VerificationStep {
    param([string]$Name, [string[]]$Arguments)

    $log = Join-Path $runDirectory "$Name.log"
    & $dotnet @Arguments | Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE. See $log"
    }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Run this script on Windows.'
    }

    New-Item -Path $runDirectory -ItemType Directory -Force | Out-Null
    $dotnet = (Get-Command dotnet -CommandType Application).Source
    $sdks = & $dotnet --list-sdks
    $sdkExit = $LASTEXITCODE
    $sdks | Set-Content -LiteralPath (Join-Path $runDirectory 'sdks.log')
    if ($sdkExit -ne 0 -or @($sdks | Where-Object { $_ -match '^10\.0\.\d+\s+\[' }).Count -eq 0) {
        throw 'Install a stable .NET 10 SDK before running verification.'
    }

    $solution = Join-Path $repository 'Discoscope.slnx'
    $buildArtifacts = Join-Path $runDirectory 'build'
    $testResults = Join-Path $runDirectory 'tests'
    Push-Location -LiteralPath $repository
    try {
        Invoke-VerificationStep -Name 'sdk' -Arguments @('--version')
        Invoke-VerificationStep -Name 'restore' -Arguments @('restore', $solution, '--artifacts-path', $buildArtifacts)
        Invoke-VerificationStep -Name 'build' -Arguments @('build', $solution, '-c', 'Release', '--no-restore', '--artifacts-path', $buildArtifacts)
        Invoke-VerificationStep -Name 'test' -Arguments @('test', $solution, '-c', 'Release', '--no-build', '--artifacts-path', $buildArtifacts,
            '--logger', 'trx', '--results-directory', $testResults)
    }
    finally {
        Pop-Location
    }
    Write-Host "Verification succeeded. Results: $runDirectory"
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
