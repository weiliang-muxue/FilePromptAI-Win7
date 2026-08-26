param(
    [string]$Version = '1.18',
    [switch]$StageOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$expectedRuntimeLength = 121346568
$expectedRuntimeSha256 = '0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'
$runtimeFileName = 'NDP48-x86-x64-AllOS-ENU.exe'

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch '^[0-9A-Za-z](?:[0-9A-Za-z._-]{0,30}[0-9A-Za-z])?$') {
    throw 'Version may contain only letters, digits, dots, underscores, and hyphens.'
}

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appBuildScript = Join-Path $projectRoot 'build.ps1'
$appDistribution = Join-Path $projectRoot 'dist'
$libraryRoot = Join-Path $projectRoot 'lib'
$libraryChecksumPath = Join-Path $projectRoot 'LIBRARIES-SHA256.txt'
$bootstrapperRoot = Join-Path $projectRoot 'bootstrapper'
$uninstallerRoot = Join-Path $projectRoot 'uninstaller'
$acceptanceRoot = Join-Path $projectRoot 'acceptance'
$appIcon = Join-Path $projectRoot 'assets\FilePromptAI.ico'
$redistRoot = Join-Path $projectRoot 'redist'
$releaseFolderName = "FilePromptAI-offline-release-v$Version"
$releaseRoot = Join-Path $projectRoot $releaseFolderName
$releaseAppRoot = Join-Path $releaseRoot 'app'
$releaseRuntimeRoot = Join-Path $releaseRoot 'runtime'
$compilerRoot = 'C:\Windows\Microsoft.NET\Framework\v3.5'
$referenceRoot = 'C:\Windows\Microsoft.NET\Framework\v2.0.50727'
$compiler = Join-Path $compilerRoot 'csc.exe'
$bootstrapperExe = Join-Path $releaseRoot 'Start-FilePromptAI.exe'
$uninstallerExe = Join-Path $releaseRoot 'Uninstall-FilePromptAI.exe'
$acceptanceExe = Join-Path $releaseRoot 'Verify-FilePromptAI.exe'
$releaseAcceptanceFixtureRoot = Join-Path $releaseRoot 'acceptance\fixtures'
$acceptanceTrustedResourceName = 'FilePromptAI.Acceptance.TrustedPayload.sha256'
$acceptanceBuildRoot = Join-Path $projectRoot 'tests\build-artifacts\acceptance'
$runtimeSource = Join-Path $redistRoot $runtimeFileName
$archivePath = Join-Path $projectRoot ("FilePromptAI-Win7-Full-v$Version.zip")

