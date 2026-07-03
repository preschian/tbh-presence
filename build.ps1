# Builds TbhPresence.exe from src\*.cs using the C# compiler that ships with
# Windows (.NET Framework 4.x) - no SDK install needed.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$out = Join-Path $here 'TbhPresence.exe'
# /target:winexe -> no console window in tray mode (console modes attach on demand)
& $csc /nologo /optimize+ /target:winexe /platform:anycpu `
    /out:$out `
    "/win32icon:$(Join-Path $here 'assets\app.ico')" `
    /r:System.Web.Extensions.dll `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    (Join-Path $here 'src\*.cs')
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "Built $out" -ForegroundColor Green
