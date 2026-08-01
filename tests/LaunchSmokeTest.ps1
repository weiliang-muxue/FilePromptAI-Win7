$ErrorActionPreference = 'Stop'

$application = Join-Path (
    Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
) 'dist\FilePrompt.exe'

$process = Start-Process `
    -FilePath $application `
    -WindowStyle Hidden `
    -PassThru

try {
    Start-Sleep -Seconds 3
    $process.Refresh()
    if ($process.HasExited) {
        throw "FilePrompt exited during startup with code $($process.ExitCode)."
    }

    if (-not $process.Responding) {
        throw 'FilePrompt started but is not responding.'
    }

    Write-Host "PASS | startup | pid=$($process.Id) | responding=$($process.Responding)"
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
