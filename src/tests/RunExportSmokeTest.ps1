$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$testRoot = Join-Path $projectRoot 'tests'
$outputRoot = Join-Path $testRoot 'build-artifacts'
$libraryRoot = Join-Path $projectRoot 'lib'
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$output = Join-Path $outputRoot 'ExportSmokeTest.exe'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw '.NET Framework C# compiler was not found.'
}

$requiredLibraries = @(
    'MigraDoc.DocumentObjectModel-gdi.dll',
    'MigraDoc.Rendering-gdi.dll',
    'PdfSharp-gdi.dll',
    'PdfSharp.Charting-gdi.dll',
    'NPOI.Core.dll',
    'NPOI.OOXML.dll',
    'NPOI.OpenXml4Net.dll',
    'NPOI.OpenXmlFormats.dll'
)
foreach ($library in $requiredLibraries) {
    $libraryPath = Join-Path $libraryRoot $library
    if (-not (Test-Path -LiteralPath $libraryPath -PathType Leaf)) {
        throw "Required export test library is missing: $libraryPath. Run prepare-libs.ps1 first."
    }
}

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
    "/reference:$(Join-Path $frameworkRoot 'System.IO.Compression.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.IO.Compression.FileSystem.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Drawing.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Windows.Forms.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Xml.dll')",
    "/reference:$(Join-Path $libraryRoot 'MigraDoc.DocumentObjectModel-gdi.dll')",
    "/reference:$(Join-Path $libraryRoot 'MigraDoc.Rendering-gdi.dll')",
    "/reference:$(Join-Path $libraryRoot 'PdfSharp-gdi.dll')",
    "/reference:$(Join-Path $libraryRoot 'PdfSharp.Charting-gdi.dll')",
    "/reference:$(Join-Path $libraryRoot 'NPOI.Core.dll')",
    "/reference:$(Join-Path $libraryRoot 'NPOI.OOXML.dll')",
    "/reference:$(Join-Path $libraryRoot 'NPOI.OpenXml4Net.dll')",
    "/reference:$(Join-Path $libraryRoot 'NPOI.OpenXmlFormats.dll')",
    (Join-Path $projectRoot 'src\AtomicFile.cs'),
    (Join-Path $projectRoot 'src\MarkdownDocument.cs'),
    (Join-Path $projectRoot 'src\CsvExporter.cs'),
    (Join-Path $projectRoot 'src\DocxExporter.cs'),
    (Join-Path $projectRoot 'src\PdfExporter.cs'),
    (Join-Path $projectRoot 'src\XlsxExporter.cs'),
    (Join-Path $testRoot 'ExportSmokeTest.cs')
)

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Export smoke test compilation failed with exit code $LASTEXITCODE."
}

# Run from the artifact directory with the same offline DLL set as the app.
Get-ChildItem -LiteralPath $libraryRoot -Filter '*.dll' -File |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $outputRoot -Force
    }
Copy-Item -LiteralPath (Join-Path $projectRoot 'FilePromptAI.exe.config') `
    -Destination ($output + '.config') -Force

& $output
if ($LASTEXITCODE -ne 0) {
    throw "Export smoke test failed with exit code $LASTEXITCODE."
}
