# Build the installer end-to-end:
#   1. dotnet publish CPT.Shell as self-contained win-x64 single-file binary
#   2. Compile installer\CustomPersonaTranslator.iss with Inno Setup (ISCC.exe)
#   3. Output: installer\dist\CPT-Setup-<version>.exe
#
# Run from repo root:  pwsh -File installer\build.ps1
$ErrorActionPreference = "Stop"

$root      = Resolve-Path "$PSScriptRoot/.."
$installer = Join-Path $root "installer"
$publish   = Join-Path $installer "build\publish"
$dist      = Join-Path $installer "dist"
$iss       = Join-Path $installer "CustomPersonaTranslator.iss"

New-Item -ItemType Directory -Force $publish, $dist | Out-Null

# 1. Publish.
Write-Host "==> dotnet publish (self-contained, single-file, win-x64)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "src\CPT.Shell\CPT.Shell.csproj") `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -p:DebugSymbols=false `
  -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Drop pdb / xml.
Get-ChildItem $publish -Include *.pdb,*.xml -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

# 2. Locate Inno Setup compiler.
$iscc = @(
  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
  "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  Write-Host "ERROR: Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php"
  exit 1
}
Write-Host "==> Inno Setup at $iscc"

# 3. Compile.
Write-Host "==> Compiling installer"
& $iscc "/Qp" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }

$out = Get-ChildItem $dist -Filter "CPT-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($out) {
  Write-Host ""
  Write-Host "=== Installer built ==="
  Write-Host $out.FullName
  Write-Host ("Size: {0:N1} MB" -f ($out.Length/1MB))
} else {
  Write-Host "WARNING: installer .exe not found in $dist"
}
