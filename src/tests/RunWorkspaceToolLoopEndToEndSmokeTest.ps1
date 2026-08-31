$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$testExecutable = Join-Path $artifactRoot 'WorkspaceToolLoopEndToEndSmokeTest.exe'

if (-not (Test-Path -LiteralPath $artifactRoot)) {
    New-Item -ItemType Directory -Path $artifactRoot | Out-Null
}

$arguments = @(
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
    "/reference:$(Join-Path $frameworkRoot 'System.Net.Http.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Security.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Web.Extensions.dll')",
    (Join-Path $projectRoot 'src\AppDataPath.cs'),
    (Join-Path $projectRoot 'src\AtomicFile.cs'),
    (Join-Path $projectRoot 'src\Models.cs'),
    (Join-Path $projectRoot 'src\ConversationModels.cs'),
    (Join-Path $projectRoot 'src\ExtensionModels.cs'),
    (Join-Path $projectRoot 'src\CodeWorkspace.cs'),
    (Join-Path $projectRoot 'src\CodeWorkspaceToolProvider.cs'),
    (Join-Path $projectRoot 'src\ModelClient.cs'),
    (Join-Path $testRoot 'WorkspaceToolLoopEndToEndSmokeTest.cs')
)

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Workspace tool loop end-to-end test compilation failed with exit code $LASTEXITCODE."
}

& $testExecutable
if ($LASTEXITCODE -ne 0) {
    throw "Workspace tool loop end-to-end test failed with exit code $LASTEXITCODE."
}
