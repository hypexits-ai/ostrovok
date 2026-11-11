# Builds a ready-to-share archive: dist\Ostrovok-<version>-win-x64.zip
# Inside: app\ (the program, self-contained - no .NET needed on the target PC), Install.cmd, Uninstall.cmd, README.txt
# and the text guides from app\release (*.txt, e.g. which Windows settings to turn on).
# Usage: powershell -ExecutionPolicy Bypass -File app\pack.ps1
# (ASCII only on purpose: Windows PowerShell 5.1 misreads UTF-8 scripts without a BOM.)
$ErrorActionPreference = 'Stop'
$env:PATH = "C:\Program Files\dotnet;" + $env:PATH
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$root    = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'Ostrovok\Ostrovok.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name    = "Ostrovok-$version-win-x64"
$publish = Join-Path $env:TEMP 'ostrovok-build\publish-release'
$stage   = Join-Path $env:TEMP "ostrovok-build\$name"
$dist    = Join-Path $root 'dist'

Write-Host "Building $name ..."
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'app') | Out-Null
Copy-Item (Join-Path $publish '*') (Join-Path $stage 'app') -Recurse
Get-ChildItem (Join-Path $stage 'app') -Filter *.pdb -Recurse | Remove-Item

$rel = Join-Path $PSScriptRoot 'release'
Copy-Item (Join-Path $rel 'setup.ps1'), (Join-Path $rel 'uninstall.ps1') $stage
foreach ($cmd in 'Install.cmd', 'Uninstall.cmd') {
    # cmd.exe wants CRLF line endings
    $text = (Get-Content (Join-Path $rel $cmd) -Raw) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText((Join-Path $stage $cmd), $text, (New-Object Text.ASCIIEncoding))
}
# README as UTF-8 with BOM so Notepad shows the Russian text correctly everywhere
$readme = Get-Content (Join-Path $root 'README.md') -Raw -Encoding UTF8
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme, (New-Object Text.UTF8Encoding $true))
# text guides: UTF-8 with BOM and CRLF, so any Notepad opens them right
Get-ChildItem $rel -Filter *.txt | ForEach-Object {
    $text = (Get-Content $_.FullName -Raw -Encoding UTF8) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText((Join-Path $stage $_.Name), $text, (New-Object Text.UTF8Encoding $true))
}

New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
"{0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
