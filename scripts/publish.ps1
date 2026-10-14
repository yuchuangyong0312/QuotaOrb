$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdkExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
& (Join-Path $PSScriptRoot 'bootstrap.ps1')
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\QuotaOrb\QuotaOrb.csproj') -Raw)).Project.PropertyGroup.Version
$destination = Join-Path $projectRoot ('dist\QuotaOrb-'+$version)
& $sdkExe publish (Join-Path $projectRoot 'src\QuotaOrb\QuotaOrb.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:IncludeSourceRevisionInInformationalVersion=false -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (Test-Path -LiteralPath (Join-Path $destination 'runtime')) { throw 'Use a clean release directory; Codex runtime files must not be bundled.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $destination '使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $destination -Recurse -Force
$installerText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'install-local.ps1'))
[IO.File]::WriteAllText((Join-Path $destination 'install-local.ps1'),$installerText,[Text.UTF8Encoding]::new($true))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.cmd') -Destination (Join-Path $destination '双击安装.cmd') -Force
Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object { $_.Extension -ne '.pdb' -and $_.Name -ne 'checksums.json' } | Get-FileHash -Algorithm SHA256 | Select-Object @{Name='Path';Expression={$_.Path.Substring($destination.Length+1)}},Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'checksums.json') -Encoding utf8
Write-Output $destination
