param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\QuotaOrb'),
    [string]$ShortcutDirectory = [Environment]::GetFolderPath('DesktopDirectory')
)
$ErrorActionPreference = 'Stop'
$sourceDir = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\','/')
$programDir = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\','/')
$desktopDir = [IO.Path]::GetFullPath($ShortcutDirectory).TrimEnd('\','/')
function Assert-NoReparsePath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'A program or shortcut path must not pass through a filesystem link.' }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
function Copy-VerifiedAtomic([string]$Origin,[string]$Target,[string]$Hash) {
    Assert-NoReparsePath $Target
    $parent = Split-Path -Parent $Target
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = Join-Path $parent ('.quota-orb-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    try {
        Copy-Item -LiteralPath $Origin -Destination $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $Hash) { throw 'The copied program file failed verification.' }
        Assert-NoReparsePath $Target
        if (Test-Path -LiteralPath $Target) { [IO.File]::Replace($temporary,$Target,[NullString]::Value) }
        else { [IO.File]::Move($temporary,$Target) }
    }
    finally {
        if ((Test-Path -LiteralPath $temporary) -and [IO.Path]::GetFullPath($temporary).StartsWith($parent+'\',[StringComparison]::OrdinalIgnoreCase)) {
            Assert-NoReparsePath $temporary
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}
if ($programDir -eq [IO.Path]::GetPathRoot($programDir).TrimEnd('\','/') -or $desktopDir -eq [IO.Path]::GetPathRoot($desktopDir).TrimEnd('\','/')) { throw 'Select a program and shortcut subdirectory, not a drive root.' }
Assert-NoReparsePath $programDir
Assert-NoReparsePath $desktopDir
$shellObject = New-Object -ComObject WScript.Shell
$installedExe = Join-Path $programDir 'QuotaOrb.exe'
$shortcutFile = Join-Path $desktopDir '额度管理.lnk'
$legacyShortcut = Join-Path $desktopDir '额度球.lnk'
$preservedShortcut = $null
foreach ($existingLink in @($shortcutFile,$legacyShortcut)) {
    Assert-NoReparsePath $existingLink
    if (Test-Path -LiteralPath $existingLink -PathType Leaf) {
        $existingShortcut = $shellObject.CreateShortcut($existingLink)
        try {
            if ($existingShortcut.TargetPath -eq $installedExe) { $preservedShortcut = $existingLink; break }
        } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($existingShortcut) | Out-Null }
    }
}
$manifestFile = Join-Path $sourceDir 'checksums.json'
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw 'Extract the complete release archive before installing.' }
$manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($required in @('QuotaOrb.exe','install-local.ps1')) {
    if (-not ($manifest | Where-Object { $_.Path -eq $required })) { throw ('Incomplete program package: ' + $required) }
}
$files = @()
$targets = @{}
foreach ($entry in $manifest) {
    $relative = [string]$entry.Path
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or
        $relative -match '(^|[\\/])(profiles|accounts\.db|auth\.json)([\\/]|$)' -or
        [IO.Path]::GetExtension($relative) -in @('.db','.db-wal','.db-shm','.lnk') -or $relative.Contains(':')) {
        throw 'The package manifest contains an invalid path.'
    }
    $origin = [IO.Path]::GetFullPath((Join-Path $sourceDir $relative))
    $target = [IO.Path]::GetFullPath((Join-Path $programDir $relative))
    if (-not $origin.StartsWith($sourceDir+'\',[StringComparison]::OrdinalIgnoreCase) -or
        -not $target.StartsWith($programDir+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The package path is outside its program directory.' }
    if ($targets.ContainsKey($target)) { throw 'The package manifest contains a duplicate path.' }
    $targets[$target] = $true
    Assert-NoReparsePath $target
    if (-not (Test-Path -LiteralPath $origin -PathType Leaf) -or
        (Get-FileHash -LiteralPath $origin -Algorithm SHA256).Hash -ne $entry.Hash) { throw ('Package file missing or damaged: ' + $relative) }
    $files += [pscustomobject]@{Origin=$origin;Target=$target;Hash=$entry.Hash}
}
Assert-NoReparsePath (Join-Path $programDir 'checksums.json')
$runtimeDirectory = [IO.Path]::GetFullPath((Join-Path $programDir 'runtime'))
if (-not $runtimeDirectory.StartsWith($programDir+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The legacy directory is outside the installation.' }
$obsoleteFiles = @('runtime\codex.exe','runtime\codex-code-mode-host.exe','runtime\codex-package.json','安装到本机.ps1','QuotaOrb.pdb','QuotaOrb.Core.pdb')
foreach ($relative in $obsoleteFiles) { Assert-NoReparsePath (Join-Path $programDir $relative) }
Assert-NoReparsePath $runtimeDirectory
$active = @(Get-Process -Name QuotaOrb -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe })
if ($active.Count -gt 0) { throw 'Exit the running app from its system tray menu before installing.' }
New-Item -ItemType Directory -Path $programDir,$desktopDir -Force | Out-Null
if (-not [string]::Equals($sourceDir,$programDir,[StringComparison]::OrdinalIgnoreCase)) {
    foreach ($file in $files) {
        Copy-VerifiedAtomic $file.Origin $file.Target $file.Hash
    }
    Copy-VerifiedAtomic $manifestFile (Join-Path $programDir 'checksums.json') (Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash
}
# Remove only obsolete files owned by QuotaOrb, after the new program has been installed.
foreach ($relative in $obsoleteFiles) {
    $legacyFile = [IO.Path]::GetFullPath((Join-Path $programDir $relative))
    if (-not $legacyFile.StartsWith($programDir+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The obsolete file is outside the installation.' }
    if (-not $targets.ContainsKey($legacyFile) -and (Test-Path -LiteralPath $legacyFile -PathType Leaf)) { Remove-Item -LiteralPath $legacyFile -Force }
}
if ((Test-Path -LiteralPath $runtimeDirectory -PathType Container) -and -not (Get-ChildItem -LiteralPath $runtimeDirectory -Force)) { Remove-Item -LiteralPath $runtimeDirectory }
$temporaryShortcut = Join-Path $desktopDir ('.quota-orb-'+[Guid]::NewGuid().ToString('N')+'.lnk')
if ($preservedShortcut) { Copy-Item -LiteralPath $preservedShortcut -Destination $temporaryShortcut }
$shortcut = $shellObject.CreateShortcut($temporaryShortcut)
if (-not $preservedShortcut) {
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = $programDir
    $shortcut.Description = '独立运行的 Codex 多账号额度助手'
}
$shortcut.IconLocation = $installedExe+',0'
try {
    $shortcut.Save()
    Assert-NoReparsePath $shortcutFile
    if (Test-Path -LiteralPath $shortcutFile) { [IO.File]::Replace($temporaryShortcut,$shortcutFile,[NullString]::Value) }
    else { [IO.File]::Move($temporaryShortcut,$shortcutFile) }
}
finally {
    if ((Test-Path -LiteralPath $temporaryShortcut) -and [IO.Path]::GetFullPath($temporaryShortcut).StartsWith($desktopDir+'\',[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $temporaryShortcut -Force }
}
# Only remove the previous app shortcut when it points to this installation.
if (Test-Path -LiteralPath $legacyShortcut -PathType Leaf) {
    if ($shellObject.CreateShortcut($legacyShortcut).TargetPath -eq $installedExe) { Remove-Item -LiteralPath $legacyShortcut }
}
Write-Output ('Installed: ' + $installedExe)
Write-Output ('Desktop shortcut: ' + $shortcutFile)
