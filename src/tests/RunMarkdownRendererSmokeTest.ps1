$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$testRoot = Join-Path $projectRoot 'tests'
$outputRoot = Join-Path $testRoot 'build-artifacts'
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$output = Join-Path $outputRoot 'MarkdownRendererSmokeTest.exe'

if (-not (Test-Path -LiteralPath $outputRoot)) {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/langversion:5',
    '/codepage:65001',
    '/warn:4',
    "/out:$output",
    "/reference:$(Join-Path $frameworkRoot 'System.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Core.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Drawing.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Windows.Forms.dll')",
    (Join-Path $projectRoot 'src\MarkdownRichTextRenderer.cs'),
    (Join-Path $testRoot 'MarkdownRendererSmokeTest.cs')
)

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Markdown renderer smoke test compilation failed with exit code $LASTEXITCODE."
}

& $output
if ($LASTEXITCODE -ne 0) {
    throw "Markdown renderer smoke test failed with exit code $LASTEXITCODE."
}
