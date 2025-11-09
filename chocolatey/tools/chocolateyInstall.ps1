$ErrorActionPreference = 'Stop'

$packageName = 'conplaya'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$packageVersion = $env:ChocolateyPackageVersion

$url = "https://github.com/guypritchard/conplaya/releases/download/v$packageVersion/conplaya-$packageVersion.zip"
$installDir = Join-Path $toolsDir 'app'

if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force
}

Install-ChocolateyZipPackage -PackageName $packageName `
    -Url $url `
    -UnzipLocation $installDir

$exePath = Join-Path $installDir 'play.exe'
if (-not (Test-Path $exePath)) {
    throw "Expected play.exe within archive, but it was not found."
}

Install-BinFile -Name 'play' -Path $exePath
