# Builds bin\LosslessSwitcher.exe against the .NET Framework 4.8 that ships with Windows.
# Needs only a Roslyn csc.exe (Visual Studio or VS Build Tools) - no .NET SDK, no NuGet.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -property installationPath
    if ($vs) { $csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe' }
}
if (-not $csc -or -not (Test-Path $csc)) {
    $csc = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio", "$env:ProgramFiles\Microsoft Visual Studio" -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -like '*Roslyn*' | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $csc) { throw 'Roslyn csc.exe not found. Install "Visual Studio Build Tools" (C# compiler).' }

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$gac = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL"
function Gac($name) { (Get-ChildItem "$gac\$name" -Recurse -Filter "$name.dll" | Select-Object -First 1).FullName }

$winmd = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata" -Recurse -Filter Windows.winmd -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -ne 'Facade' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$winRefs = if ($winmd) { @($winmd) } else {
    # No Windows SDK: the per-namespace metadata that ships with Windows works too.
    'Foundation', 'Media' | ForEach-Object { "$env:WINDIR\System32\WinMetadata\Windows.$_.winmd" }
}

$refs = @(
    "$fw\mscorlib.dll", "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Drawing.dll",
    "$fw\System.Windows.Forms.dll", "$fw\System.Xml.dll", "$fw\System.Xml.Linq.dll",
    "$fw\System.Net.Http.dll", "$fw\System.Web.Extensions.dll",
    (Gac 'System.Runtime'), (Gac 'System.Runtime.WindowsRuntime'),
    (Gac 'System.Runtime.InteropServices.WindowsRuntime'), (Gac 'System.Threading.Tasks')
) + $winRefs

New-Item -ItemType Directory -Force "$root\bin" | Out-Null
$cscArgs = @('-nologo', '-noconfig', '-nostdlib', '-langversion:latest', '-optimize+', '-target:winexe', '-platform:anycpu',
          "-win32manifest:$root\src\app.manifest", "-out:$root\bin\LosslessSwitcher.exe") +
        ($refs | ForEach-Object { "-r:$_" }) + (Get-ChildItem "$root\src\*.cs" | ForEach-Object FullName)
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Get-Item "$root\bin\LosslessSwitcher.exe" | Select-Object FullName, @{n='KB'; e={[math]::Round($_.Length / 1KB, 1)}}
