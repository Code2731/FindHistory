function Update-PublishDirectory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$WorkspaceRoot,
          [Parameter(Mandatory)][string]$SourceDirectory)
    $ErrorActionPreference = 'Stop'
    $workspace = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
    $artifacts = Join-Path $workspace 'artifacts'
    $target = Join-Path $workspace 'publish'
    $source = (Resolve-Path -LiteralPath $SourceDirectory).Path
    if (!$source.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source must be inside workspace artifacts.'
    }
    foreach ($path in @($workspace, $artifacts, $source, $target)) {
        if ((Test-Path -LiteralPath $path) -and
            ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse point not supported: $path"
        }
    }
    $ancestor = Split-Path -Parent $source
    while ($ancestor -ne $workspace) {
        if ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse point not supported: $ancestor"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    $targetPrefix = $target + [IO.Path]::DirectorySeparatorChar
    if (Get-Process FindHistory -ErrorAction SilentlyContinue | Where-Object {
        !$_.Path -or $_.Path.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)
    }) {
        throw 'Exit FindHistory from the tray menu before updating publish.'
    }
    foreach ($name in @('FindHistory.exe', 'FindHistory.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) {
            throw "Missing release file: $name"
        }
    }
    foreach ($directory in @($source, $target)) {
        if (!(Test-Path -LiteralPath $directory)) { continue }
        $entries = @(Get-ChildItem -LiteralPath $directory -Recurse -Force)
        if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw "Reparse point found in: $directory"
        }
        if ($entries | Where-Object { !$_.PSIsContainer -and
            ($_.Name -match '\.(db|sqlite|sqlite3)($|-)|\.bak$' -or $_.Name -eq 'settings.json') }) {
            throw "Data files found in $directory. Move DB and settings to a separate data directory first."
        }
    }
    $runId = [Guid]::NewGuid().ToString('N')
    $stage = Join-Path $artifacts "publish-stage-$runId"
    $backup = Join-Path $artifacts "publish-before-update-$runId"
    Copy-Item -LiteralPath $source -Destination $stage -Recurse
    $files = @(Get-ChildItem -LiteralPath $source -Recurse -File -Force)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $stage $relative)).Hash) {
            throw "Staged file verification failed: $relative"
        }
    }
    if ($files.Count -ne @(Get-ChildItem -LiteralPath $stage -Recurse -File -Force).Count) {
        throw 'Staged file count mismatch.'
    }
    # Exact targets are resolved above. No recursive deletion; retain the old directory.
    if (Get-Process FindHistory -ErrorAction SilentlyContinue | Where-Object {
        !$_.Path -or $_.Path.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)
    }) {
        throw 'FindHistory started during staging. Publish was not replaced.'
    }
    $hadTarget = Test-Path -LiteralPath $target
    if ($hadTarget) { Move-Item -LiteralPath $target -Destination $backup }
    try { Move-Item -LiteralPath $stage -Destination $target }
    catch {
        if ($hadTarget -and !(Test-Path -LiteralPath $target)) {
            Move-Item -LiteralPath $backup -Destination $target
        }
        throw
    }
    [PSCustomObject]@{
        PublishDirectory = $target
        BackupDirectory = if ($hadTarget) { $backup } else { $null }
        VerifiedFiles = $files.Count
    }
}
