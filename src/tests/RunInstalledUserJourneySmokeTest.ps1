#Requires -Version 5.1

param(
    [string]$Version = '1.17',
    [string]$ArchivePath = ''
)

$minimumPowerShellVersion = [Version]'5.1'
if ($PSVersionTable.PSVersion -lt $minimumPowerShellVersion -or
    $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'RunInstalledUserJourneySmokeTest.ps1 requires Windows PowerShell 5.1 or later.'
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if (-not ('FilePromptAIInstalledJourneyNativeMethods' -as [type])) {
    Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class FilePromptAIInstalledJourneyNativeMethods
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorInvalidParameter = 87;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr processHandle,
        uint flags,
        StringBuilder executablePath,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(
        IntPtr processHandle,
        out uint exitCode);

    public static string GetProcessImagePath(int processId)
    {
        IntPtr processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (processHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorInvalidParameter)
            {
                return null;
            }
            throw new Win32Exception(
                error,
                "Unable to open process " + processId + ".");
        }

        try
        {
            StringBuilder executablePath = new StringBuilder(32768);
            uint size = (uint)executablePath.Capacity;
            if (QueryFullProcessImageNameW(
                processHandle,
                0,
                executablePath,
                ref size))
            {
                return executablePath.ToString();
            }

            int error = Marshal.GetLastWin32Error();
            if (error == ErrorInvalidHandle ||
                error == ErrorInvalidParameter)
            {
                return null;
            }
            throw new Win32Exception(
                error,
                "Unable to query process " + processId + " image path.");
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }
}
'@
}

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch '^[0-9A-Za-z](?:[0-9A-Za-z._-]{0,30}[0-9A-Za-z])?$') {
    throw 'Version may contain only letters, digits, dots, underscores, and hyphens.'
}

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$repositoryRoot = Split-Path -Parent $projectRoot
$frameworkRoot = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$runId = [Guid]::NewGuid().ToString('N')
$testExecutable = Join-Path $artifactRoot (
    "InstalledUserJourneySmokeTest-$runId.exe")
$probeSource = Join-Path $testRoot 'LauncherEnvironmentProbe.cs'
$probeExecutable = Join-Path $artifactRoot (
    "LauncherEnvironmentProbe-$runId.exe")
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$resolvedArchivePath = if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    Join-Path (Join-Path $repositoryRoot 'exe') $archiveName
}
else {
    [IO.Path]::GetFullPath($ArchivePath)
}
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'FilePromptAI-InstalledJourney-' + [Guid]::NewGuid().ToString('N')
)
$extractedRoot = Join-Path $temporaryRoot 'package'
$dataRoot = Join-Path $temporaryRoot 'data with spaces'

