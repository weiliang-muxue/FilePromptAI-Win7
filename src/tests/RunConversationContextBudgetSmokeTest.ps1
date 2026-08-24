$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$testExecutable = Join-Path `
    $artifactRoot `
    'ConversationContextBudgetSmokeTest.exe'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw '.NET Framework C# compiler was not found.'
}
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
    (Join-Path $projectRoot 'src\ConversationModels.cs'),
    (Join-Path $projectRoot 'src\ConversationContextBudget.cs'),
    (Join-Path $testRoot 'ConversationContextBudgetSmokeTest.cs')
)

& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Context budget test compilation failed with exit code $LASTEXITCODE."
}

& $testExecutable
if ($LASTEXITCODE -ne 0) {
    throw "Context budget test failed with exit code $LASTEXITCODE."
}
