$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $root (".build/native-wallpaper/" + [guid]::NewGuid().ToString("N"))
$package = Join-Path $staging "LyricWallpaper"
$release = Join-Path $root "release"
$destination = Join-Path $release "LyricWallpaper-win-x64.exe"

& (Join-Path $PSScriptRoot "build-lyric-server.ps1") -Worker
$bundled = Join-Path $root ".build/native-worker/dist/LyricWorker"
if (-not (Test-Path -LiteralPath (Join-Path $bundled "LyricWorker.exe"))) {
  throw "Missing packaged lyrics worker."
}
Copy-Item -LiteralPath (Join-Path $root "LICENSE"),(Join-Path $root "THIRD_PARTY_NOTICES.md") -Destination $bundled
New-Item -Path $staging -ItemType Directory -Force | Out-Null
$workerBundle = Join-Path $staging "LyricWorker.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($bundled, $workerBundle)

& dotnet publish (Join-Path $root "native-wallpaper/NativeWallpaper.csproj") -c Release `
  -r win-x64 --self-contained true -o $package `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=embedded "-p:WorkerBundle=$workerBundle"
if ($LASTEXITCODE) { throw "Native wallpaper publish failed ($LASTEXITCODE)" }

$files = @(Get-ChildItem -LiteralPath $package -Force)
if ($files.Count -ne 1 -or $files[0].PSIsContainer -or $files[0].Name -ne "NativeWallpaper.exe") {
  throw "Expected a single NativeWallpaper.exe in the publish directory."
}
New-Item -Path $release -ItemType Directory -Force | Out-Null
Move-Item -LiteralPath $files[0].FullName -Destination $destination -Force
Write-Output $destination
