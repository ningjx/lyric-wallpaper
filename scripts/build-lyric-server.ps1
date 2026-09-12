param(
  [switch]$Clean
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$distRoot = Join-Path $projectRoot "release"
$trayData = "$(Join-Path $projectRoot 'server/assets/tray.svg');server/assets"
$appIcon = Join-Path $projectRoot "server/assets/LyricServer.ico"

if ($Clean -and (Test-Path $distRoot)) {
  Remove-Item -LiteralPath $distRoot -Recurse -Force
}

Push-Location $projectRoot
try {
  $python = (Get-Command python -ErrorAction Stop).Source

  # PyInstaller 会沿 PATH 解析原生依赖。Codex 附带的 Poppler ICU DLL 与 Qt 的
  # ICU ABI 不兼容；若被误收进单文件包，QtCore 会在启动时加载失败。
  $originalPath = $env:PATH
  $pythonDir = Split-Path -Parent $python
  $systemRoot = $env:SystemRoot
  $env:PATH = @(
    $pythonDir
    (Join-Path $systemRoot "System32")
    $systemRoot
    (Join-Path $systemRoot "System32\\Wbem")
  ) -join ';'

  & $python -m pip install -r server/requirements-desktop.txt
  & $python -m PyInstaller --noconfirm --clean --onefile --windowed `
    --name LyricServer `
    --icon $appIcon `
    --add-data $trayData `
    --collect-submodules winrt `
    --distpath $distRoot `
    --workpath (Join-Path $projectRoot ".build/lyric-server") `
    --specpath (Join-Path $projectRoot ".build") `
    desktop_entry.py
} finally {
  if ($null -ne $originalPath) {
    $env:PATH = $originalPath
  }
  Pop-Location
}
