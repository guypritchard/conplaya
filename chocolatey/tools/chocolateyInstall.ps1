$ErrorActionPreference = 'Stop'

$packageName = 'conplaya'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$packageVersion = $env:ChocolateyPackageVersion
$releaseVersionFile = Join-Path $toolsDir 'release-version.txt'
$releaseVersion = $null

if (Test-Path $releaseVersionFile) {
    $releaseVersion = (Get-Content -Path $releaseVersionFile -Raw).Trim()
}

if (-not $releaseVersion) {
    $releaseVersion = $packageVersion
}

$url = "https://github.com/guypritchard/conplaya/releases/download/v$releaseVersion/conplaya-$releaseVersion.zip"
$installDir = Join-Path $toolsDir 'app'

$checksumUrl = "$url.sha256"
$checksumFile = Join-Path $env:TEMP "$packageName-$packageVersion.sha256"

if (Test-Path $checksumFile) {
    Remove-Item $checksumFile -Force
}

Get-ChocolateyWebFile -PackageName $packageName `
    -FileFullPath $checksumFile `
    -Url $checksumUrl

$checksum = (Get-Content -Path $checksumFile -Raw).Trim()
if (-not $checksum) {
    throw "Failed to download checksum for version $packageVersion."
}

Remove-Item $checksumFile -Force -ErrorAction SilentlyContinue

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
