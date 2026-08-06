param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot 'src'
$distributionRoot = Join-Path $projectRoot 'dist'
$libraryRoot = Join-Path $projectRoot 'lib'
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$outputExe = Join-Path $distributionRoot 'FilePromptAI.exe'
$iconPath = Join-Path $projectRoot 'assets\FilePromptAI.ico'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework C# compiler was not found.'
}

if ((Split-Path -Leaf $distributionRoot) -ne 'dist' -or
    (Split-Path -Parent $distributionRoot) -ne $projectRoot) {
    throw "Unsafe distribution path: $distributionRoot"
}
if (Test-Path -LiteralPath $distributionRoot) {
    Remove-Item -LiteralPath $distributionRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $distributionRoot | Out-Null

$sourceFiles = @(
    Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File |
        Sort-Object Name |
        ForEach-Object { $_.FullName }
)

if ($sourceFiles.Count -eq 0) {
    throw 'No C# source files were found.'
}

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll',
    'System.Net.Http.dll',
    'System.Security.dll',
    'System.Web.Extensions.dll',
    'System.Windows.Forms.dll',
    'System.Xml.dll',
    'System.Xml.Linq.dll'
)

$compilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:5',
    '/codepage:65001',
    '/warn:4',
    "/out:$outputExe",
    "/win32manifest:$(Join-Path $projectRoot 'app.manifest')"
)

if (Test-Path -LiteralPath $iconPath) {
    $compilerArguments += "/win32icon:$iconPath"
}

foreach ($reference in $references) {
    $compilerArguments += "/reference:$(Join-Path $frameworkRoot $reference)"
}

$compilerArguments += $sourceFiles
& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'FilePromptAI.exe.config') `
    -Destination (Join-Path $distributionRoot 'FilePromptAI.exe.config') `
    -Force

if (Test-Path -LiteralPath $libraryRoot) {
    Get-ChildItem -LiteralPath $libraryRoot -Filter '*.dll' -File -Recurse |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $distributionRoot -Force
        }
}

$noticePath = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt'
if (Test-Path -LiteralPath $noticePath) {
    Copy-Item -LiteralPath $noticePath -Destination $distributionRoot -Force
}

$readmePath = Join-Path $projectRoot 'README.md'
if (Test-Path -LiteralPath $readmePath) {
    Copy-Item -LiteralPath $readmePath -Destination $distributionRoot -Force
}

Write-Host "Built: $outputExe"
