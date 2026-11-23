[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')]
    [string]$Version = '0.2.0-rc.1',
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Build the installer on Windows.'
    }
    if (-not $CompilerPath) {
        $compiler = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue
        $CompilerPath = $compiler.Source
        if (-not $CompilerPath) {
            $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
        }
    }
    $compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
    $numericVersion = ($Version -split '-', 2)[0]
    $output = Join-Path $repository 'artifacts/installer'
    $build = Join-Path $output ('build-' + [guid]::NewGuid().ToString('N'))
    $publish = Join-Path $build 'payload'
    New-Item -Path $publish -ItemType Directory -Force | Out-Null

    $project = Join-Path $repository 'src/Discoscope.Desktop/Discoscope.Desktop.csproj'
    & dotnet publish $project -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $build 'dotnet') `
        -o $publish "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $publish

    $recipe = Join-Path $repository 'installer/Discoscope.iss'
    & $compiler "/DAppVersion=$Version" "/DNumericVersion=$numericVersion" "/DPublishDirectory=$publish" "/O$output" $recipe
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

    $file = "Discoscope-$Version-win-x64-setup.exe"
    $installer = Join-Path $output $file
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $file" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Write-Host "Installer: $installer"
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
