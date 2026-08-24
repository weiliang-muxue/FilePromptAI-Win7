$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$testExecutable = Join-Path $artifactRoot 'ConversationStoreSmokeTest.exe'
$storagePath = Join-Path $artifactRoot 'conversation-store-smoke.xml'

if (-not (Test-Path -LiteralPath $artifactRoot)) {
    New-Item -ItemType Directory -Path $artifactRoot | Out-Null
}

$compilerArguments = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:5',
    '/codepage:65001',
    '/warn:4',
    "/out:$testExecutable",
    "/reference:$(Join-Path $frameworkRoot 'System.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Core.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Xml.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Xml.Linq.dll')",
    (Join-Path $projectRoot 'src\AppDataPath.cs'),
    (Join-Path $projectRoot 'src\ConversationModels.cs'),
    (Join-Path $projectRoot 'src\ConversationStore.cs'),
    (Join-Path $testRoot 'ConversationStoreSmokeTest.cs')
)

& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Conversation test compilation failed with exit code $LASTEXITCODE."
}

& $testExecutable $storagePath
if ($LASTEXITCODE -ne 0) {
    throw "Conversation test failed with exit code $LASTEXITCODE."
}
