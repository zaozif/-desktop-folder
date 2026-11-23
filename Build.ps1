param(
    [string]$Output = (Join-Path $PSScriptRoot 'Desktop Folders.exe'),
    [string]$NativeCompiler
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
if (!$NativeCompiler) {
    $NativeCompiler = (Get-Command clang++.exe -ErrorAction SilentlyContinue).Source
}
if (!$NativeCompiler) {
    $NativeCompiler = Get-Process windhawk -ErrorAction SilentlyContinue | Where-Object Path |
        ForEach-Object { Join-Path (Split-Path $_.Path -Parent) 'Compiler\bin\clang++.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$NativeCompiler) {
    throw 'Specify -NativeCompiler with a clang++.exe that has MinGW-w64 and C++/WinRT headers.'
}
$nativeBuild = Join-Path $PSScriptRoot 'obj'
[IO.Directory]::CreateDirectory($nativeBuild) | Out-Null
$nativeDll = Join-Path $nativeBuild 'TaskbarTuning-x64.dll'
$nativeSources = @('TaskbarBridge.cpp', 'TaskbarAppearance.cpp') | ForEach-Object { Join-Path (Join-Path $PSScriptRoot 'native') $_ }
$nativeFlags = @('-std=c++23','--target=x86_64-w64-mingw32','-DUNICODE','-D_UNICODE','-D_WIN32_WINNT=0x0A00',
    '-O2','-shared','-static','-s','-Wno-pragma-pack','-Wno-dll-attribute-on-redeclaration',
    "-ffile-prefix-map=$PSScriptRoot=.",'-Wl,--no-insert-timestamp')
& $NativeCompiler @nativeFlags @nativeSources -luser32 -lwindowsapp -lole32 -loleaut32 -luuid -o $nativeDll
if ($LASTEXITCODE -ne 0) { throw "Native build failed ($LASTEXITCODE)." }

$references = @('WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll',
    'WPF\UIAutomationTypes.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll','Microsoft.CSharp.dll')
$theme = Join-Path $PSScriptRoot 'ViewTheme.xaml'
$arguments = @('/nologo', '/target:winexe', '/optimize+', '/utf8output', '/codepage:65001',
    '/main:DesktopFolders.Program', "/out:$Output", ("/win32manifest:" + (Join-Path $PSScriptRoot 'app.manifest')),
    "/resource:$nativeDll,DesktopFolders.TaskbarTuning.x64.dll", "/resource:$theme,DesktopFolders.ViewTheme.xaml")
$icon = Join-Path $PSScriptRoot 'Folder.ico'
if (Test-Path -LiteralPath $icon) { $arguments += "/win32icon:$icon" }
$arguments += $references | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$arguments += Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName
& (Join-Path $framework 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
