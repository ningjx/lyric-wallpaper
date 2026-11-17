param(
  [switch]$Clean,
  [switch]$Worker
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$distRoot = Join-Path $projectRoot $(if ($Worker) { ".build/native-worker/dist" } else { "release" })
$trayData = "$(Join-Path $projectRoot 'server/assets/tray.svg');server/assets"
$appIcon = Join-Path $projectRoot "server/assets/LyricServer.ico"

if ($Clean -and (Test-Path $distRoot)) {
  $rootPath = (Resolve-Path -LiteralPath $projectRoot).ProviderPath.TrimEnd('\')
  $cleanPath = (Resolve-Path -LiteralPath $distRoot).ProviderPath
  if (-not $cleanPath.StartsWith("$rootPath\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Build output is outside the project directory: $cleanPath"
  }
  Remove-Item -LiteralPath $cleanPath -Recurse -Force
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

  if ($Worker) {
    & $python -m pip install --disable-pip-version-check -r server/requirements.txt 'pyinstaller>=6.20,<7'
    if ($LASTEXITCODE) { throw "Installing worker dependencies failed ($LASTEXITCODE)" }
    & $python -m PyInstaller --noconfirm --clean --onedir --console `
      --name LyricWorker --paths $projectRoot --collect-submodules winrt `
      --distpath $distRoot `
      --workpath (Join-Path $projectRoot ".build/native-worker/work") `
      --specpath (Join-Path $projectRoot ".build/native-worker") `
      server/worker.py
  } else {
    & $python -m pip install -r server/requirements-desktop.txt
    if ($LASTEXITCODE) { throw "Installing tray dependencies failed ($LASTEXITCODE)" }
    & $python -m PyInstaller --noconfirm --clean --onefile --windowed `
      --name LyricServer `
      --icon $appIcon `
      --add-data $trayData `
      --collect-submodules winrt `
      --distpath $distRoot `
      --workpath (Join-Path $projectRoot ".build/lyric-server") `
      --specpath (Join-Path $projectRoot ".build") `
      desktop_entry.py
  }
  if ($LASTEXITCODE) { throw "PyInstaller failed ($LASTEXITCODE)" }
} finally {
  if ($null -ne $originalPath) {
    $env:PATH = $originalPath
  }
  Pop-Location
}
