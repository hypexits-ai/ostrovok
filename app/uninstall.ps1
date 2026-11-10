# Stops Ostrovok, removes it from start with Windows and deletes the installed program.
# Settings and the log in %LOCALAPPDATA%\Ostrovok are left in place.
Get-Process Ostrovok -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Ostrovok' -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\Ostrovok') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Ostrovok removed.'
