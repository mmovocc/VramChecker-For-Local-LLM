$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\worker-test'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$batch = Join-Path $output 'build-mock.cmd'
@"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cl /nologo /EHsc /O2 /MT /LD "$root\tests\MockGlass.cpp" /Fo"$output\Mock.obj" /link /OUT:"$output\LiquidGlass.Native.dll" /IMPLIB:"$output\Mock.lib"
exit /b %errorlevel%
"@ | Set-Content -LiteralPath $batch -Encoding ascii
& $env:ComSpec /d /c $batch
if ($LASTEXITCODE -ne 0) { throw 'Mock build failed' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /main:RenderWorkerTest /platform:x64 /codepage:65001 "/out:$output\RenderWorkerTest.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Management.dll "$root\GpuVramWidget.cs" "$root\GlassRenderer.cs" "$root\launcher\*.cs" "$root\ui\*.cs" "$root\framework\GlassOptions.cs" "$root\framework\GlassRenderer.cs" "$root\tests\RenderWorker.cs"
if ($LASTEXITCODE -ne 0) { throw 'Worker test build failed' }
& (Join-Path $output 'RenderWorkerTest.exe')
if ($LASTEXITCODE -ne 0) { throw 'Worker test failed' }
