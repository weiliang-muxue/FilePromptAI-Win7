$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Join-Path $projectRoot 'packages'
$libraryRoot = Join-Path $projectRoot 'lib'

if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw 'The local packages directory is missing. This script never downloads dependencies; restore the checked package set on a connected build computer first.'
}

$frameworkFolders = @(
    'PdfPig.0.1.15\lib\net471',
    'Microsoft.Bcl.HashCode.6.0.0\lib\net462',
    'System.Buffers.4.6.0\lib\net462',
    'System.Memory.4.6.0\lib\net462',
    'System.Numerics.Vectors.4.6.0\lib\net462',
    'System.Runtime.CompilerServices.Unsafe.6.1.0\lib\net462',
    'NPOI.2.7.4\lib\net472',
    'BouncyCastle.Cryptography.2.4.0\lib\net461',
    'Enums.NET.5.0.0\lib\net461',
    'ExtendedNumerics.BigDecimal.2025.1001.2.129\lib\net48',
    'MathNet.Numerics.Signed.5.0.0\lib\net48',
    'Microsoft.IO.RecyclableMemoryStream.3.0.1\lib\netstandard2.0',
    'SharpZipLib.1.4.2\lib\netstandard2.0',
    'SixLabors.Fonts.1.0.1\lib\netstandard2.0',
    'SixLabors.ImageSharp.2.1.10\lib\net472',
    'System.Security.Cryptography.Xml.8.0.2\lib\net462',
    'System.Text.Encoding.CodePages.5.0.0\lib\net461',
    'System.Threading.Tasks.Extensions.4.5.2\lib\netstandard2.0',
    'ZString.2.6.0\lib\netstandard2.0',
    'PDFsharp-MigraDoc-gdi.1.50.5147\lib\net20'
)

$expectedLibraryFiles = @(
    'BouncyCastle.Cryptography.dll',
    'Enums.NET.dll',
    'ExtendedNumerics.BigDecimal.dll',
    'ICSharpCode.SharpZipLib.dll',
    'MathNet.Numerics.dll',
    'Microsoft.Bcl.HashCode.dll',
    'Microsoft.IO.RecyclableMemoryStream.dll',
    'NPOI.Core.dll',
    'NPOI.OOXML.dll',
    'NPOI.OpenXml4Net.dll',
    'NPOI.OpenXmlFormats.dll',
    'SixLabors.Fonts.dll',
    'SixLabors.ImageSharp.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Security.Cryptography.Xml.dll',
    'System.Text.Encoding.CodePages.dll',
    'System.Threading.Tasks.Extensions.dll',
    'UglyToad.PdfPig.Core.dll',
    'UglyToad.PdfPig.dll',
    'UglyToad.PdfPig.DocumentLayoutAnalysis.dll',
    'UglyToad.PdfPig.Fonts.dll',
    'UglyToad.PdfPig.Package.dll',
    'UglyToad.PdfPig.Tokenization.dll',
    'UglyToad.PdfPig.Tokens.dll',
    'ZString.dll',
    'MigraDoc.DocumentObjectModel-gdi.dll',
    'MigraDoc.Rendering-gdi.dll',
    'MigraDoc.RtfRendering-gdi.dll',
    'PdfSharp-gdi.dll',
    'PdfSharp.Charting-gdi.dll'
)

$copyPlan = @()
foreach ($relativeFolder in $frameworkFolders) {
    $sourceFolder = Join-Path $packageRoot $relativeFolder
    if (-not (Test-Path -LiteralPath $sourceFolder -PathType Container)) {
        throw "Missing local package asset directory: $sourceFolder"
    }

    $copyPlan += @(
        Get-ChildItem -LiteralPath $sourceFolder -Filter '*.dll' -File
    )
}

$duplicateNames = @(
    $copyPlan |
        Group-Object Name |
        Where-Object { $_.Count -ne 1 }
)
if ($duplicateNames.Count -gt 0) {
    throw "Duplicate library file names were selected: $($duplicateNames.Name -join ', ')"
}

$actualLibraryFiles = @($copyPlan | ForEach-Object { $_.Name } | Sort-Object)
$libraryDifferences = @(
    Compare-Object `
        -ReferenceObject @($expectedLibraryFiles | Sort-Object) `
        -DifferenceObject $actualLibraryFiles
)
if ($libraryDifferences.Count -gt 0) {
    $details = $libraryDifferences |
        ForEach-Object { "$($_.InputObject) [$($_.SideIndicator)]" }
    throw "The local package DLL set does not match the approved offline dependency set: $($details -join ', ')"
}

if (-not (Test-Path -LiteralPath $libraryRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $libraryRoot | Out-Null
}

# Remove only generated DLLs. Other project files in lib, if any, are preserved.
Get-ChildItem -LiteralPath $libraryRoot -Filter '*.dll' -File |
    Remove-Item -Force

foreach ($sourceFile in $copyPlan) {
    Copy-Item -LiteralPath $sourceFile.FullName -Destination $libraryRoot -Force
}

$preparedLibraryFiles = @(
    Get-ChildItem -LiteralPath $libraryRoot -Filter '*.dll' -File |
        ForEach-Object { $_.Name } |
        Sort-Object
)
$preparedDifferences = @(
    Compare-Object `
        -ReferenceObject @($expectedLibraryFiles | Sort-Object) `
        -DifferenceObject $preparedLibraryFiles
)
if ($preparedDifferences.Count -gt 0) {
    throw 'The prepared library directory failed its final file-list verification.'
}

Write-Host "Prepared and verified $($preparedLibraryFiles.Count) local libraries in: $libraryRoot"
