# Removes Ostrovok: stops it, turns off start with Windows, deletes the shortcut and the program.
# Settings and the log in %LOCALAPPDATA%\Ostrovok are kept (delete that folder too for a clean removal).
Get-Process Ostrovok -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Ostrovok' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Ostrovok.lnk') -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\Ostrovok') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Ostrovok removed.' -ForegroundColor Green
