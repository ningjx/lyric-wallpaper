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
  python -m pip install -r server/requirements-desktop.txt
  python -m PyInstaller --noconfirm --clean --onefile --windowed `
    --name LyricServer `
    --icon $appIcon `
    --add-data $trayData `
    --collect-submodules winrt `
    --distpath $distRoot `
    --workpath (Join-Path $projectRoot ".build/lyric-server") `
    --specpath (Join-Path $projectRoot ".build") `
    desktop_entry.py
} finally {
  Pop-Location
}
