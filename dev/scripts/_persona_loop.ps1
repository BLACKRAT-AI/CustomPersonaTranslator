# Run the persona-pipeline smoke 5 times and measure each output's F0 +
# Chatterbox speaker-embedding similarity to the Jarvis reference. Confirms
# the WPF-side wrapper is still producing properly-cloned audio after fixes.
$py = "$env:LOCALAPPDATA\Programs\CustomPersonaTranslator\tools\voiceclone\python\python.exe"
$results = @()
for ($i = 0; $i -lt 10; $i++) {
    Remove-Item "$env:TEMP\cpt_persona_jarvis.wav","$env:TEMP\cpt_metric.txt" -Force -ErrorAction SilentlyContinue
    dotnet run --project tests/CPT.Smoke --no-build -- persona jarvis 2>&1 | Out-Null
    & $py ".\scripts\_metric.py" 2>$null
    $line = Get-Content "$env:TEMP\cpt_metric.txt" -ErrorAction SilentlyContinue
    $parts = $line -split '\|'
    Write-Host ("app run {0}  F0 = {1,5} Hz   speaker-cos vs Jarvis = {2}" -f $i, $parts[0], $parts[1])
}
