param(
    [string]$ExecutablePath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\QuotaOrb\QuotaOrb.exe'),
    [string]$Arguments = '',
    [switch]$Hidden
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or [IO.Path]::GetFileName($exe) -ne 'QuotaOrb.exe') {
    throw 'Select an existing QuotaOrb.exe.'
}

# Use the existing desktop's automation object, not a new ShellExecute caller.
# A direct Start-Process from a managed terminal inherits its kill-on-close job.
# Reference: https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643
$shell = $null; $windows = $null; $desktop = $null; $view = $null; $desktopShell = $null
try {
    $shell = New-Object -ComObject Shell.Application
    $windows = $shell.Windows()
    $desktopHwnd = 0
    $desktop = $windows.FindWindowSW(0,$null,8,[ref]$desktopHwnd,1)
    if (-not $desktop -or $desktopHwnd -eq 0) { throw 'Windows desktop is not available. Open QuotaOrb from its desktop shortcut.' }
    $view = $desktop.Document
    $desktopShell = $view.Application
    if (-not $desktopShell) { throw 'Windows desktop launch is unavailable. Open QuotaOrb from its desktop shortcut.' }
    $show = if ($Hidden) { 0 } else { 1 }
    $desktopShell.ShellExecute($exe,$Arguments,(Split-Path -Parent $exe),'open',$show)
} finally {
    foreach ($reference in @($desktopShell,$view,$desktop,$windows,$shell)) {
        if ($null -ne $reference -and [Runtime.InteropServices.Marshal]::IsComObject($reference)) {
            try { [Runtime.InteropServices.Marshal]::ReleaseComObject($reference) | Out-Null } catch [Runtime.InteropServices.InvalidComObjectException] { }
        }
    }
}
