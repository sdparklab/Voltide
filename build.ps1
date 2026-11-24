$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) {
    throw "Cannot find csc.exe at $csc"
}

$files = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*.cs' |
         ForEach-Object { $_.FullName }

if ($files.Count -eq 0) { throw "No .cs files found" }

$opts = @(
    '/codepage:65001',
    '/optimize+',
    '/nologo',
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll'
)

# The Xbox pad battery over Bluetooth LE asks the WinRT GATT APIs. csc accepts the
# per-namespace metadata Windows keeps in System32\WinMetadata, so the merged
# Windows.winmd that only the Windows SDK ships is not required. If either half is
# missing the provider compiles out and a BLE pad reports an unknown percentage -
# never a fabricated one.
$winmd = Join-Path $env:SystemRoot 'System32\WinMetadata'
$facadeRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework'
$facade = $null
foreach ($ver in @('v4.8', 'v4.7.2', 'v4.6.1', 'v4.5.2', 'v4.5.1', 'v4.5')) {
    $candidate = Join-Path $facadeRoot (Join-Path $ver 'Facades\System.Runtime.dll')
    if (Test-Path $candidate) { $facade = $candidate; break }
}

$bleOpts = @()
if ($facade) {
    $winmds = @('Windows.Foundation.winmd', 'Windows.Devices.winmd', 'Windows.Storage.winmd')
    $missing = @($winmds | Where-Object { -not (Test-Path (Join-Path $winmd $_)) })
    if ($missing.Count -eq 0) {
        $bleOpts += '/r:' + $facade
        foreach ($w in $winmds) { $bleOpts += '/r:' + (Join-Path $winmd $w) }
        $bleOpts += '/define:HAVE_BLE_GATT'
    }
}
if ($bleOpts.Count -eq 0) {
    Write-Host 'WinRT metadata not found: building without the BLE battery path.'
}

$gui  = Join-Path $root 'Voltide.exe'
$test = Join-Path $root 'Voltide-Test.exe'
$ico  = Join-Path $root 'app.ico'

# The icon is generated from the logo geometry by tools\makeicon.ps1; if it is
# missing the build still succeeds, just without a shell icon.
$iconOpt = @()
if (Test-Path $ico) { $iconOpt = @("/win32icon:$ico") }

# Typefaces travel inside the exe, so a single file is enough to distribute.
# Files are staged under ASCII names first: csc's /resource switch mangles
# non-ASCII paths, and one of these is 汉仪旗黑.
$resOpt = @()
$fontDir = Join-Path $root 'Fonts'
if (Test-Path $fontDir) {
    $staging = Join-Path $root 'obj'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    $faces = @(
        @{ Source = 'Montserrat-Medium.ttf';   Embed = 'Montserrat-Medium.ttf' },
        @{ Source = 'Montserrat-SemiBold.ttf'; Embed = 'Montserrat-SemiBold.ttf' }
    )

    # The CJK face has a Chinese filename. Spelling it here would break: this script
    # is UTF-8 without BOM and PowerShell 5.1 reads it as ANSI, turning the name into
    # mojibake so Test-Path silently misses the file. Find it by elimination instead.
    $cjk = Get-ChildItem $fontDir -Filter '*.ttf' |
           Where-Object { $_.Name -notlike 'Montserrat*' } |
           Sort-Object Length -Descending |
           Select-Object -First 1

    if ($cjk) { $faces += @{ Source = $cjk.Name; Embed = 'AlibabaPuHuiTi-3-75-SemiBold.ttf' } }

    foreach ($face in $faces) {
        $src = Join-Path $fontDir $face.Source
        if (-not (Test-Path $src)) { continue }

        $staged = Join-Path $staging $face.Embed
        Copy-Item $src $staged -Force
        $resOpt += "/resource:$staged,$($face.Embed)"
    }
}

Write-Host "Compiling GUI ($($files.Count) files)..."

& $csc /target:winexe /out:$gui @opts @bleOpts @iconOpt @resOpt $files
if ($LASTEXITCODE -ne 0) { throw "GUI compile failed" }

Write-Host "Compiling console test build..."

& $csc /target:exe /out:$test @opts @bleOpts @iconOpt @resOpt $files
if ($LASTEXITCODE -ne 0) { throw "Test compile failed" }

Write-Host ""
Write-Host ("GUI  -> {0}  ({1:N0} bytes)" -f $gui,  (Get-Item $gui).Length)
Write-Host ("Test -> {0}  ({1:N0} bytes)" -f $test, (Get-Item $test).Length)
