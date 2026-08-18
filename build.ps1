$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$uia = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$uiaClient = Join-Path $uia 'UIAutomationClient.dll'
$uiaTypes = Join-Path $uia 'UIAutomationTypes.dll'
foreach ($ref in @($uiaClient, $uiaTypes)) {
    if (-not (Test-Path $ref)) { throw "UI Automation reference assembly not found: $ref" }
}

$ico = Join-Path $root 'SoundFocus.ico'
if (-not (Test-Path $ico)) { & (Join-Path $root 'make-icon.ps1') }
$out = Join-Path $root 'SoundFocus.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:"$out" `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:"$uiaClient" /reference:"$uiaTypes" `
    /win32icon:"$ico" /resource:"$ico",SoundFocus.ico `
    (Join-Path $root 'SoundFocus.cs')
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
Write-Output "built: $out"
