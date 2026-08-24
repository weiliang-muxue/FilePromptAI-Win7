$ErrorActionPreference = 'Stop'

$application = Join-Path (
    Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
) 'dist\FilePromptAI.exe'

$process = Start-Process `
    -FilePath $application `
    -WindowStyle Hidden `
    -PassThru

try {
    Start-Sleep -Seconds 3
    $process.Refresh()
    if ($process.HasExited) {
        throw "FilePrompt AI exited during startup with code $($process.ExitCode)."
    }

    if (-not $process.Responding) {
        throw 'FilePrompt AI started but is not responding.'
    }

    Write-Host "PASS | startup | pid=$($process.Id) | responding=$($process.Responding)"
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
