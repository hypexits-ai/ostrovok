# Builds Ostrovok, installs it to %LOCALAPPDATA%\Programs\Ostrovok, turns on start with Windows and launches it.
# Usage: powershell -ExecutionPolicy Bypass -File install.ps1
# (ASCII only on purpose: Windows PowerShell 5.1 misreads UTF-8 scripts without a BOM.)
$ErrorActionPreference = 'Stop'
$env:PATH = "C:\Program Files\dotnet;" + $env:PATH
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$project = Join-Path $PSScriptRoot 'Ostrovok\Ostrovok.csproj'
$publish = Join-Path $env:TEMP 'ostrovok-build\publish'
$dest    = Join-Path $env:LOCALAPPDATA 'Programs\Ostrovok'

Write-Host 'Building...'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Get-Process Ostrovok -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $publish '*') $dest -Recurse -Force

$exe = Join-Path $dest 'Ostrovok.exe'
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Ostrovok' -Value "`"$exe`""

Start-Process $exe
Write-Host "Installed: $exe"
