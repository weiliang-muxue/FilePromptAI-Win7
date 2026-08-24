$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$fakeDirectory = Join-Path $artifactRoot 'mcp server'
$fakeServer = Join-Path $fakeDirectory 'Fake MCP Server.exe'
$testExecutable = Join-Path $artifactRoot 'McpRuntimeSmokeTest.exe'

New-Item -ItemType Directory -Path $fakeDirectory -Force | Out-Null

$fakeArguments = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:5',
    '/codepage:65001',
    '/warn:4',
    "/out:$fakeServer",
    "/reference:$(Join-Path $frameworkRoot 'System.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Core.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Web.Extensions.dll')",
    (Join-Path $testRoot 'FakeMcpServer.cs')
)
& $compiler $fakeArguments
if ($LASTEXITCODE -ne 0) {
    throw "Fake MCP server compilation failed with exit code $LASTEXITCODE."
}

$testArguments = @(
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
    "/reference:$(Join-Path $frameworkRoot 'System.Xml.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Xml.Linq.dll')",
    (Join-Path $projectRoot 'src\AppDataPath.cs'),
    (Join-Path $projectRoot 'src\AtomicFile.cs'),
    (Join-Path $projectRoot 'src\ExtensionModels.cs'),
    (Join-Path $projectRoot 'src\ExtensionStore.cs'),
    (Join-Path $projectRoot 'src\McpRuntime.cs'),
    (Join-Path $testRoot 'McpRuntimeSmokeTest.cs')
)
& $compiler $testArguments
if ($LASTEXITCODE -ne 0) {
    throw "MCP runtime test compilation failed with exit code $LASTEXITCODE."
}

& $testExecutable $fakeServer
if ($LASTEXITCODE -ne 0) {
    throw "MCP runtime test failed with exit code $LASTEXITCODE."
}
