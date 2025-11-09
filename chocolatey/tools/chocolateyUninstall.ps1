$ErrorActionPreference = 'Stop'

Uninstall-BinFile -Name 'play'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$installDir = Join-Path $toolsDir 'app'
if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force
}
