$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/Update-PublishDirectory.ps1')
$workspace = Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/publish-test-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $workspace 'artifacts/payload'
$target = Join-Path $workspace 'publish'
[IO.Directory]::CreateDirectory($source) | Out-Null
[IO.Directory]::CreateDirectory($target) | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'FindHistory.exe'), 'new-exe')
[IO.File]::WriteAllText((Join-Path $source 'FindHistory.dll'), 'new-dll')
[IO.File]::WriteAllText((Join-Path $target 'old-file.txt'), 'previous-publish')
$result = Update-PublishDirectory -WorkspaceRoot $workspace -SourceDirectory $source
if ($result.VerifiedFiles -ne 2 -or !(Test-Path -LiteralPath (Join-Path $result.BackupDirectory 'old-file.txt')) -or
    (Test-Path -LiteralPath (Join-Path $target 'old-file.txt')) -or
    [IO.File]::ReadAllText((Join-Path $target 'FindHistory.dll')) -ne 'new-dll') {
    throw 'Publish replacement or backup failed.'
}
$firstHash = (Get-FileHash -LiteralPath (Join-Path $target 'FindHistory.dll')).Hash
foreach ($invalidSource in @((Join-Path $workspace 'missing'), $target)) {
    $rejected = $false
    try { Update-PublishDirectory -WorkspaceRoot $workspace -SourceDirectory $invalidSource | Out-Null }
    catch { $rejected = $true }
    if (!$rejected) { throw "Accepted invalid source: $invalidSource" }
}
[IO.File]::WriteAllText((Join-Path $source 'settings.json'), '{}')
$rejected = $false
try { Update-PublishDirectory -WorkspaceRoot $workspace -SourceDirectory $source | Out-Null }
catch { $rejected = $true }
if (!$rejected -or (Get-FileHash -LiteralPath (Join-Path $target 'FindHistory.dll')).Hash -ne $firstHash) {
    throw 'Data-file rejection did not preserve publish.'
}
$cleanSource = Join-Path $workspace 'artifacts/clean-payload'
[IO.Directory]::CreateDirectory($cleanSource) | Out-Null
[IO.File]::WriteAllText((Join-Path $cleanSource 'FindHistory.exe'), 'next-exe')
[IO.File]::WriteAllText((Join-Path $cleanSource 'FindHistory.dll'), 'next-dll')
# Simulate a failure after the old publish directory has been moved to backup.
function Move-Item {
    param([string]$LiteralPath, [string]$Destination)
    if ((Split-Path -Leaf $LiteralPath) -like 'publish-stage-*') {
        throw 'Injected stage move failure.'
    }
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
}
$rejected = $false
try { Update-PublishDirectory -WorkspaceRoot $workspace -SourceDirectory $cleanSource | Out-Null }
catch { $rejected = $true }
if (!$rejected -or (Get-FileHash -LiteralPath (Join-Path $target 'FindHistory.dll')).Hash -ne $firstHash) {
    throw 'Failed replacement did not restore the previous publish directory.'
}
Write-Output "PASS: replacement, backup, hashes, invalid input, data protection, rollback. Fixtures: $workspace"
