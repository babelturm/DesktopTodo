$ErrorActionPreference = 'Stop'
$sourceDir = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$outputPath = Join-Path (Split-Path $sourceDir -Parent) 'DesktopTodo.exe'
$iconPath = Join-Path $sourceDir 'DesktopTodo.ico'
$iconBuilder = Join-Path ([IO.Path]::GetTempPath()) ('DesktopTodo-IconBuilder-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & $compilerPath /nologo /target:exe /codepage:65001 "/out:$iconBuilder" /reference:System.Drawing.dll "$sourceDir\NotificationIcon.cs" "$sourceDir\BuildIcon.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Icon builder failed.' }
    & $iconBuilder $iconPath (Split-Path $sourceDir -Parent)
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
} finally { if (Test-Path -LiteralPath $iconBuilder) { Remove-Item -LiteralPath $iconBuilder -Force } }
$metadataDir = Join-Path $env:WINDIR 'System32\WinMetadata'
$runtimeFacade = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll'
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$outputPath" "/win32manifest:$sourceDir\app.manifest" "/win32icon:$iconPath" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Runtime.WindowsRuntime.dll /reference:System.Runtime.InteropServices.WindowsRuntime.dll "/reference:$runtimeFacade" "/reference:$metadataDir\Windows.UI.winmd" "/reference:$metadataDir\Windows.Data.winmd" "/reference:$metadataDir\Windows.Foundation.winmd" "$sourceDir\DesktopTodo.cs" "$sourceDir\Details.cs" "$sourceDir\TaskCanvas.cs" "$sourceDir\Notifications.cs" "$sourceDir\NotificationTests.cs" "$sourceDir\ModernMenu.cs" "$sourceDir\ModernDialogs.cs" "$sourceDir\Lists.cs" "$sourceDir\ListTests.cs" "$sourceDir\CompletionTests.cs" "$sourceDir\NotificationIcon.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $outputPath"
