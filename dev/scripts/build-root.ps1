# Rebuild the root launcher and packaged Windows application.
$ErrorActionPreference = 'Stop'
$devRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $devRoot '..'))
$buildRoot = Join-Path $env:LOCALAPPDATA ('Temp\CPT-root-build-' + (Get-Date -Format yyyyMMdd-HHmmss))
$publishRoot = Join-Path $buildRoot 'publish'
$appRoot = Join-Path $devRoot 'app'
dotnet publish (Join-Path $devRoot 'src\CPT.Shell\CPT.Shell.csproj') --configuration Release --runtime win-x64 --self-contained true -t:Rebuild -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false --artifacts-path $buildRoot -o $publishRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
Get-ChildItem -LiteralPath $publishRoot | Where-Object { $_.Extension -notin @('.xml','.pdb') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $appRoot -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $appRoot 'scripts') -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'bootstrap*.ps1') (Join-Path $appRoot 'scripts') -Force
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll "/out:$repoRoot\CPT.exe" (Join-Path $devRoot 'launcher\Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
Get-Item (Join-Path $repoRoot 'CPT.exe'), (Join-Path $appRoot 'CPT.Shell.exe') | Select-Object FullName,Length
