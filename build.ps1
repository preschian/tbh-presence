# Builds TbhPresence.exe from src\*.cs using the C# compiler that ships with
# Windows (.NET Framework 4.x) - no SDK install needed.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$out = Join-Path $here 'TbhPresence.exe'
& $csc /nologo /optimize+ /target:exe /platform:anycpu `
    /out:$out `
    /r:System.Web.Extensions.dll `
    (Join-Path $here 'src\*.cs')
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "Built $out" -ForegroundColor Green