function Get-Sha256Hex {
    param([string]$Path)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Get-DirectorySnapshot {
    param([string]$Root)

    $files = @{}
    $exists = Test-Path -LiteralPath $Root -PathType Container
    if (-not $exists) {
        return [pscustomobject]@{
            Exists = $false
            Files = $files
        }
    }

    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    foreach ($file in @(Get-ChildItem `
            -LiteralPath $resolvedRoot `
            -File `
            -Recurse `
            -Force)) {
        $relativePath = $file.FullName.Substring(
            $resolvedRoot.Length).TrimStart('\')
        $files[$relativePath] = '{0}:{1}' -f `
            $file.Length,
            (Get-Sha256Hex -Path $file.FullName)
    }

    return [pscustomobject]@{
        Exists = $true
        Files = $files
    }
}

function Assert-DirectorySnapshotEqual {
    param(
        [object]$Before,
        [object]$After,
        [string]$Scenario
    )

    if ($Before.Exists -ne $After.Exists) {
        throw "$Scenario changed whether the directory exists."
    }
    $beforeNames = @($Before.Files.Keys | Sort-Object)
    $afterNames = @($After.Files.Keys | Sort-Object)
    if (($beforeNames -join "`n") -ne ($afterNames -join "`n")) {
        throw "$Scenario changed the file set."
    }
    foreach ($name in $beforeNames) {
        if ($Before.Files[$name] -ne $After.Files[$name]) {
            throw "$Scenario changed file bytes: $name"
        }
    }
}

function Assert-NoExistingFilePromptAIProcesses {
    $existing = @(Get-Process -Name 'FilePromptAI' -ErrorAction SilentlyContinue)
    try {
        if ($existing.Count -ne 0) {
            $ids = @($existing | ForEach-Object { $_.Id }) -join ', '
            throw "The installed journey will not run while another FilePromptAI process exists: $ids"
        }
    }
    finally {
        foreach ($process in $existing) {
            $process.Dispose()
        }
    }
}

function Test-ProcessExecutablePath {
    param(
        [Diagnostics.Process]$Process,
        [string]$ExecutablePath
    )

    $candidatePath = `
        [FilePromptAIInstalledJourneyNativeMethods]::GetProcessImagePath(
            $Process.Id)
    if ([string]::IsNullOrEmpty($candidatePath)) {
        return $false
    }

    return [string]::Equals(
        [IO.Path]::GetFullPath($candidatePath),
        [IO.Path]::GetFullPath($ExecutablePath),
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-ProcessesByExecutablePath {
    param([string]$ExecutablePath)

    $fullPath = [IO.Path]::GetFullPath($ExecutablePath)
    $processName = [IO.Path]::GetFileNameWithoutExtension($fullPath)
    $matches = @()
    foreach ($candidate in @(Get-Process `
            -Name $processName `
            -ErrorAction SilentlyContinue)) {
        $matched = $false
        try {
            $candidate.Refresh()
            $matched = -not $candidate.HasExited -and
                (Test-ProcessExecutablePath `
                    -Process $candidate `
                    -ExecutablePath $fullPath)
            if ($matched) {
                $matches += $candidate
            }
        }
        finally {
            if (-not $matched) {
                $candidate.Dispose()
            }
        }
    }
    return @($matches)
}

function Stop-AndAssertNoProcessesByPath {
    param([string]$ExecutablePath)

    $cleanupErrors = New-Object Collections.Generic.List[string]
    foreach ($process in @(Get-ProcessesByExecutablePath `
            -ExecutablePath $ExecutablePath)) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                $process.Kill()
                if (-not $process.WaitForExit(10000)) {
                    $cleanupErrors.Add(
                        "PID $($process.Id) did not exit after Kill().")
                }
            }
        }
        catch {
            $cleanupErrors.Add(
                "PID $($process.Id) could not be terminated: $($_.Exception.Message)")
        }
        finally {
            $process.Dispose()
        }
    }

    $remaining = @(Get-ProcessesByExecutablePath `
        -ExecutablePath $ExecutablePath)
    try {
        if ($remaining.Count -ne 0) {
            $remainingIds = @($remaining | ForEach-Object { $_.Id }) -join ', '
            $cleanupErrors.Add(
                "processes still use the extracted executable: $remainingIds")
        }
    }
    finally {
        foreach ($process in $remaining) {
            $process.Dispose()
        }
    }

    if ($cleanupErrors.Count -ne 0) {
        throw "FilePromptAI process cleanup failed: $($cleanupErrors -join ' ')"
    }
}

function ConvertTo-WindowsCommandLineArgument {
    param([AllowEmptyString()][string]$Value)

    if ([string]::IsNullOrEmpty($Value)) {
        return '""'
    }
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            for ($index = 0; $index -lt (2 * $backslashes + 1); $index++) {
                [void]$builder.Append('\')
            }
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        for ($index = 0; $index -lt $backslashes; $index++) {
            [void]$builder.Append('\')
        }
        $backslashes = 0
        [void]$builder.Append($character)
    }
    for ($index = 0; $index -lt (2 * $backslashes); $index++) {
        [void]$builder.Append('\')
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-CheckedProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$Name,
        [int]$TimeoutMilliseconds
    )

    $stdoutPath = Join-Path $temporaryRoot ($Name + '.stdout.txt')
    $stderrPath = Join-Path $temporaryRoot ($Name + '.stderr.txt')
    $quoted = @($Arguments | ForEach-Object {
        ConvertTo-WindowsCommandLineArgument -Value $_
    })
    $argumentLine = [string]::Join(' ', [string[]]$quoted)
    $process = Start-Process `
        -FilePath $FilePath `
        -ArgumentList $argumentLine `
        -WorkingDirectory $WorkingDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru
    $nativeProcessHandle = $process.Handle
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        try {
            $process.Kill()
        }
        catch {
        }
        $process.WaitForExit()
        throw "$Name timed out after $TimeoutMilliseconds ms."
    }

    # Drain redirected streams, then refresh the Process wrapper before
    # reading ExitCode. Without both calls Windows PowerShell may return null.
    $process.WaitForExit()
    $process.Refresh()
    [uint32]$nativeExitCode = 0
    if (-not [FilePromptAIInstalledJourneyNativeMethods]::GetExitCodeProcess(
            $nativeProcessHandle,
            [ref]$nativeExitCode)) {
        $nativeError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "Unable to read the $Name exit code (Win32 $nativeError)."
    }
    $exitCode = [uint64]$nativeExitCode
    $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
        Get-Content -LiteralPath $stdoutPath -Raw
    }
    else {
        ''
    }
    $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
        Get-Content -LiteralPath $stderrPath -Raw
    }
    else {
        ''
    }
    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host $stdout.TrimEnd()
    }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr.TrimEnd()
    }
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode."
    }

    return $exitCode
}

function Assert-LauncherEnvironmentForwarding {
    param(
        [string]$LauncherPath,
        [string]$LauncherConfigPath,
        [string]$ProbeExecutable,
        [string]$ProbeRoot,
        [string]$DataRoot
    )

    $probeAppRoot = Join-Path $ProbeRoot 'app'
    $probeLauncher = Join-Path $ProbeRoot 'Start-FilePromptAI.exe'
    $probeLauncherConfig = "$probeLauncher.config"
    $probeApplication = Join-Path $probeAppRoot 'FilePromptAI.exe'
    New-Item -ItemType Directory -Path $probeAppRoot -Force | Out-Null
    Copy-Item -LiteralPath $LauncherPath -Destination $probeLauncher -Force
    Copy-Item `
        -LiteralPath $LauncherConfigPath `
        -Destination $probeLauncherConfig `
        -Force
    Copy-Item `
        -LiteralPath $ProbeExecutable `
        -Destination $probeApplication `
        -Force

    $previousDataRoot = [Environment]::GetEnvironmentVariable(
        'FILEPROMPTAI_DATA_ROOT',
        [EnvironmentVariableTarget]::Process)
    $launcher = $null
    try {
        [Environment]::SetEnvironmentVariable(
            'FILEPROMPTAI_DATA_ROOT',
            $DataRoot,
            [EnvironmentVariableTarget]::Process)
        $launcher = Start-Process `
            -FilePath $probeLauncher `
            -WorkingDirectory $ProbeRoot `
            -PassThru
        if (-not $launcher.WaitForExit(10000)) {
            throw 'The probe root launcher did not exit within 10 seconds.'
        }
        $launcher.WaitForExit()
        $launcher.Refresh()
        if ($launcher.ExitCode -ne 0) {
            throw "The probe root launcher returned exit code $($launcher.ExitCode)."
        }

        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        $reports = @()
        while ([DateTime]::UtcNow -lt $deadline) {
            $reports = @(Get-ChildItem `
                -LiteralPath $probeAppRoot `
                -Filter 'launcher-environment-*.txt' `
                -File `
                -ErrorAction SilentlyContinue)
            if ($reports.Count -ne 0) {
                break
            }
            Start-Sleep -Milliseconds 50
        }
        if ($reports.Count -ne 1) {
            throw "The launcher environment probe produced $($reports.Count) report files."
        }

        $bytes = [IO.File]::ReadAllBytes($reports[0].FullName)
        if ($bytes.Length -ge 3 -and
            $bytes[0] -eq 0xEF -and
            $bytes[1] -eq 0xBB -and
            $bytes[2] -eq 0xBF) {
            throw 'The launcher environment probe report unexpectedly has a UTF-8 BOM.'
        }
        $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
        $actualDataRoot = $strictUtf8.GetString($bytes)
        if (-not [string]::Equals(
                $actualDataRoot,
                $DataRoot,
                [StringComparison]::Ordinal)) {
            throw "The root launcher did not forward FILEPROMPTAI_DATA_ROOT exactly. Expected '$DataRoot'; actual '$actualDataRoot'."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'FILEPROMPTAI_DATA_ROOT',
            $previousDataRoot,
            [EnvironmentVariableTarget]::Process)
        if ($null -ne $launcher) {
            try {
                $launcher.Refresh()
                if (-not $launcher.HasExited) {
                    $launcher.Kill()
                    $launcher.WaitForExit()
                }
            }
            catch {
            }
            $launcher.Dispose()
        }
        Stop-AndAssertNoProcessesByPath -ExecutablePath $probeApplication
    }

    Write-Host "PASS | root launcher environment probe | isolated=$DataRoot; utf8NoBom=true; exact=true"
}

function Assert-RealPackagedApplicationLaunch {
    param(
        [string]$LauncherPath,
        [string]$ApplicationPath,
        [string]$WorkingDirectory,
        [string]$DataRoot
    )

    $expectedWindowTitle = 'FilePrompt AI  ' + [char]0x00B7 + '  ' +
        [char]0x5185 + [char]0x7F51 + [char]0x6587 + [char]0x4EF6 +
        [char]0x95EE + [char]0x7B54 + [char]0x5DE5 + [char]0x4F5C +
        [char]0x53F0
    $previousDataRoot = [Environment]::GetEnvironmentVariable(
        'FILEPROMPTAI_DATA_ROOT',
        [EnvironmentVariableTarget]::Process)
    $defaultDataRoot = Join-Path (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)) `
        'FilePromptAI-Win7'
    $defaultDataBefore = Get-DirectorySnapshot -Root $defaultDataRoot
    $applicationFullPath = [IO.Path]::GetFullPath($ApplicationPath)
    $launcher = $null
    $process = $null
    $secondLauncher = $null
    try {
        [Environment]::SetEnvironmentVariable(
            'FILEPROMPTAI_DATA_ROOT',
            $DataRoot,
            [EnvironmentVariableTarget]::Process)

        $existingProcessIds = @{}
        foreach ($existing in @(Get-Process `
                -Name ([IO.Path]::GetFileNameWithoutExtension($ApplicationPath)) `
                -ErrorAction SilentlyContinue)) {
            $existingProcessIds[$existing.Id] = $true
            $existing.Dispose()
        }
        if ($existingProcessIds.Count -ne 0) {
            throw 'The installed journey will not run while another FilePromptAI process exists.'
        }
        $launchStarted = [DateTime]::UtcNow.AddSeconds(-1)
        $launcher = Start-Process `
            -FilePath $LauncherPath `
            -WorkingDirectory $WorkingDirectory `
            -PassThru

        if (-not $launcher.WaitForExit(10000)) {
            throw 'The root launcher did not exit after starting the packaged application.'
        }
        $launcher.WaitForExit()
        $launcher.Refresh()
        if ($launcher.ExitCode -ne 0) {
            throw "The root launcher returned exit code $($launcher.ExitCode)."
        }

        $applicationProcessName = [IO.Path]::GetFileNameWithoutExtension(
            $applicationFullPath)
        $processDeadline = [DateTime]::UtcNow.AddSeconds(30)
        while ([DateTime]::UtcNow -lt $processDeadline -and $null -eq $process) {
            foreach ($candidate in @(Get-ProcessesByExecutablePath `
                    -ExecutablePath $applicationFullPath)) {
                $selected = $false
                try {
                    $candidate.Refresh()
                    if (-not $candidate.HasExited -and
                        -not $existingProcessIds.ContainsKey($candidate.Id) -and
                        $candidate.StartTime.ToUniversalTime() -ge $launchStarted) {
                        $process = $candidate
                        $selected = $true
                        break
                    }
                }
                catch [InvalidOperationException] {
                }
                finally {
                    if (-not $selected) {
                        $candidate.Dispose()
                    }
                }
            }
            if ($null -eq $process) {
                Start-Sleep -Milliseconds 100
            }
        }
        if ($null -eq $process) {
            throw 'The root launcher did not start the packaged app\\FilePromptAI.exe process within 30 seconds.'
        }

        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $windowHandle = [IntPtr]::Zero
        while ([DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) {
                throw "The real packaged application exited during startup with code $($process.ExitCode)."
            }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                $windowHandle = $process.MainWindowHandle
                break
            }
            Start-Sleep -Milliseconds 100
        }
        if ($windowHandle -eq [IntPtr]::Zero) {
            throw 'The real packaged application did not create a main window within 30 seconds.'
        }

        $responsiveDeadline = [DateTime]::UtcNow.AddSeconds(10)
        while ([DateTime]::UtcNow -lt $responsiveDeadline) {
            $process.Refresh()
            if ($process.HasExited) {
                throw "The real packaged application exited after creating its window with code $($process.ExitCode)."
            }
            if ($process.Responding -and
                $process.MainWindowTitle -ceq $expectedWindowTitle) {
                break
            }
            Start-Sleep -Milliseconds 100
        }
        $process.Refresh()
        if ($process.MainWindowTitle -cne $expectedWindowTitle) {
            throw "The real packaged application opened an unexpected window: $($process.MainWindowTitle)"
        }
        if (-not $process.Responding) {
            throw 'The real packaged application main window is not responding.'
        }

        $secondLauncher = Start-Process `
            -FilePath $LauncherPath `
            -WorkingDirectory $WorkingDirectory `
            -PassThru
        if (-not $secondLauncher.WaitForExit(10000)) {
            throw 'A second root launcher did not exit after invoking the single-instance application.'
        }
        $secondLauncher.WaitForExit()
        $secondLauncher.Refresh()
        if ($secondLauncher.ExitCode -ne 0) {
            throw "The second root launcher returned exit code $($secondLauncher.ExitCode)."
        }
        Start-Sleep -Seconds 2
        foreach ($candidate in @(Get-Process `
                -Name $applicationProcessName `
                -ErrorAction SilentlyContinue)) {
            try {
                $candidate.Refresh()
                if (-not $candidate.HasExited -and
                    $candidate.Id -ne $process.Id -and
                    (Test-ProcessExecutablePath `
                        -Process $candidate `
                        -ExecutablePath $applicationFullPath)) {
                    throw "A second root launch left another packaged application running: PID $($candidate.Id)."
                }
            }
            finally {
                $candidate.Dispose()
            }
        }
        $process.Refresh()
        if ($process.HasExited -or -not $process.Responding) {
            throw 'The primary packaged application did not remain responsive after a second launch.'
        }

        $nativeProcessHandle = $process.Handle
        if (-not $process.CloseMainWindow()) {
            throw 'The real packaged application did not accept a normal main-window close request.'
        }
        if (-not $process.WaitForExit(30000)) {
            throw 'The real packaged application did not exit normally within 30 seconds.'
        }
        $process.WaitForExit()
        [uint32]$nativeExitCode = 0
        if (-not [FilePromptAIInstalledJourneyNativeMethods]::GetExitCodeProcess(
                $nativeProcessHandle,
                [ref]$nativeExitCode)) {
            $nativeError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "Unable to read the real packaged application exit code (Win32 $nativeError)."
        }
        if ($nativeExitCode -ne 0) {
            throw "The real packaged application returned exit code $nativeExitCode after normal close."
        }
        $conversationPath = Join-Path $DataRoot 'conversations.xml'
        if (-not (Test-Path -LiteralPath $conversationPath -PathType Leaf)) {
            throw 'The real packaged application did not persist conversations.xml during normal close.'
        }
        $settingsPath = Join-Path $DataRoot 'settings.xml'
        if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
            throw 'A blank first launch unexpectedly wrote unchanged default settings.'
        }
        Write-Host "PASS | real root-launcher startup | launcherExit=0; appPid=$($process.Id); window=$windowHandle; singleton=true; responding=true; exit=0"
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'FILEPROMPTAI_DATA_ROOT',
            $previousDataRoot,
            [EnvironmentVariableTarget]::Process)
        if ($null -ne $launcher) {
            try {
                $launcher.Refresh()
                if (-not $launcher.HasExited) {
                    $launcher.Kill()
                    $launcher.WaitForExit()
                }
            }
            catch {
            }
            $launcher.Dispose()
        }
        if ($null -ne $process) {
            try {
                $process.Refresh()
                if (-not $process.HasExited) {
                    $process.Kill()
                    $process.WaitForExit()
                }
            }
            catch {
            }
            $process.Dispose()
        }
        if ($null -ne $secondLauncher) {
            try {
                $secondLauncher.Refresh()
                if (-not $secondLauncher.HasExited) {
                    $secondLauncher.Kill()
                    $secondLauncher.WaitForExit()
                }
            }
            catch {
            }
            $secondLauncher.Dispose()
        }
        $cleanupFailure = ''
        $snapshotFailure = ''
        try {
            Stop-AndAssertNoProcessesByPath `
                -ExecutablePath $applicationFullPath
        }
        catch {
            $cleanupFailure = $_.Exception.Message
        }
        try {
            Assert-DirectorySnapshotEqual `
                -Before $defaultDataBefore `
                -After (Get-DirectorySnapshot -Root $defaultDataRoot) `
                -Scenario 'Root launcher environment forwarding'
        }
        catch {
            $snapshotFailure = $_.Exception.Message
        }
        if (-not [string]::IsNullOrEmpty($cleanupFailure) -or
            -not [string]::IsNullOrEmpty($snapshotFailure)) {
            throw "Installed journey cleanup verification failed. Process cleanup: $cleanupFailure Default data root: $snapshotFailure"
        }
        Write-Host "PASS | root launcher preserved FILEPROMPTAI_DATA_ROOT | isolated=$DataRoot; defaultRootUnchanged=true"
    }
}

function Assert-SafeArchiveEntries {
    param(
        [string]$Path,
        [string]$Destination
    )

    $destinationFull = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    $seen = @{}
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 10000) {
            throw 'The release ZIP has an invalid entry count.'
        }
        foreach ($entry in $archive.Entries) {
            $entryName = $entry.FullName
            if ([string]::IsNullOrWhiteSpace($entryName) -or
                ($entryName.IndexOf('/') -ge 0 -and
                    $entryName.IndexOf('\') -ge 0) -or
                $entryName.StartsWith('/', [StringComparison]::Ordinal) -or
                $entryName.StartsWith('\', [StringComparison]::Ordinal) -or
                $entryName.IndexOf(':') -ge 0) {
                throw "The release ZIP contains an unsafe entry: $entryName"
            }

            $normalizedName = $entryName.Replace('/', '\')
            $isDirectory = $normalizedName.EndsWith(
                '\',
                [StringComparison]::Ordinal)
            $trimmedName = if ($isDirectory) {
                $normalizedName.Substring(0, $normalizedName.Length - 1)
            }
            else {
                $normalizedName
            }
            $segments = @($trimmedName -split '\\')
            if ($segments.Count -eq 0 -or
                @($segments | Where-Object {
                    [string]::IsNullOrEmpty($_) -or
                    $_ -eq '.' -or
                    $_ -eq '..' -or
                    $_.TrimEnd(' ', '.') -cne $_
                }).Count -ne 0) {
                throw "The release ZIP contains a non-canonical entry: $entryName"
            }

            $key = $trimmedName
            if ($seen.ContainsKey($key)) {
                throw "The release ZIP contains a duplicate entry: $entryName"
            }
            $seen[$key] = $true
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $key))
            if (-not $target.StartsWith(
                    $destinationFull,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "The release ZIP entry escapes the extraction root: $entryName"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-PackageManifest {
    param([string]$PackageRoot)

    $manifestName = 'PACKAGE-CHECKSUMS-SHA256.txt'
    $manifestPath = Join-Path $PackageRoot $manifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "The extracted package is missing $manifestName."
    }

    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    if ($manifestBytes.Length -eq 0 -or
        $manifestBytes.Length -gt 4MB -or
        ($manifestBytes.Length -ge 3 -and
            $manifestBytes[0] -eq 0xEF -and
            $manifestBytes[1] -eq 0xBB -and
            $manifestBytes[2] -eq 0xBF)) {
        throw 'The package checksum manifest has invalid bytes or a UTF-8 BOM.'
    }
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    $manifestText = $strictUtf8.GetString($manifestBytes)
    if (-not $manifestText.EndsWith("`r`n", [StringComparison]::Ordinal) -or
        [regex]::IsMatch($manifestText, '(?<!\r)\n|\r(?!\n)')) {
        throw 'The package checksum manifest must use canonical CRLF lines.'
    }

    $packagePrefix = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\') + '\'
    $expected = New-Object `
        'System.Collections.Generic.Dictionary[string,bool]' `
        ([StringComparer]::Ordinal)
    $lines = @($manifestText.Substring(
        0,
        $manifestText.Length - 2) -split "`r`n")
    foreach ($line in $lines) {
        $match = [regex]::Match(
            $line,
            '^(?<Hash>[0-9A-F]{64}) \*(?<Path>[^\r\n]+)$')
        if (-not $match.Success) {
            throw "The package checksum manifest has a non-canonical line: $line"
        }
        $expectedHash = $match.Groups['Hash'].Value
        $relativePath = $match.Groups['Path'].Value
        $segments = @($relativePath -split '\\')
        if ($relativePath.IndexOf('/') -ge 0 -or
            $relativePath.IndexOf(':') -ge 0 -or
            $relativePath.StartsWith('\', [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                $relativePath,
                $relativePath.Trim(),
                [StringComparison]::Ordinal) -or
            @($segments | Where-Object {
                [string]::IsNullOrEmpty($_) -or
                $_ -eq '.' -or
                $_ -eq '..' -or
                $_.TrimEnd(' ', '.') -cne $_
            }).Count -ne 0 -or
            [string]::Equals(
                $relativePath,
                $manifestName,
                [StringComparison]::OrdinalIgnoreCase) -or
            $expected.ContainsKey($relativePath)) {
            throw "The package checksum manifest has an unsafe or duplicate path: $relativePath"
        }

        $payloadPath = [IO.Path]::GetFullPath((Join-Path $PackageRoot $relativePath))
        if (-not $payloadPath.StartsWith(
                $packagePrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw "A manifest payload is missing or outside the package: $relativePath"
        }
        $canonicalRelativePath = $payloadPath.Substring($packagePrefix.Length)
        if (-not [string]::Equals(
                $canonicalRelativePath,
                $relativePath,
                [StringComparison]::Ordinal)) {
            throw "A manifest payload path is not canonical: $relativePath"
        }
        $actualHash = Get-Sha256Hex -Path $payloadPath
        if (-not [string]::Equals(
                $expectedHash,
                $actualHash,
                [StringComparison]::Ordinal)) {
            throw "A manifest payload hash does not match: $relativePath"
        }
        $expected[$relativePath] = $true
    }
    if ($expected.Count -eq 0) {
        throw 'The package checksum manifest is empty.'
    }

    $actualFiles = @([IO.Directory]::GetFiles(
        $PackageRoot,
        '*',
        [IO.SearchOption]::AllDirectories))
    if ($actualFiles.Count -ne $expected.Count + 1) {
        throw "The extracted package file set differs from its manifest: files=$($actualFiles.Count); manifest=$($expected.Count)."
    }
    foreach ($actualFile in $actualFiles) {
        $relativePath = $actualFile.Substring($packagePrefix.Length)
        if ([string]::Equals(
            $relativePath,
            $manifestName,
            [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not $expected.ContainsKey($relativePath)) {
            throw "The extracted package contains an unlisted file: $relativePath"
        }
    }

    Write-Host "PASS | package manifest | entries=$($expected.Count); exactFiles=$($actualFiles.Count)"
}

foreach ($required in @($compiler, $resolvedArchivePath, $probeSource)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required installed-journey input is missing: $required"
    }
}
Assert-NoExistingFilePromptAIProcesses
try {
    if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
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
        "/reference:$(Join-Path $frameworkRoot 'System.Drawing.dll')",
        "/reference:$(Join-Path $frameworkRoot 'System.Windows.Forms.dll')",
        (Join-Path $testRoot 'InstalledUserJourneySmokeTest.cs'),
        (Join-Path $projectRoot 'acceptance\PackagedUiJourney.cs')
    )
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Installed user journey host compilation failed with exit code $LASTEXITCODE."
    }

    $probeArguments = @(
        '/nologo',
        '/target:winexe',
        '/platform:anycpu',
        '/optimize+',
        '/langversion:5',
        '/codepage:65001',
        '/warn:4',
        "/out:$probeExecutable",
        "/reference:$(Join-Path $frameworkRoot 'System.dll')",
        $probeSource
    )
    & $compiler $probeArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher environment probe compilation failed with exit code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Path $extractedRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Assert-SafeArchiveEntries `
        -Path $resolvedArchivePath `
        -Destination $extractedRoot
    [IO.Compression.ZipFile]::ExtractToDirectory(
        $resolvedArchivePath,
        $extractedRoot)
    Assert-PackageManifest -PackageRoot $extractedRoot

    $launcherPath = Join-Path $extractedRoot 'Start-FilePromptAI.exe'
    $uninstallerPath = Join-Path $extractedRoot 'Uninstall-FilePromptAI.exe'
    $applicationPath = Join-Path $extractedRoot 'app\FilePromptAI.exe'
    foreach ($required in @($launcherPath, $uninstallerPath, $applicationPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "The final ZIP is missing a required executable: $required"
        }
    }

    [void](Invoke-CheckedProcess `
        -FilePath $launcherPath `
        -Arguments @('--check') `
        -WorkingDirectory $extractedRoot `
        -Name 'launcher-check' `
        -TimeoutMilliseconds 30000)
    Write-Host 'PASS | root launcher --check'
    [void](Invoke-CheckedProcess `
        -FilePath $uninstallerPath `
        -Arguments @('--check') `
        -WorkingDirectory $extractedRoot `
        -Name 'uninstaller-check' `
        -TimeoutMilliseconds 30000)
    Write-Host 'PASS | root uninstaller --check'

    $probeRoot = Join-Path $temporaryRoot 'probe-root'
    $probeDataRoot = Join-Path $temporaryRoot 'probe data root with spaces'
    New-Item -ItemType Directory -Path $probeDataRoot -Force | Out-Null
    Assert-LauncherEnvironmentForwarding `
        -LauncherPath $launcherPath `
        -LauncherConfigPath "$launcherPath.config" `
        -ProbeExecutable $probeExecutable `
        -ProbeRoot $probeRoot `
        -DataRoot $probeDataRoot

    $realLaunchDataRoot = Join-Path $temporaryRoot 'real launch data with spaces'
    New-Item -ItemType Directory -Path $realLaunchDataRoot -Force | Out-Null
    Assert-RealPackagedApplicationLaunch `
        -LauncherPath $launcherPath `
        -ApplicationPath $applicationPath `
        -WorkingDirectory $extractedRoot `
        -DataRoot $realLaunchDataRoot

    $installedTestExecutable = Join-Path `
        (Split-Path -Parent $applicationPath) `
        'InstalledUserJourneySmokeTest.exe'
    Write-Host (
        'CHECK | temporary installed-journey host is injected only after ' +
        'the exact ZIP manifest passed; it is not a package payload')
    Copy-Item -LiteralPath $testExecutable `
        -Destination $installedTestExecutable -Force
    Copy-Item -LiteralPath "$applicationPath.config" `
        -Destination "$installedTestExecutable.config" -Force
    [void](Invoke-CheckedProcess `
        -FilePath $installedTestExecutable `
        -Arguments @($applicationPath, $dataRoot) `
        -WorkingDirectory (Split-Path -Parent $applicationPath) `
        -Name 'installed-ui-journey' `
        -TimeoutMilliseconds 90000)
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemporary.StartsWith(
        $resolvedSystemTemp,
        [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporary).StartsWith(
            'FilePromptAI-InstalledJourney-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
    foreach ($artifact in @($testExecutable, $probeExecutable)) {
        if (Test-Path -LiteralPath $artifact -PathType Leaf) {
            Remove-Item -LiteralPath $artifact -Force
        }
    }
}

Write-Host "PASS | final ZIP installed user journey | archive=$resolvedArchivePath"
