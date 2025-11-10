$ErrorActionPreference = 'Stop'

$packageName = 'conplaya'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$packageVersion = $env:ChocolateyPackageVersion

$url = "https://github.com/guypritchard/conplaya/releases/download/v$packageVersion/conplaya-$packageVersion.zip"
$installDir = Join-Path $toolsDir 'app'

$checksums = @{
    '0.5.6' = '8CC76072035B71C5EE2F68A8799A2D7042D04245FA4D05F063ED89468871E8FA'
}

if (-not $checksums.ContainsKey($packageVersion)) {
    throw "No checksum registered for version $packageVersion. Update chocolateyInstall.ps1."
}

$checksum = $checksums[$packageVersion]

if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force
}

Install-ChocolateyZipPackage -PackageName $packageName `
    -Url $url `
    -Checksum $checksum `
    -ChecksumType 'sha256' `
    -UnzipLocation $installDir

$exePath = Join-Path $installDir 'play.exe'
if (-not (Test-Path $exePath)) {
    throw "Expected play.exe within archive, but it was not found."
}

Install-BinFile -Name 'play' -Path $exePath
