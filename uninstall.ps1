# Remove o Agent Scratchpad: fecha a app, apaga o atalho de arranque e a pasta de instalacao.
$dest = Join-Path $env:LOCALAPPDATA 'AgentScratchpad'
$exe  = Join-Path $dest 'AgentScratchpad.exe'
$startup = [Environment]::GetFolderPath('Startup')

if (Test-Path $exe) { Start-Process $exe -ArgumentList '--quit' -Wait }
Get-Process AgentScratchpad, Scratchpad -ErrorAction SilentlyContinue | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
Get-Process AgentScratchpad, Scratchpad -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-Item (Join-Path $startup 'Agent Scratchpad.lnk'), (Join-Path $startup 'Scratchpad.lnk') -ErrorAction SilentlyContinue
Remove-Item $dest, (Join-Path $env:LOCALAPPDATA 'Scratchpad') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Agent Scratchpad removed."
