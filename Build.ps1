param([string]$Output = (Join-Path $PSScriptRoot 'Desktop Folders.exe'))

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$references = @('WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll',
    'WPF\UIAutomationTypes.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll','Microsoft.CSharp.dll')
$arguments = @('/nologo', '/target:winexe', '/optimize+', '/utf8output', '/codepage:65001',
    '/main:DesktopFolders.Program', "/out:$Output", ("/win32manifest:" + (Join-Path $PSScriptRoot 'app.manifest')))
$icon = Join-Path $PSScriptRoot 'Folder.ico'
if (Test-Path -LiteralPath $icon) { $arguments += "/win32icon:$icon" }
$arguments += $references | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$arguments += Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName
& (Join-Path $framework 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
