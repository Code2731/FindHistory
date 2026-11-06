[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Update-PublishDirectory.ps1')
if (Get-Process FindHistory -ErrorAction SilentlyContinue) {
    throw 'Exit FindHistory from the tray menu before publishing.'
}
Push-Location $workspace
try {
    dotnet build FindHistory.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet run --project tests/FindHistory.SmokeTests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
    & (Join-Path $workspace 'tests/PublishDirectory.Tests.ps1')
    [xml]$project = Get-Content -LiteralPath 'FindHistory.csproj' -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$') { throw 'Invalid project version.' }
    $runId = [Guid]::NewGuid().ToString('N')
    $release = Join-Path $workspace "artifacts/release-v$version-$runId"
    $payload = Join-Path $release 'win-x64'
    dotnet publish FindHistory.csproj -c Release -r win-x64 --self-contained true -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Windows x64 publish failed.' }
    $zipName = "FindHistory-v$version-win-x64.zip"
    $zip = Join-Path $release $zipName
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $release "FindHistory-v$version-win-x64.sha256"),
        "$hash  $zipName`n", [Text.UTF8Encoding]::new($false))
    Update-PublishDirectory -WorkspaceRoot $workspace -SourceDirectory $payload | Format-List
    Write-Output "Release ZIP: $zip"
    Write-Output 'Local packaging completed. GitHub publishing is a separate, explicitly requested step.'
}
finally { Pop-Location }
