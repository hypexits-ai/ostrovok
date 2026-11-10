# Ostrovok setup: copies the program to %LOCALAPPDATA%\Programs\Ostrovok, adds it to start with Windows,
# creates a Start menu shortcut and launches it. No administrator rights needed.
# (ASCII only on purpose: Windows PowerShell 5.1 misreads UTF-8 scripts without a BOM.)
$ErrorActionPreference = 'Stop'
$src  = Join-Path $PSScriptRoot 'app'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Ostrovok'
$exe  = Join-Path $dest 'Ostrovok.exe'

Write-Host ''
Write-Host '  Ostrovok - island for Windows' -ForegroundColor Cyan
Write-Host ''

# WebView2 runtime draws the island. It ships with Windows 11; Windows 10 may need it.
$wv = @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') |
      Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $wv) {
    Write-Host 'Microsoft Edge WebView2 Runtime is missing. Opening the download page...' -ForegroundColor Yellow
    Start-Process 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
    Write-Host 'Install it, then run Install.cmd again.'
    exit 1
}

Write-Host 'Closing a running copy...'
Get-Process Ostrovok -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "Copying to $dest ..."
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $src '*') $dest -Recurse -Force
Get-ChildItem $dest -Recurse -File | Unblock-File

Write-Host 'Turning on start with Windows...'
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Ostrovok' -Value "`"$exe`""

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Ostrovok.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = $dest; $s.Description = 'Ostrovok'; $s.Save()

Start-Process $exe
Write-Host ''
Write-Host 'Done! The island is at the top of the screen.' -ForegroundColor Green
Write-Host 'Options: right-click the Ostrovok icon in the tray (next to the clock).'
