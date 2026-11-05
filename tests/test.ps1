$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$obj = Join-Path $root 'obj'
$out = Join-Path $obj 'Voltide-Omni-Tests.exe'

New-Item -ItemType Directory -Path $obj -Force | Out-Null
$sources = @(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*.cs' |
    ForEach-Object { $_.FullName })
$sources += Join-Path $PSScriptRoot 'AsusRogProviderTests.cs'
& $csc /nologo /codepage:65001 /target:exe "/out:$out" /main:Voltide.Tests.AsusRogProviderTests `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Test compile failed' }

& $out
if ($LASTEXITCODE -ne 0) { throw 'ROG Omni tests failed' }
