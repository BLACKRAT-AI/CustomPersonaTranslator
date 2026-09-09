# Optional bootstrap — installs Chatterbox voice cloning into a portable Python.
#
# Self-contained: downloads a Python 3.11 embeddable distribution into
# tools/voiceclone/python and installs torch + chatterbox-tts there. No
# system Python required.
#
# This script is idempotent and ends with a verification step that imports
# chatterbox.tts. If verification fails the script exits non-zero so the
# in-app installer dialog can show the failure (and settings.json is NOT
# updated to point at a broken install).
#
# Run from repo root:  pwsh -File scripts/bootstrap_chatterbox.ps1

$ErrorActionPreference = "Stop"
$root  = Resolve-Path "$PSScriptRoot/.."
$vcDir = Join-Path $root "tools\voiceclone"
$pyDir = Join-Path $vcDir "python"
$script = Join-Path $vcDir "clone_server.py"
$siteDir = Join-Path $pyDir "Lib\site-packages"
New-Item -ItemType Directory -Force $vcDir, $pyDir | Out-Null

$pyExe = Join-Path $pyDir "python.exe"

# 1. Portable Python.
if (-not (Test-Path $pyExe)) {
  $pyUrl = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip"
  $pyZip = Join-Path $vcDir "python-embed.zip"
  Write-Host "[1/4] Downloading Python 3.11 embeddable…"
  Invoke-WebRequest -Uri $pyUrl -OutFile $pyZip
  Expand-Archive -Force $pyZip $pyDir
  Remove-Item $pyZip -Force
} else {
  Write-Host "[1/4] Python embed already present."
}

# 2. Rewrite python._pth from scratch with site-packages enabled.
#    NOTE: must be ASCII / UTF8-no-BOM. PowerShell's Set-Content -Encoding utf8
#    prepends a BOM that corrupts the embeddable Python parser (it pollutes
#    the first sys.path entry with ﻿, breaking encodings import).
$pth = Get-ChildItem $pyDir -Filter "python*._pth" | Select-Object -First 1
if ($pth) {
  $pthContent = "python311.zip`r`n.`r`nLib\site-packages`r`nimport site`r`n"
  [System.IO.File]::WriteAllText($pth.FullName, $pthContent, [System.Text.UTF8Encoding]::new($false))
  Write-Host "[2/4] Rewrote $($pth.Name) (no BOM, site enabled)."
}

# 3. Bootstrap pip if not present.
$pipMod = Join-Path $siteDir "pip"
if (-not (Test-Path $pipMod)) {
  $getPip = Join-Path $vcDir "get-pip.py"
  Write-Host "[3/4] Downloading get-pip.py + installing pip…"
  Invoke-WebRequest -Uri "https://bootstrap.pypa.io/get-pip.py" -OutFile $getPip
  & $pyExe $getPip --no-warn-script-location
  if ($LASTEXITCODE -ne 0) { Remove-Item $getPip -Force; throw "get-pip failed (exit $LASTEXITCODE)" }
  Remove-Item $getPip -Force
} else {
  Write-Host "[3/4] pip already installed."
}

# 4. Install torch + chatterbox-tts. Detect NVIDIA so we can pick the CUDA wheel.
$hasCuda = $false
try {
  $gpuInfo = & cmd /c "wmic path win32_VideoController get name 2>&1"
  if ($gpuInfo -match "NVIDIA|GeForce|RTX|Quadro") { $hasCuda = $true }
} catch {}
Write-Host ("[4/4] Installing deps  ({0} wheel)…" -f ($(if ($hasCuda) {"CUDA"} else {"CPU"})))

# Pin setuptools <80: chatterbox-tts depends on resemble-perth, which still
# uses pkg_resources (dropped in setuptools 81+). Without this pin the install
# completes but ChatterboxTTS.from_pretrained() blows up at runtime with
# "'NoneType' object is not callable" because perth silently sets its main
# class to None when pkg_resources is missing.
& $pyExe -m pip install --upgrade --no-warn-script-location pip "setuptools<80" wheel
if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed" }

# Install Chatterbox FIRST (it pins torch>=2.6 and would otherwise overwrite
# a pre-installed CUDA wheel with a plain CPU one from PyPI).
& $pyExe -m pip install --upgrade --no-warn-script-location chatterbox-tts soundfile
if ($LASTEXITCODE -ne 0) { throw "chatterbox-tts install failed" }

# Now force-reinstall torch + torchaudio from the CUDA index so we get GPU support.
# cu124 covers torch 2.6+ for NVIDIA cards with driver >= 550.
if ($hasCuda) {
  & $pyExe -m pip install --force-reinstall --no-warn-script-location `
        torch torchaudio --index-url https://download.pytorch.org/whl/cu124
  if ($LASTEXITCODE -ne 0) { throw "CUDA torch install failed" }
}

# 5. Verification step — confirm pkg_resources, torch, chatterbox AND
#    the underlying perth_net watermarker class are all reachable. Catches
#    the silent "perth = None" failure that import-only checks miss.
#
# Important: write probe to a real .py file and ignore Python warnings via
# `-W ignore`. Python's pkg_resources prints a DeprecationWarning to stderr,
# and PowerShell with $ErrorActionPreference=Stop treats native stderr as a
# terminating error — which aborts this script even though the import was
# successful. So we suppress warnings and only consult $LASTEXITCODE + stdout.
Write-Host "Verifying chatterbox + perth + pkg_resources…"
$probePath = Join-Path $vcDir "_verify.py"
@"
import warnings; warnings.filterwarnings('ignore')
import pkg_resources
import torch, torchaudio
import perth
assert perth.PerthImplicitWatermarker is not None, 'perth.PerthImplicitWatermarker is None'
from chatterbox.tts import ChatterboxTTS
print('ok', torch.__version__, 'cuda', torch.cuda.is_available())
"@ | Set-Content -Path $probePath -Encoding ascii

# Suppress non-terminating PS errors so stderr noise doesn't blow up the script.
$prev = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$verifyOut = & $pyExe -W ignore $probePath 2>$null
$verifyExit = $LASTEXITCODE
$ErrorActionPreference = $prev
Remove-Item $probePath -Force -ErrorAction SilentlyContinue

if ($verifyExit -ne 0 -or "$verifyOut" -notmatch '^ok') {
  Write-Host "VERIFICATION FAILED (exit=$verifyExit):"
  Write-Host "$verifyOut"
  throw "chatterbox stack is not functional. Install is broken; settings will not be updated."
}
Write-Host "Verification OK: $verifyOut"

# 6. Patch settings.json now that we know the install actually works.
$settingsPath = Join-Path $env:LOCALAPPDATA "CustomPersonaTranslator\settings.json"
$settingsDir  = Split-Path $settingsPath
New-Item -ItemType Directory -Force $settingsDir | Out-Null
$settings = if (Test-Path $settingsPath) {
  Get-Content $settingsPath -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
function Set-Prop($obj, $name, $value) {
  if ($obj.PSObject.Properties.Name -contains $name) { $obj.$name = $value }
  else { $obj | Add-Member -MemberType NoteProperty -Name $name -Value $value }
}
Set-Prop $settings "ChatterboxPython" $pyExe
Set-Prop $settings "ChatterboxScript" $script
$settings | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsPath -Encoding utf8

Write-Host ""
Write-Host "=== Chatterbox bootstrap complete ==="
Write-Host "Python:  $pyExe"
Write-Host "Script:  $script"
Write-Host ""
Write-Host "First synthesis will download the ~3 GB Chatterbox model (one-time)."
