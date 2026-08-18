param(
    [ValidateSet('Skills', 'Mcp')]
    [string]$Mode = 'Skills'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$application = Join-Path $projectRoot 'dist\FilePromptAI.exe'
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$hostExecutable = Join-Path $artifactRoot 'ExtensionsDialogHost.exe'
$outputPath = Join-Path $artifactRoot (
    'FilePromptAI-ui-v1.12-extensions-' + $Mode.ToLowerInvariant() + '.png'
)
$profileRoot = Join-Path $artifactRoot 'extensions-ui-profile'

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:5',
    '/codepage:65001',
    '/warn:4',
    "/out:$hostExecutable",
    "/reference:$(Join-Path $frameworkRoot 'System.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Core.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Windows.Forms.dll')",
    (Join-Path $testRoot 'ExtensionsDialogHost.cs')
)
& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Extensions dialog host compilation failed with exit code $LASTEXITCODE."
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ExtensionsCaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(
        IntPtr handle,
        IntPtr deviceContext,
        uint flags);
}
'@

$startInfo = New-Object Diagnostics.ProcessStartInfo
$startInfo.FileName = $hostExecutable
$startInfo.Arguments = '"' + $application + '" ' + $Mode.ToLowerInvariant()
$startInfo.WorkingDirectory = $artifactRoot
$startInfo.UseShellExecute = $false
$previousDataRoot = $env:FILEPROMPTAI_DATA_ROOT
try {
    $env:FILEPROMPTAI_DATA_ROOT = $profileRoot
    $process = [Diagnostics.Process]::Start($startInfo)
}
finally {
    $env:FILEPROMPTAI_DATA_ROOT = $previousDataRoot
}

try {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and
        [DateTime]::UtcNow -lt $deadline)

    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Extensions dialog window was not created.'
    }

    Start-Sleep -Milliseconds 500
    $rect = New-Object ExtensionsCaptureNative+RECT
    if (-not [ExtensionsCaptureNative]::GetWindowRect(
        $process.MainWindowHandle,
        [ref]$rect
    )) {
        throw 'Could not read extensions dialog bounds.'
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object Drawing.Bitmap $width, $height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $deviceContext = $graphics.GetHdc()
        try {
            if (-not [ExtensionsCaptureNative]::PrintWindow(
                $process.MainWindowHandle,
                $deviceContext,
                0
            )) {
                throw 'PrintWindow could not capture extensions dialog.'
            }
        }
        finally {
            $graphics.ReleaseHdc($deviceContext)
        }

        $bitmap.Save($outputPath, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    Write-Host "PASS | extensions ui capture | mode=$Mode | ${width}x${height} | $outputPath"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
    }
}
