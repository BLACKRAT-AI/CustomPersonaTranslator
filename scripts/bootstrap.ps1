# CPT bootstrap — downloads Piper, a default voice, whisper-cli + a base model.
# Run from repo root:  pwsh -File scripts/bootstrap.ps1
$ErrorActionPreference = "Stop"

$root = Resolve-Path "$PSScriptRoot/.."
$toolsDir   = Join-Path $root "tools"
$piperDir   = Join-Path $toolsDir "piper"
$whisperDir = Join-Path $toolsDir "whisper"
$ytDir      = Join-Path $toolsDir "youtube"
$llamaDir   = Join-Path $toolsDir "llama"
$piperModelsDir = Join-Path $piperDir "models"
$whisperModelsDir = Join-Path $whisperDir "models"
$llamaModelsDir   = Join-Path $llamaDir "models"

New-Item -ItemType Directory -Force $piperDir, $whisperDir, $ytDir, $llamaDir, $piperModelsDir, $whisperModelsDir, $llamaModelsDir | Out-Null

function Get-IfMissing($url, $outPath) {
  if (Test-Path $outPath) { Write-Host "skip $outPath (exists)"; return }
  Write-Host "downloading $url"
  Invoke-WebRequest -Uri $url -OutFile $outPath
}

# --- Piper (Windows x64) ---------------------------------------------------
$piperZip = Join-Path $piperDir "piper.zip"
Get-IfMissing "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip" $piperZip
if (-not (Test-Path (Join-Path $piperDir "piper.exe"))) {
  Expand-Archive -Force $piperZip $piperDir
  # Piper zip extracts into a "piper" subfolder.
  $inner = Join-Path $piperDir "piper"
  if (Test-Path $inner) { Get-ChildItem $inner | Move-Item -Destination $piperDir -Force; Remove-Item $inner -Recurse -Force }
}

# Default voice (en_US Amy, medium).
$voiceBase = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium"
Get-IfMissing "$voiceBase/en_US-amy-medium.onnx"       (Join-Path $piperModelsDir "en_US-amy-medium.onnx")
Get-IfMissing "$voiceBase/en_US-amy-medium.onnx.json"  (Join-Path $piperModelsDir "en_US-amy-medium.onnx.json")

# --- whisper.cpp (BLAS CPU build) ------------------------------------------
$whisperModel = Join-Path $whisperModelsDir "ggml-base.en.bin"
Get-IfMissing "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin" $whisperModel

$whisperZip = Join-Path $whisperDir "whisper-bin.zip"
$whisperExe = Join-Path $whisperDir "whisper-cli.exe"
if (-not (Test-Path $whisperExe)) {
  Get-IfMissing "https://github.com/ggml-org/whisper.cpp/releases/download/v1.8.4/whisper-blas-bin-x64.zip" $whisperZip
  Expand-Archive -Force $whisperZip $whisperDir
  $releaseDir = Join-Path $whisperDir "Release"
  if (Test-Path $releaseDir) {
    Get-ChildItem $releaseDir | Move-Item -Destination $whisperDir -Force
    Remove-Item $releaseDir -Recurse -Force
  }
}

# --- yt-dlp + ffmpeg (for YouTube persona import) --------------------------
$ytDlp = Join-Path $ytDir "yt-dlp.exe"
Get-IfMissing "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" $ytDlp

$ffmpegExe = Join-Path $ytDir "ffmpeg.exe"
if (-not (Test-Path $ffmpegExe)) {
  $ffmpegZip = Join-Path $ytDir "ffmpeg.zip"
  Get-IfMissing "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" $ffmpegZip
  $extract = Join-Path $ytDir "_ffmpeg_extract"
  Expand-Archive -Force $ffmpegZip $extract
  $found = Get-ChildItem $extract -Recurse -Filter ffmpeg.exe | Select-Object -First 1
  if ($found) { Copy-Item $found.FullName $ffmpegExe -Force }
  Remove-Item $extract -Recurse -Force
  Remove-Item $ffmpegZip -Force
}

