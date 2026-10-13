param([string]$Variant = 'candidate', [string]$Trace = '', [string]$Probe = '')
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$probeRoot = Split-Path -Parent $PSScriptRoot
if (-not $Probe) { $Probe = "$PSScriptRoot\bin\Release\net48\AudioProbe.exe" }
$probeArguments = @("$probeRoot\artifacts\$Variant\SoundFocus.exe", "$probeRoot\artifacts\$Variant-probe.txt")
if ($Trace) { $probeArguments += $Trace }
& C:\Users\mjb\tools\agent-scripts\Invoke-OnHiddenDesktop.ps1 -FilePath $Probe -ArgumentList $probeArguments -WorkingDirectory $probeRoot -TimeoutSeconds 240
