param(
    [ValidateSet('Empty', 'Conversation')]
    [string]$Mode = 'Conversation',
    [switch]$MinimumWindow,
    [switch]$Physical125
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$application = Join-Path $projectRoot 'dist\FilePromptAI.exe'
$artifactRoot = Join-Path $projectRoot 'tests\build-artifacts'
$profileRoot = Join-Path $artifactRoot ('ui-profile-' + $Mode.ToLowerInvariant())
$applicationData = Join-Path $profileRoot 'FilePromptAI-Win7'
$sizeSuffix = if ($MinimumWindow) { '-minimum' } else { '' }
$outputPath = Join-Path $artifactRoot (
    'FilePromptAI-ui-v1.11-' + $Mode.ToLowerInvariant() + $sizeSuffix + '.png'
)

if (-not (Test-Path -LiteralPath $artifactRoot)) {
    New-Item -ItemType Directory -Path $artifactRoot | Out-Null
}

if (Test-Path -LiteralPath $profileRoot) {
    Remove-Item -LiteralPath $profileRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $applicationData -Force | Out-Null

if ($Mode -eq 'Conversation') {
    Add-Type -AssemblyName System.Security
    $entropy = [System.Text.Encoding]::UTF8.GetBytes('FilePromptAIWin7.Settings.v1')
    $clearKey = [System.Text.Encoding]::UTF8.GetBytes('ui-smoke-key')
    $protectedKey = [System.Security.Cryptography.ProtectedData]::Protect(
        $clearKey,
        $entropy,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    $encodedKey = [Convert]::ToBase64String($protectedKey)
    $settings = @"
<?xml version="1.0" encoding="utf-8"?>
<FilePromptAISettings version="1">
  <EndpointUrl>http://127.0.0.1:19999/v1/chat/completions</EndpointUrl>
  <ModelName>internal-test-model</ModelName>
  <ProtectedApiKey>$encodedKey</ProtectedApiKey>
</FilePromptAISettings>
"@
    [IO.File]::WriteAllText(
        (Join-Path $applicationData 'settings.xml'),
        $settings,
        [Text.UTF8Encoding]::new($true)
    )

    $now = [DateTime]::UtcNow.ToString('o')
    $conversation = @"
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<ConversationStore version="1" currentSessionId="session-a">
  <Session id="session-a" title="Quarterly report review" createdAt="$now" updatedAt="$now">
    <Messages>
      <Message role="user" createdAt="$now">Summarize the key metrics and risks.</Message>
      <Message role="assistant" createdAt="$now">## Key findings

- Revenue continues to grow
- Cost movement needs review

| Metric | Current | Change |
| --- | --- | --- |
| Revenue | 1.28m | +8% |
| Cost | 0.76m | +12% |

Review the source of the cost increase first.</Message>
    </Messages>
  </Session>
  <Session id="session-b" title="Contract terms review" createdAt="$now" updatedAt="$now">
    <Messages>
      <Message role="user" createdAt="$now">Review payment and default clauses.</Message>
    </Messages>
  </Session>
</ConversationStore>
"@
    [IO.File]::WriteAllText(
        (Join-Path $applicationData 'conversations.xml'),
        $conversation,
        [Text.UTF8Encoding]::new($true)
    )
    [xml](Get-Content -LiteralPath (
        Join-Path $applicationData 'conversations.xml'
    ) -Raw) | Out-Null
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class FilePromptAICaptureNative
{
    public delegate bool EnumChildCallback(IntPtr handle, IntPtr parameter);

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
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(
        IntPtr parent,
        EnumChildCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(
        IntPtr handle,
        System.Text.StringBuilder value,
        int maximum);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(
        IntPtr handle,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter);

    public static string DescribeListView(IntPtr parent)
    {
        string result = string.Empty;
        EnumChildWindows(parent, delegate(IntPtr handle, IntPtr parameter)
        {
            System.Text.StringBuilder name = new System.Text.StringBuilder(64);
            GetClassName(handle, name, name.Capacity);
            if (name.ToString().IndexOf(
                "SysListView32",
                StringComparison.OrdinalIgnoreCase) >= 0)
            {
                RECT rect;
                GetWindowRect(handle, out rect);
                int[] widths = new int[4];
                for (int index = 0; index < widths.Length; index++)
                {
                    widths[index] = SendMessage(
                        handle,
                        0x101D,
                        new IntPtr(index),
                        IntPtr.Zero).ToInt32();
                }

                result = (rect.Right - rect.Left).ToString() + "px columns=" +
                    string.Join(",", widths);
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

$startInfo = New-Object Diagnostics.ProcessStartInfo
$startInfo.FileName = $application
$startInfo.WorkingDirectory = Split-Path -Parent $application
$startInfo.UseShellExecute = $false
$previousDataRoot = $env:FILEPROMPTAI_DATA_ROOT
try {
    $env:FILEPROMPTAI_DATA_ROOT = $applicationData
    $process = [Diagnostics.Process]::Start($startInfo)
}
finally {
    $env:FILEPROMPTAI_DATA_ROOT = $previousDataRoot
}

try {
    $null = $process.WaitForInputIdle(10000)
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and
        [DateTime]::UtcNow -lt $deadline)

    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'FilePrompt AI main window was not created.'
    }

    if ($MinimumWindow) {
        if (-not [FilePromptAICaptureNative]::SetWindowPos(
            $process.MainWindowHandle,
            [IntPtr]::Zero,
            40,
            40,
            880,
            520,
            0x0040
        )) {
            throw 'Could not resize FilePrompt AI to its minimum window size.'
        }

        Start-Sleep -Milliseconds 300
    }

    $null = [FilePromptAICaptureNative]::SetForegroundWindow(
        $process.MainWindowHandle
    )
    Start-Sleep -Milliseconds 500

    $rect = New-Object FilePromptAICaptureNative+RECT
    if (-not [FilePromptAICaptureNative]::GetWindowRect(
        $process.MainWindowHandle,
        [ref]$rect
    )) {
        throw 'Could not read FilePrompt AI window bounds.'
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object Drawing.Bitmap $width, $height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $physicalPath = ''
    try {
        $deviceContext = $graphics.GetHdc()
        try {
            if (-not [FilePromptAICaptureNative]::PrintWindow(
                $process.MainWindowHandle,
                $deviceContext,
                0
            )) {
                throw 'PrintWindow could not capture FilePrompt AI.'
            }
        }
        finally {
            $graphics.ReleaseHdc($deviceContext)
        }
        $bitmap.Save($outputPath, [Drawing.Imaging.ImageFormat]::Png)
        if ($Physical125) {
            $physicalPath = [IO.Path]::Combine(
                [IO.Path]::GetDirectoryName($outputPath),
                [IO.Path]::GetFileNameWithoutExtension($outputPath) +
                    '-physical125.png'
            )
            $scaledWidth = [int][Math]::Round($width * 1.25)
            $scaledHeight = [int][Math]::Round($height * 1.25)
            $scaled = New-Object Drawing.Bitmap $scaledWidth, $scaledHeight
            $scaledGraphics = [Drawing.Graphics]::FromImage($scaled)
            try {
                $scaledGraphics.InterpolationMode =
                    [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $scaledGraphics.DrawImage(
                    $bitmap,
                    [Drawing.Rectangle]::new(
                        0,
                        0,
                        $scaledWidth,
                        $scaledHeight
                    )
                )
                $scaled.Save(
                    $physicalPath,
                    [Drawing.Imaging.ImageFormat]::Png
                )
            }
            finally {
                $scaledGraphics.Dispose()
                $scaled.Dispose()
            }
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    $dpi = 0
    try {
        $dpi = [FilePromptAICaptureNative]::GetDpiForWindow(
            $process.MainWindowHandle
        )
    }
    catch [EntryPointNotFoundException] {
        $dpi = 96
    }

    $listView = [FilePromptAICaptureNative]::DescribeListView(
        $process.MainWindowHandle
    )
    Write-Host "PASS | ui capture | mode=$Mode | ${width}x${height} | dpi=$dpi | list=$listView | $outputPath | physical125=$physicalPath"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
    }
}
