$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $root (".build/native-wallpaper/" + [guid]::NewGuid().ToString("N"))
$package = Join-Path $staging "LyricWallpaper"
$release = Join-Path $root "release"
$zip = Join-Path $release "LyricWallpaper-win-x64.zip"

& (Join-Path $PSScriptRoot "build-lyric-server.ps1") -Worker
& dotnet publish (Join-Path $root "native-wallpaper/NativeWallpaper.csproj") -c Release `
  -r win-x64 --self-contained true -o $package
if ($LASTEXITCODE) { throw "Native wallpaper publish failed ($LASTEXITCODE)" }

$bundled = Join-Path $root ".build/native-worker/dist/LyricWorker"
if (-not (Test-Path -LiteralPath (Join-Path $bundled "LyricWorker.exe"))) {
  throw "Missing packaged lyrics worker."
}
Copy-Item -LiteralPath $bundled -Destination (Join-Path $package "worker") -Recurse
Copy-Item -LiteralPath (Join-Path $root "LICENSE"),(Join-Path $root "THIRD_PARTY_NOTICES.md") -Destination $package
New-Item -Path $release -ItemType Directory -Force | Out-Null
$archive = Join-Path $staging "LyricWallpaper-win-x64.zip"
Compress-Archive -LiteralPath $package -DestinationPath $archive
Move-Item -LiteralPath $archive -Destination $zip -Force
Write-Output $zip
