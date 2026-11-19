param([switch]$Hardware)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $PSScriptRoot 'KeychronM6Tests.exe'
$sources = @('src/Core/Models.cs', 'src/Interop/HidNative.cs', 'src/Providers/IDeviceProvider.cs', 'src/Providers/KeychronM6Provider.cs', 'tests/KeychronM6Tests.cs') | ForEach-Object { Join-Path $root $_ }
& $csc /nologo /codepage:65001 /target:exe /out:$testExe $sources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
if ($Hardware) { & $testExe --hardware } else { & $testExe }
if ($LASTEXITCODE -ne 0) { throw 'Keychron tests failed' }