function Assert-FileExists {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing $Description`: $Path"
    }
}

function Assert-OfflineRuntime {
    param([string]$Path)

    Assert-FileExists -Path $Path -Description '.NET Framework 4.8 offline installer'

    $runtime = Get-Item -LiteralPath $Path
    if ($runtime.Length -ne $expectedRuntimeLength) {
        throw "The .NET Framework installer has an unexpected size. Expected $expectedRuntimeLength bytes, got $($runtime.Length). A small web installer or a truncated file cannot be packaged."
    }

    $runtimeHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not [string]::Equals(
        $runtimeHash,
        $expectedRuntimeSha256,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "The .NET Framework installer SHA-256 does not match the approved offline installer. Actual: $runtimeHash"
    }

    if ($runtime.VersionInfo.ProductName -ne 'Microsoft .NET Framework 4.8' -or
        $runtime.VersionInfo.OriginalFilename -ne $runtimeFileName) {
        throw 'The runtime file metadata does not identify the Microsoft .NET Framework 4.8 offline installer.'
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate -eq $null -or
        $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "The .NET Framework installer does not have a valid Microsoft Authenticode signature. Status: $($signature.Status)"
    }
}

function Copy-RequiredFile {
    param(
        [string]$Source,
        [string]$Destination,
        [string]$Description
    )

    Assert-FileExists -Path $Source -Description $Description
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

Assert-FileExists -Path $appBuildScript -Description 'application build script'
Assert-FileExists -Path $appIcon -Description 'application icon'
Assert-FileExists -Path $libraryChecksumPath -Description 'approved library checksum manifest'
Assert-FileExists -Path (Join-Path $uninstallerRoot 'Program.cs') -Description 'uninstaller source'
Assert-FileExists -Path (Join-Path $uninstallerRoot 'AssemblyInfo.cs') -Description 'uninstaller assembly metadata'
Assert-FileExists -Path (Join-Path $uninstallerRoot 'uninstaller.manifest') -Description 'uninstaller manifest'
Assert-FileExists -Path (Join-Path $uninstallerRoot 'Uninstall-FilePromptAI.exe.config') -Description 'uninstaller configuration'
Assert-FileExists -Path (Join-Path $acceptanceRoot 'Program.cs') -Description 'acceptance verifier source'
Assert-FileExists -Path (Join-Path $acceptanceRoot 'PackagedUiJourney.cs') -Description 'packaged UI acceptance journey source'
Assert-FileExists -Path (Join-Path $acceptanceRoot 'AssemblyInfo.cs') -Description 'acceptance verifier metadata'
Assert-FileExists -Path (Join-Path $acceptanceRoot 'acceptance.manifest') -Description 'acceptance verifier manifest'
Assert-FileExists -Path (Join-Path $acceptanceRoot 'Verify-FilePromptAI.exe.config') -Description 'acceptance verifier configuration'
Assert-FileExists -Path (Join-Path $acceptanceRoot 'fixtures\acceptance.txt') -Description 'acceptance text fixture'
Assert-FileExists -Path $compiler -Description '.NET Framework 3.5 compiler'
Assert-FileExists -Path (Join-Path $referenceRoot 'System.dll') -Description '.NET 2.0 System.dll reference'
Assert-FileExists -Path (Join-Path $referenceRoot 'System.Drawing.dll') -Description '.NET 2.0 System.Drawing.dll reference'
Assert-FileExists -Path (Join-Path $referenceRoot 'System.Windows.Forms.dll') -Description '.NET 2.0 Windows Forms reference'
Assert-OfflineRuntime -Path $runtimeSource

# build.ps1 prepares and verifies the approved local DLL set before compiling.
& powershell -NoProfile -ExecutionPolicy Bypass -File $appBuildScript
if ($LASTEXITCODE -ne 0) {
    throw 'The FilePrompt AI application build failed.'
}

$libraryFiles = @(
    Get-ChildItem -LiteralPath $libraryRoot -Filter '*.dll' -File |
        Sort-Object Name
)
if ($libraryFiles.Count -eq 0) {
    throw 'No application libraries were prepared.'
}

# A clean staging directory prevents old exports, test files, or stale DLLs from leaking into a release.
if ((Split-Path -Leaf $releaseRoot) -ne $releaseFolderName -or
    (Split-Path -Parent $releaseRoot) -ne $projectRoot) {
    throw "Unsafe staging path: $releaseRoot"
}
if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $releaseAppRoot -Force | Out-Null
New-Item -ItemType Directory -Path $releaseRuntimeRoot -Force | Out-Null
New-Item -ItemType Directory -Path $releaseAcceptanceFixtureRoot -Force | Out-Null

Copy-RequiredFile `
    -Source (Join-Path $appDistribution 'FilePromptAI.exe') `
    -Destination $releaseAppRoot `
    -Description 'FilePrompt AI executable'
Copy-RequiredFile `
    -Source (Join-Path $appDistribution 'FilePromptAI.exe.config') `
    -Destination $releaseAppRoot `
    -Description 'FilePrompt AI configuration'

foreach ($libraryFile in $libraryFiles) {
    $distributionLibrary = Join-Path $appDistribution $libraryFile.Name
    Assert-FileExists -Path $distributionLibrary -Description "application library $($libraryFile.Name)"

    $sourceHash = (Get-FileHash -LiteralPath $libraryFile.FullName -Algorithm SHA256).Hash
    $distributionHash = (Get-FileHash -LiteralPath $distributionLibrary -Algorithm SHA256).Hash
    if ($sourceHash -ne $distributionHash) {
        throw "The built copy of $($libraryFile.Name) does not match the prepared library."
    }

    Copy-Item -LiteralPath $distributionLibrary -Destination $releaseAppRoot -Force
}

Copy-RequiredFile `
    -Source (Join-Path $projectRoot 'README.md') `
    -Destination $releaseAppRoot `
    -Description 'application README'
Copy-RequiredFile `
    -Source (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') `
    -Destination $releaseAppRoot `
    -Description 'third-party notices'
Copy-RequiredFile `
    -Source $libraryChecksumPath `
    -Destination $releaseAppRoot `
    -Description 'approved library checksum manifest'

$compilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    '/warn:4',
    "/out:$bootstrapperExe",
    "/win32manifest:$(Join-Path $bootstrapperRoot 'bootstrapper.manifest')",
    "/win32icon:$appIcon",
    "/reference:$(Join-Path $referenceRoot 'System.dll')",
    "/reference:$(Join-Path $referenceRoot 'System.Windows.Forms.dll')",
    (Join-Path $bootstrapperRoot 'AssemblyInfo.cs'),
    (Join-Path $bootstrapperRoot 'Program.cs')
)

& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Bootstrapper compilation failed with exit code $LASTEXITCODE."
}

Copy-RequiredFile `
    -Source (Join-Path $bootstrapperRoot 'Start-FilePromptAI.exe.config') `
    -Destination $releaseRoot `
    -Description 'bootstrapper configuration'

$uninstallerCompilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    '/warn:4',
    "/out:$uninstallerExe",
    "/win32manifest:$(Join-Path $uninstallerRoot 'uninstaller.manifest')",
    "/win32icon:$appIcon",
    "/reference:$(Join-Path $referenceRoot 'System.dll')",
    "/reference:$(Join-Path $referenceRoot 'System.Drawing.dll')",
    "/reference:$(Join-Path $referenceRoot 'System.Windows.Forms.dll')",
    (Join-Path $uninstallerRoot 'AssemblyInfo.cs'),
    (Join-Path $uninstallerRoot 'Program.cs')
)

& $compiler $uninstallerCompilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Uninstaller compilation failed with exit code $LASTEXITCODE."
}

Copy-RequiredFile `
    -Source (Join-Path $uninstallerRoot 'Uninstall-FilePromptAI.exe.config') `
    -Destination $releaseRoot `
    -Description 'uninstaller configuration'