# --- llama.cpp (replaces Ollama dependency) --------------------------------
# Detect NVIDIA so we can pull the CUDA build when possible.
$hasCuda = $false
try {
  $gpuInfo = & cmd /c "wmic path win32_VideoController get name 2>&1"
  if ($gpuInfo -match "NVIDIA|GeForce|RTX|Quadro") { $hasCuda = $true }
} catch {}

$llamaExe = Join-Path $llamaDir "llama-server.exe"
if (-not (Test-Path $llamaExe)) {
  $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases/latest' -Headers @{ 'User-Agent' = 'cpt-setup' }
  $assetName = if ($hasCuda) { 'llama-*-bin-win-cuda-*-x64.zip' } else { 'llama-*-bin-win-cpu-x64.zip' }
  $asset = $rel.assets | Where-Object { $_.name -like $assetName } | Select-Object -First 1
  if (-not $asset) {
    # Fallback to any Windows x64 build.
    $asset = $rel.assets | Where-Object { $_.name -like '*win*x64*.zip' -and $_.name -notlike '*arm*' } | Select-Object -First 1
  }
  if (-not $asset) { Write-Host "ERROR: no llama.cpp Windows release asset found."; exit 1 }
  $zip = Join-Path $llamaDir "llama.zip"
  Write-Host "downloading $($asset.browser_download_url)"
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip
  Expand-Archive -Force $zip $llamaDir
  Remove-Item $zip -Force
  # llama.cpp releases sometimes nest binaries one folder deep; flatten if so.
  $nested = Get-ChildItem $llamaDir -Directory | Select-Object -First 1
  if ($nested -and (Test-Path (Join-Path $nested.FullName "llama-server.exe"))) {
    Get-ChildItem $nested.FullName | Move-Item -Destination $llamaDir -Force
    Remove-Item $nested.FullName -Recurse -Force
  }
}

# Default chat model: Qwen 2.5 3B Instruct, Q4_K_M (~1.9 GB, fast on CPU and GPU).
$modelFile = "qwen2.5-3b-instruct-q4_k_m.gguf"
$modelPath = Join-Path $llamaModelsDir $modelFile
$modelUrl  = "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf"
Get-IfMissing $modelUrl $modelPath

# --- Patch settings.json ---------------------------------------------------
$localAppData = $env:LOCALAPPDATA
$settingsDir  = Join-Path $localAppData "CustomPersonaTranslator"
$settingsPath = Join-Path $settingsDir "settings.json"
New-Item -ItemType Directory -Force $settingsDir | Out-Null

if (Test-Path $settingsPath) {
  $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
} else {
  $settings = [pscustomobject]@{}
}
function Set-Prop($obj, $name, $value) {
  if ($obj.PSObject.Properties.Name -contains $name) { $obj.$name = $value }
  else { $obj | Add-Member -MemberType NoteProperty -Name $name -Value $value }
}
Set-Prop $settings "PiperPath"        (Join-Path $piperDir "piper.exe")
Set-Prop $settings "PiperModelsDir"   $piperModelsDir
Set-Prop $settings "WhisperPath"      (Join-Path $whisperDir "whisper-cli.exe")
Set-Prop $settings "WhisperModelPath" $whisperModel
Set-Prop $settings "YtDlpPath"        $ytDlp
Set-Prop $settings "FfmpegPath"       $ffmpegExe
Set-Prop $settings "LlamaCppExe"      $llamaExe
Set-Prop $settings "LlamaCppModel"    $modelPath
Set-Prop $settings "LlamaCppPort"     18080
Set-Prop $settings "LlamaCppGpuLayers" $(if ($hasCuda) { 99 } else { 0 })
$settings | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsPath -Encoding utf8

Write-Host ""
Write-Host "=== bootstrap complete ==="
Write-Host "settings written to: $settingsPath"
Write-Host "LLM:     $modelPath"
Write-Host "Piper:   $piperDir"
Write-Host "Whisper: $whisperDir"
Write-Host "YT:      $ytDir"
Write-Host ""
Write-Host "Everything is local. No external services required."
