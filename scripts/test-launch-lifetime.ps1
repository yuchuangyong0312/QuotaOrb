param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [switch]$LegacyStart
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this regression check in PowerShell 7.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
$fixture = Join-Path $projectRoot ('.build\launch-lifetime\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$dataRoot = Join-Path $fixture 'synthetic data'
$hostScript = Join-Path $fixture 'host.ps1'
@'
param($ExecutablePath,$DataRoot,$Fixture,$LaunchScript,[switch]$LegacyStart)
$ErrorActionPreference = 'Stop'
while (-not (Test-Path -LiteralPath (Join-Path $Fixture 'go'))) { Start-Sleep -Milliseconds 50 }
$arguments = '--smoke-test --skip-auth-handoff --data-root "' + $DataRoot + '"'
if ($LegacyStart) {
    $child = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $childId = $child.Id
} else {
    & $LaunchScript -ExecutablePath $ExecutablePath -Arguments $arguments -Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $child = Get-CimInstance Win32_Process -Filter "Name='QuotaOrb.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataRoot) } | Select-Object -First 1
        if ($child) { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $child) { throw 'The isolated app process was not found.' }
    $childId = $child.ProcessId
}
[IO.File]::WriteAllText((Join-Path $Fixture 'child-id'),[string]$childId)
Start-Sleep -Seconds 45
'@ | Set-Content -LiteralPath $hostScript -Encoding utf8
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class QuotaLaunchTestJob {
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr a, string n);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr j, int c, IntPtr p, uint n);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr j, IntPtr p);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool IsProcessInJob(IntPtr p, IntPtr j, out bool result);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public static IntPtr Create(IntPtr process) {
        IntPtr job=CreateJobObject(IntPtr.Zero,null), limits=Marshal.AllocHGlobal(144);
        if(job==IntPtr.Zero) throw new Win32Exception();
        try {
            for(int i=0;i<144;i++) Marshal.WriteByte(limits,i,0);
            Marshal.WriteInt32(limits,16,0x2800); // Kill-on-close + breakaway allowed, like the deployment host.
            if(!SetInformationJobObject(job,9,limits,144) || !AssignProcessToJobObject(job,process)) throw new Win32Exception();
            return job;
        } catch { CloseHandle(job); throw; }
        finally { Marshal.FreeHGlobal(limits); }
    }
    public static bool Contains(IntPtr job, IntPtr process) {
        bool result;
        if(!IsProcessInJob(process,job,out result)) throw new Win32Exception();
        return result;
    }
}
'@
$hostArgs = '-NoProfile -File "' + $hostScript + '" -ExecutablePath "' + $exe + '" -DataRoot "' + $dataRoot + '" -Fixture "' + $fixture + '" -LaunchScript "' + (Join-Path $PSScriptRoot 'start-local.ps1') + '"'
if ($LegacyStart) { $hostArgs += ' -LegacyStart' }
$testHost = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList $hostArgs -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $fixture 'host-error.txt')
$job = [IntPtr]::Zero
$app = $null
try {
    $job = [QuotaLaunchTestJob]::Create($testHost.Handle)
    [IO.File]::WriteAllText((Join-Path $fixture 'go'),'ready')
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath (Join-Path $fixture 'child-id'))) {
        if ($testHost.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw ('Test host failed: ' + (Get-Content -LiteralPath (Join-Path $fixture 'host-error.txt') -Raw)) }
        Start-Sleep -Milliseconds 50
    }
    $childId = [int](Get-Content -LiteralPath (Join-Path $fixture 'child-id') -Raw)
    $app = Get-Process -Id $childId
    $inHostJob = [QuotaLaunchTestJob]::Contains($job,$app.Handle)
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$childId").ParentProcessId
    $parentName = (Get-Process -Id $parent -ErrorAction SilentlyContinue).ProcessName
    [QuotaLaunchTestJob]::CloseHandle($job) | Out-Null
    $job = [IntPtr]::Zero
    if (-not $testHost.WaitForExit(5000)) { throw 'The simulated deployment host did not exit.' }
    if (-not $app.WaitForExit(45000)) { throw 'The isolated app did not finish its checks.' }
    $resultFile = Join-Path $dataRoot 'result.json'
    $completed = Test-Path -LiteralPath $resultFile
    [pscustomobject]@{Mode=$(if($LegacyStart){'Legacy'}else{'Explorer'});Parent=$parentName;InHostJob=$inHostJob;CompletedAfterHostExit=$completed;Fixture=$fixture}
    if ($inHostJob -or -not $completed) { throw 'FAIL: the app was tied to its deployment host and did not survive host exit.' }
    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    if (-not $result.success) { throw 'The app survived host exit but its native UI checks failed.' }
    Write-Output 'PASS: the app completed its native UI checks after the deployment host and its job had exited.'
} finally {
    if ($job -ne [IntPtr]::Zero) { [QuotaLaunchTestJob]::CloseHandle($job) | Out-Null }
    if ($null -ne $app) { $app.Dispose() }
    $testHost.Dispose()
}
