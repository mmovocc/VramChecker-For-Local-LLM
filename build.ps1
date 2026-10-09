param([switch]$Run, [string]$OutputDirectory = 'bin')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ Build Tools with a Windows SDK.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ toolchain was not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
# Generate a fixed build script. No filesystem deletion or moving occurs through cmd.
$nativeBuild = @"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
fxc /nologo /T vs_5_0 /E VSMain /Fo "$output\LiquidGlass.vs.cso" "$root\native\LiquidGlass.hlsl"
if errorlevel 1 exit /b 1
fxc /nologo /T ps_5_0 /E PSMain /Fo "$output\LiquidGlass.ps.cso" "$root\native\LiquidGlass.hlsl"
if errorlevel 1 exit /b 1
fxc /nologo /T ps_5_0 /E PSGeometry /Fo "$output\LiquidGlass.geometry.cso" "$root\native\LiquidGlass.hlsl"
if errorlevel 1 exit /b 1
fxc /nologo /T ps_5_0 /E PSReference /Fo "$output\LiquidGlass.reference.cso" "$root\native\LiquidGlass.hlsl"
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /O2 /MT /W4 /LD /DUNICODE /D_UNICODE "$root\native\LiquidGlass.cpp" /Fo"$output\LiquidGlass.obj" /link /OUT:"$output\LiquidGlass.Native.dll" /IMPLIB:"$output\LiquidGlass.Native.lib" d3d11.lib dxgi.lib dcomp.lib d3dcompiler.lib dwmapi.lib user32.lib gdi32.lib
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /O2 /MT "$root\tests\SamplingClock.cpp" /Fo"$output\SamplingClockTest.obj" /Fe"$output\SamplingClockTest.exe"
if errorlevel 1 exit /b 1
"$output\SamplingClockTest.exe"
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /O2 /MT "$root\tests\BackdropCache.cpp" /Fo"$output\BackdropCacheTest.obj" /Fe"$output\BackdropCacheTest.exe" /link user32.lib
if errorlevel 1 exit /b 1
"$output\BackdropCacheTest.exe"
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /O2 /MT "$root\tests\MotionPrediction.cpp" /Fo"$output\MotionPredictionTest.obj" /Fe"$output\MotionPredictionTest.exe"
if errorlevel 1 exit /b 1
"$output\MotionPredictionTest.exe"
exit /b %errorlevel%
"@
$batch = Join-Path $output 'build-native.cmd'
Set-Content -LiteralPath $batch -Value $nativeBuild -Encoding ascii
& $env:ComSpec /d /c $batch
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:library /platform:x64 /optimize+ /codepage:65001 "/out:$output\LiquidGlass.WinForms.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "$root\framework\GlassOptions.cs" "$root\framework\GlassRenderer.cs" "$root\framework\GlassForm.cs"
if ($LASTEXITCODE -ne 0) { throw 'Framework build failed.' }
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$output\GlassDemo.exe" "/r:$output\LiquidGlass.WinForms.dll" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "$root\samples\GlassDemo.cs"
if ($LASTEXITCODE -ne 0) { throw 'Demo build failed.' }
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$output\GpuVramWidget.exe" "/r:$output\LiquidGlass.WinForms.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Management.dll "$root\GpuVramWidget.cs" "$root\GlassRenderer.cs" "$root\launcher\*.cs" "$root\ui\*.cs"
if ($LASTEXITCODE -ne 0) { throw 'WinForms build failed.' }
Copy-Item -LiteralPath (Join-Path $root 'native\LiquidGlass.hlsl') -Destination $output -Force
Write-Host "Built: $output\GpuVramWidget.exe"
if ($Run) { Start-Process -FilePath (Join-Path $output 'GpuVramWidget.exe') -WorkingDirectory $output -WindowStyle Normal }
