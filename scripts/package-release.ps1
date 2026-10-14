param([string]$OutputDirectory = 'C:\DownLoad\额度管理工具备份')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\QuotaOrb\QuotaOrb.csproj') -Raw)).Project.PropertyGroup.Version
$publishDir = Join-Path $projectRoot ('dist\QuotaOrb-'+$version)
$backupDir = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
function Assert-NoReparsePath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The backup path must not pass through a filesystem link.' }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
if ($backupDir -eq [IO.Path]::GetPathRoot($backupDir).TrimEnd('\','/')) { throw 'Select a backup subdirectory, not a drive root.' }
Assert-NoReparsePath $backupDir
$manifestFile = Join-Path $publishDir 'checksums.json'
if (-not (Test-Path -LiteralPath $manifestFile)) { throw 'Run scripts/publish.ps1 first.' }
$manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
$requiredFiles = @('QuotaOrb.exe')
$legacyFiles = @('runtime\codex.exe','runtime\codex-code-mode-host.exe','runtime\codex-package.json')
foreach ($relative in $requiredFiles) {
    $entry = $manifest | Where-Object { $_.Path -eq $relative }
    $origin = Join-Path $publishDir $relative
    if (-not $entry -or (Get-FileHash -LiteralPath $origin -Algorithm SHA256).Hash -ne $entry.Hash) { throw ('Program file is missing or damaged: '+$relative) }
}
if (Test-Path -LiteralPath $backupDir) {
    if ((Get-Item -LiteralPath $backupDir).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The backup directory must not be a link.' }
    foreach ($directory in Get-ChildItem -LiteralPath $backupDir -Directory -Force) {
        if ($directory.Name -ne 'runtime' -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The destination contains another directory. Use a clean software backup directory.' }
        if (Get-ChildItem -LiteralPath $directory.FullName -Directory -Force) { throw 'The destination contains another directory. Use a clean software backup directory.' }
    }
    foreach ($file in Get-ChildItem -LiteralPath $backupDir -File -Recurse -Force) {
        Assert-NoReparsePath $file.FullName
        $relative = $file.FullName.Substring($backupDir.Length+1)
        if ($relative -notin $requiredFiles -and $relative -notin $legacyFiles) { throw 'The destination contains other files. Use a clean software backup directory.' }
    }
}
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
foreach ($relative in $requiredFiles) {
    $origin = Join-Path $publishDir $relative
    $target = Join-Path $backupDir $relative
    Assert-NoReparsePath $target
    $temporary = Join-Path $backupDir ('.quota-orb-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    try {
        Copy-Item -LiteralPath $origin -Destination $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $origin -Algorithm SHA256).Hash) { throw 'Backup copy checksum mismatch.' }
        Assert-NoReparsePath $target
        # Replacing the directory entry leaves any unrelated hardlink's content intact.
        if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary,$target,[NullString]::Value) }
        else { [IO.File]::Move($temporary,$target) }
    }
    finally {
        if ((Test-Path -LiteralPath $temporary) -and [IO.Path]::GetFullPath($temporary).StartsWith($backupDir+'\',[StringComparison]::OrdinalIgnoreCase)) {
            Assert-NoReparsePath $temporary
            Remove-Item -LiteralPath $temporary -Force
        }
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $origin -Algorithm SHA256).Hash) { throw 'Backup copy checksum mismatch.' }
}
foreach ($relative in $legacyFiles) {
    $legacyFile = [IO.Path]::GetFullPath((Join-Path $backupDir $relative))
    if (-not $legacyFile.StartsWith($backupDir+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The legacy file is outside the backup directory.' }
    if (Test-Path -LiteralPath $legacyFile -PathType Leaf) { Remove-Item -LiteralPath $legacyFile -Force }
}
$runtimeDirectory = [IO.Path]::GetFullPath((Join-Path $backupDir 'runtime'))
if (-not $runtimeDirectory.StartsWith($backupDir+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The legacy directory is outside the backup directory.' }
if ((Test-Path -LiteralPath $runtimeDirectory -PathType Container) -and -not (Get-ChildItem -LiteralPath $runtimeDirectory -Force)) { Remove-Item -LiteralPath $runtimeDirectory }
Get-ChildItem -LiteralPath $backupDir -File -Recurse | Select-Object FullName,Length
