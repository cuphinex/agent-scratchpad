# Compila o Agent Scratchpad, instala-o em %LOCALAPPDATA%\AgentScratchpad e arranca-o com o Windows.
$ErrorActionPreference = 'Stop'

$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc  = Join-Path $fw 'csc.exe'
$dest = Join-Path $env:LOCALAPPDATA 'AgentScratchpad'
$exe  = Join-Path $dest 'AgentScratchpad.exe'
$bin  = Join-Path $PSScriptRoot 'bin'
$startup = [Environment]::GetFolderPath('Startup')

New-Item -ItemType Directory -Force $bin, $dest | Out-Null
$sources = Get-ChildItem $PSScriptRoot -Filter *.cs | ForEach-Object { $_.FullName }
$refs = 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll',
        'System.Windows.Forms.dll', 'System.Drawing.dll' | ForEach-Object { '/r:' + (Join-Path $fw $_) }
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu "/win32manifest:$PSScriptRoot\app.manifest" `
    "/out:$bin\AgentScratchpad.exe" $refs $sources
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

# Pede as instancias antigas que saiam antes de substituir o exe (incluindo o antigo "Scratchpad").
$oldDest = Join-Path $env:LOCALAPPDATA 'Scratchpad'
foreach ($old in (Join-Path $oldDest 'Scratchpad.exe'), $exe) {
    if (Test-Path $old) { Start-Process $old -ArgumentList '--quit' -Wait }
}
Get-Process Scratchpad, AgentScratchpad -ErrorAction SilentlyContinue | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
Get-Process Scratchpad, AgentScratchpad -ErrorAction SilentlyContinue | Stop-Process -Force

Copy-Item "$bin\AgentScratchpad.exe" $exe -Force
Copy-Item (Join-Path $PSScriptRoot 'AgentScratchpad.exe.config') (Join-Path $dest 'AgentScratchpad.exe.config') -Force
$cfg = Join-Path $dest 'config.ini'
if (-not (Test-Path $cfg)) {
    # Migra a configuracao do antigo "Scratchpad", se existir; senao usa a de omissao.
    $oldCfg = Join-Path $oldDest 'config.ini'
    if (Test-Path $oldCfg) { Copy-Item $oldCfg $cfg } else { Copy-Item (Join-Path $PSScriptRoot 'config.ini') $cfg }
}
# Limpa a instalacao antiga (atalho e pasta).
Remove-Item (Join-Path $startup 'Scratchpad.lnk') -ErrorAction SilentlyContinue
if (Test-Path $oldDest) { Remove-Item $oldDest -Recurse -Force -ErrorAction SilentlyContinue }

$lnk = Join-Path $startup 'Agent Scratchpad.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $dest
$shortcut.Save()

# Arranca pelo Explorer, com o ambiente normal do utilizador (o mesmo do arranque do Windows),
# e nao com o de quem correu este script (ex.: variaveis de outra sessao do Claude Code).
Start-Process explorer.exe -ArgumentList "`"$exe`""
Write-Host "Agent Scratchpad installed in $dest (autostart: $lnk)"
