$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'

if (-not (Test-Path -LiteralPath $artifactRoot)) {
    New-Item -ItemType Directory -Path $artifactRoot | Out-Null
}

# Each invocation owns its compiler output and runtime directory. This keeps
# parallel smoke-test runs from moving or deleting one another's test files.
$runName = 'code-workspace-run-{0}-{1}' -f $PID, ([Guid]::NewGuid().ToString('N'))
$runRoot = Join-Path $artifactRoot $runName
$testExecutable = Join-Path $runRoot 'CodeWorkspaceSmokeTest.exe'
$runtimeRoot = Join-Path $runRoot 'runtime'
New-Item -ItemType Directory -Path $runRoot | Out-Null

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
    "/reference:$(Join-Path $frameworkRoot 'System.Security.dll')",
    (Join-Path $projectRoot 'src\AppDataPath.cs'),
    (Join-Path $projectRoot 'src\AtomicFile.cs'),
    (Join-Path $projectRoot 'src\CodeWorkspace.cs'),
    (Join-Path $testRoot 'CodeWorkspaceSmokeTest.cs')
)

try {
    & $compiler $compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Code workspace test compilation failed with exit code $LASTEXITCODE."
    }

    & $testExecutable $runtimeRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Code workspace test failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
