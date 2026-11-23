$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $root (".build/native-wallpaper/" + [guid]::NewGuid().ToString("N"))
$package = Join-Path $staging "LyricWallpaper"
$release = Join-Path $root "release"
$destination = Join-Path $release "LyricWallpaper-win-x64.exe"

& dotnet publish (Join-Path $root "native-wallpaper/NativeWallpaper.csproj") -c Release `
  -r win-x64 --self-contained true -o $package `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=embedded
if ($LASTEXITCODE) { throw "Native wallpaper publish failed ($LASTEXITCODE)" }

$files = @(Get-ChildItem -LiteralPath $package -Force)
if ($files.Count -ne 1 -or $files[0].PSIsContainer -or $files[0].Name -ne "NativeWallpaper.exe") {
  throw "Expected a single NativeWallpaper.exe in the publish directory."
}
New-Item -Path $release -ItemType Directory -Force | Out-Null
Move-Item -LiteralPath $files[0].FullName -Destination $destination -Force
Write-Output $destination