Copy-RequiredFile `
    -Source (Join-Path $acceptanceRoot 'Verify-FilePromptAI.exe.config') `
    -Destination $releaseRoot `
    -Description 'acceptance verifier configuration'
Copy-RequiredFile `
    -Source (Join-Path $acceptanceRoot 'fixtures\acceptance.txt') `
    -Destination $releaseAcceptanceFixtureRoot `
    -Description 'acceptance text fixture'
foreach ($fixtureName in @('sample.pdf', 'sample.docx', 'sample.png')) {
    Copy-RequiredFile `
        -Source (Join-Path $projectRoot "tests\fixtures\$fixtureName") `
        -Destination $releaseAcceptanceFixtureRoot `
        -Description "acceptance $fixtureName fixture"
}

Copy-Item -LiteralPath $runtimeSource -Destination $releaseRuntimeRoot -Force
Assert-OfflineRuntime -Path (Join-Path $releaseRuntimeRoot $runtimeFileName)
Copy-RequiredFile `
    -Source (Join-Path $projectRoot 'OFFLINE-README.txt') `
    -Destination $releaseRoot `
    -Description 'offline README'

# Embed the expected payload hashes before compiling the verifier. The
# verifier itself is intentionally absent, avoiding a self-referential hash;
# its identity is anchored by the ZIP checksum published outside the archive.
New-Item -ItemType Directory -Path $acceptanceBuildRoot -Force | Out-Null
$trustedPayloadManifestPath = Join-Path $acceptanceBuildRoot (
    'TrustedPayload-' + [Guid]::NewGuid().ToString('N') + '.txt'
)
$trustedPayloadLines = @(
    Get-ChildItem -LiteralPath $releaseRoot -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($releaseRoot.Length).TrimStart('\')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "$hash *$relativePath"
        }
)
[IO.File]::WriteAllLines(
    $trustedPayloadManifestPath,
    $trustedPayloadLines,
    (New-Object Text.UTF8Encoding($false)))

$acceptanceCompilerArguments = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    '/warn:4',
    "/out:$acceptanceExe",
    "/win32manifest:$(Join-Path $acceptanceRoot 'acceptance.manifest')",
    "/resource:$trustedPayloadManifestPath,$acceptanceTrustedResourceName",
    "/reference:$(Join-Path $referenceRoot 'System.dll')",
    "/reference:$(Join-Path $referenceRoot 'System.Drawing.dll')",
    "/reference:$(Join-Path $referenceRoot 'System.Windows.Forms.dll')",
    "/reference:$(Join-Path $referenceRoot 'System.Xml.dll')",
    (Join-Path $acceptanceRoot 'AssemblyInfo.cs'),
    (Join-Path $acceptanceRoot 'PackagedUiJourney.cs'),
    (Join-Path $acceptanceRoot 'Program.cs')
)

$acceptanceCompilerExitCode = -1
try {
    & $compiler $acceptanceCompilerArguments
    $acceptanceCompilerExitCode = $LASTEXITCODE
}
finally {
    if (Test-Path -LiteralPath $trustedPayloadManifestPath -PathType Leaf) {
        Remove-Item -LiteralPath $trustedPayloadManifestPath -Force
    }
}
if ($acceptanceCompilerExitCode -ne 0) {
    throw "Acceptance verifier compilation failed with exit code $acceptanceCompilerExitCode."
}
Assert-FileExists -Path $acceptanceExe -Description 'acceptance verifier executable'

# The checksum list lets an administrator verify every delivered payload file without Internet access.
$checksumLines = @(
    Get-ChildItem -LiteralPath $releaseRoot -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($releaseRoot.Length).TrimStart('\')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "$hash *$relativePath"
        }
)
$checksumPath = Join-Path $releaseRoot 'PACKAGE-CHECKSUMS-SHA256.txt'
[IO.File]::WriteAllLines(
    $checksumPath,
    $checksumLines,
    (New-Object Text.UTF8Encoding($false)))

if ($StageOnly) {
    Write-Host "Prepared and verified offline package staging directory: $releaseRoot"
    return
}

Compress-Archive `
    -Path (Join-Path $releaseRoot '*') `
    -DestinationPath $archivePath `
    -CompressionLevel Optimal `
    -Force

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$archiveHashPath = "$archivePath.sha256.txt"
[IO.File]::WriteAllText(
    $archiveHashPath,
    "$archiveHash *$(Split-Path -Leaf $archivePath)`r`n",
    (New-Object Text.UTF8Encoding($false)))

Write-Host "Built and verified offline package: $archivePath"
Write-Host "Archive SHA-256: $archiveHash"
Write-Host "Archive checksum file: $archiveHashPath"
