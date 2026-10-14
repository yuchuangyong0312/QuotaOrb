$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $projectRoot '.tools'
$sdkDir = Join-Path $toolsDir 'dotnet'
$sdkVersion = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $sdkDir ('sdk\'+$sdkVersion)))) {
    $metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $release = $metadata.releases | Where-Object { $_.sdk.version -eq $sdkVersion -or $_.sdks.version -contains $sdkVersion } | Select-Object -First 1
    $sdk = @($release.sdk) + @($release.sdks) | Where-Object version -eq $sdkVersion | Select-Object -First 1
    $download = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') } | Select-Object -First 1
    if (-not $download) { throw 'No official Windows x64 SDK package found.' }
    $archive = Join-Path $toolsDir 'dotnet-sdk.zip'
    Write-Output "Downloading .NET SDK $sdkVersion locally..."
    Invoke-WebRequest -Uri $download.url -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $download.hash) { throw 'SDK checksum mismatch.' }
    New-Item -ItemType Directory -Path $sdkDir -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $sdkDir -Force
    Write-Output 'SDK verified and extracted.'
}
& (Join-Path $sdkDir 'dotnet.exe') --version
