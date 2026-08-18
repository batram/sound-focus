# Starts SoundFocus at login by putting a shortcut in the per-user Startup folder.
# No admin rights, nothing in the registry, and removable with -Remove.
#
#   powershell -NoProfile -File install-autostart.ps1
#   powershell -NoProfile -File install-autostart.ps1 -Arguments '--hotkey ctrl+alt+f9'
#   powershell -NoProfile -File install-autostart.ps1 -Remove
param(
    [string]$Arguments = '',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'bin\Release\net48\SoundFocus.exe'
$startup = [Environment]::GetFolderPath('Startup')
$link = Join-Path $startup 'SoundFocus.lnk'

if ($Remove) {
    if (Test-Path $link) { Remove-Item $link -Force; Write-Output "removed $link" }
    else { Write-Output "nothing to remove at $link" }
    return
}

if (-not (Test-Path $exe)) {
    Write-Output "SoundFocus.exe not built yet, running dotnet build"
    Push-Location $root
    try {
        dotnet build -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }
}
if (-not (Test-Path $exe)) { throw "expected the build to produce $exe" }

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($link)
$sc.TargetPath = $exe
$sc.Arguments = $Arguments
$sc.WorkingDirectory = $root          # so it finds its own files if that ever matters
$sc.IconLocation = "$exe,0"
$sc.Description = 'Jump to the app or tab currently making sound'
$sc.WindowStyle = 1
$sc.Save()

Write-Output "installed $link"
Write-Output "  target: $exe $Arguments"
Write-Output "starts automatically at next sign-in; remove with -Remove"
