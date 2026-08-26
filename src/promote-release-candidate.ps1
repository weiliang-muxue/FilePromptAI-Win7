param(
    [string]$Version = '1.18',
    [string]$SourceRoot,
    [string]$DestinationRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch '^[0-9A-Za-z](?:[0-9A-Za-z._-]{0,30}[0-9A-Za-z])?$') {
    throw 'Version may contain only letters, digits, dots, underscores, and hyphens.'
}

$SourceProjectRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent $MyInvocation.MyCommand.Path))
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = $SourceProjectRoot
}
if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path (Split-Path -Parent $SourceProjectRoot) 'exe'
}
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$sourceArchive = Join-Path $SourceRoot $archiveName
$receiptPath = Join-Path $SourceRoot (
    "tests\build-artifacts\release\ReleaseCandidate-v$Version.txt")
$destinationArchive = Join-Path $DestinationRoot $archiveName
$evidenceHelper = Join-Path $SourceRoot 'tests\ReleaseAcceptanceEvidence.ps1'
$installedJourneyScript = Join-Path $SourceRoot (
    'tests\RunInstalledUserJourneySmokeTest.ps1')
$windowsPowerShell = Join-Path $PSHOME 'powershell.exe'
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
$utf8NoBom = New-Object Text.UTF8Encoding($false)

# A private promotion-test fixture changes this constant to exercise cleanup
# failure after a successful commit. Production executions keep it disabled.
$postCommitCleanupFailureForTests = $false
# Private fixture-only crash hooks. Production executions keep both disabled.
$crashAfterReplacementForTests = 0
$throwAfterReplacementForTests = 0
$crashAfterCleanupDeletionForTests = 0
$holdPromotionLockMillisecondsForTests = 0
$stopAfterRecoveryForTests = $false

if ($null -eq ('FilePromptAIVolumeIdentity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class FilePromptAIVolumeIdentity
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);
}
'@
}

function Assert-NoReparseAncestor {
    param(
        [string]$Path,
        [string]$Description
    )

    $current = [IO.Path]::GetFullPath($Path)
    $currentRoot = [IO.Path]::GetPathRoot($current)
    if ($current.Length -gt $currentRoot.Length) {
        $current = $current.TrimEnd('\')
    }
    while (-not (Test-Path -LiteralPath $current)) {
        if ([string]::Equals(
                $current,
                $currentRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Description has no existing safe ancestor: $Path"
        }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) {
            throw "$Description has no existing safe ancestor: $Path"
        }
        $current = if ($parent.Length -gt $currentRoot.Length) {
            $parent.TrimEnd('\')
        }
        else {
            $currentRoot
        }
    }
    while (-not [string]::IsNullOrEmpty($current)) {
        $attributes = [IO.File]::GetAttributes($current)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description must not use a reparse-point ancestor: $current"
        }
        if ([string]::Equals(
                $current,
                $currentRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) {
            break
        }
        $current = if ($parent.Length -gt $currentRoot.Length) {
            $parent.TrimEnd('\')
        }
        else {
            $currentRoot
        }
    }
}

function Get-PhysicalVolumeIdentity {
    param([string]$Path)

    $existing = [IO.Path]::GetFullPath($Path)
    while (-not (Test-Path -LiteralPath $existing)) {
        $parent = Split-Path -Parent $existing
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $existing) {
            throw "Unable to find an existing ancestor for volume identity: $Path"
        }
        $existing = $parent
    }
    $mount = New-Object Text.StringBuilder 1024
    if (-not [FilePromptAIVolumeIdentity]::GetVolumePathName(
            $existing,
            $mount,
            $mount.Capacity)) {
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "Unable to resolve the physical volume mount point for $Path (Win32 $code)."
    }
    $volume = New-Object Text.StringBuilder 1024
    if (-not [FilePromptAIVolumeIdentity]::GetVolumeNameForVolumeMountPoint(
            $mount.ToString(),
            $volume,
            $volume.Capacity)) {
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "Unable to resolve the physical volume identity for $Path (Win32 $code)."
    }
    return $volume.ToString().TrimEnd('\')
}

function Get-PromotionFileHash {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-PromotionFileHash {
    param(
        [string]$Path,
        [string]$Expected
    )

    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-PromotionFileHash -Path $Path) -ceq $Expected
}

function ConvertTo-PromotionXmlAttribute {
    param([string]$Value)

    return [Security.SecurityElement]::Escape($Value)
}

function Write-PromotionJournal {
    param(
        [string]$TransactionRoot,
        [string]$TransactionId,
        [object[]]$Items,
        [ValidateSet('prepared', 'installing', 'committed')]
        [string]$Phase,
        [int]$ReplacedCount
    )

    $journalPath = Join-Path $TransactionRoot 'transaction.xml'
    $nextPath = Join-Path $TransactionRoot 'transaction.next'
    $previousPath = Join-Path $TransactionRoot 'transaction.previous'
    $text =
        '<?xml version="1.0" encoding="utf-8"?>' + "`r`n" +
        '<promotionTransaction schemaVersion="1"' +
        ' transactionId="' + (ConvertTo-PromotionXmlAttribute $TransactionId) + '"' +
        ' version="' + (ConvertTo-PromotionXmlAttribute $Version) + '"' +
        ' phase="' + $Phase + '"' +
        ' replacedCount="' + $ReplacedCount.ToString(
            [Globalization.CultureInfo]::InvariantCulture) + '"' +
        ' destinationRoot="' +
            (ConvertTo-PromotionXmlAttribute $DestinationRoot) + '">' + "`r`n"
    for ($index = 0; $index -lt $Items.Count; $index++) {
        $item = $Items[$index]
        $text +=
            '  <item index="' + $index.ToString(
                [Globalization.CultureInfo]::InvariantCulture) + '"' +
            ' name="' + (ConvertTo-PromotionXmlAttribute $item.Name) + '"' +
            ' target="' + (ConvertTo-PromotionXmlAttribute $item.Target) + '"' +
            ' staged="' + (ConvertTo-PromotionXmlAttribute $item.Staged) + '"' +
            ' backup="' + (ConvertTo-PromotionXmlAttribute $item.Backup) + '"' +
            ' discard="' + (ConvertTo-PromotionXmlAttribute $item.Discard) + '"' +
            ' existed="' + $item.Existed.ToString().ToLowerInvariant() + '"' +
            ' oldSha256="' + $item.OldHash + '"' +
            ' newSha256="' + $item.NewHash + '" />' + "`r`n"
    }
    $text += '</promotionTransaction>' + "`r`n"
    $bytes = $utf8NoBom.GetBytes($text)
    $stream = [IO.File]::Open(
        $nextPath,
        [IO.FileMode]::Create,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
    if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
        [IO.File]::Replace($nextPath, $journalPath, $previousPath)
        if (Test-Path -LiteralPath $previousPath -PathType Leaf) {
            Remove-Item -LiteralPath $previousPath -Force
        }
    }
    else {
        [IO.File]::Move($nextPath, $journalPath)
    }
}

function Read-PromotionJournal {
    param([string]$TransactionRoot)

    $journalPath = Join-Path $TransactionRoot 'transaction.xml'
    if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) {
        throw "Promotion recovery journal is missing; evidence was preserved: $TransactionRoot"
    }
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 131072
    $document = New-Object Xml.XmlDocument
    $document.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($journalPath, $settings)
    try {
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
    }
    $root = $document.DocumentElement
    if ($null -eq $root -or $root.Name -cne 'promotionTransaction' -or
        $root.Attributes.Count -ne 6 -or
        $root.GetAttribute('schemaVersion') -cne '1' -or
        $root.GetAttribute('version') -cne $Version -or
        $root.GetAttribute('transactionId') -cnotmatch '^[0-9a-f]{32}$' -or
        $root.GetAttribute('phase') -cnotmatch '^(?:prepared|installing|committed)$' -or
        -not [string]::Equals(
            $root.GetAttribute('destinationRoot'),
            $DestinationRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Promotion recovery journal header is invalid; evidence was preserved: $TransactionRoot"
    }
    $transactionId = $root.GetAttribute('transactionId')
    if ((Split-Path -Leaf $TransactionRoot) -cne $transactionId) {
        throw "Promotion recovery journal directory identity is invalid; evidence was preserved: $TransactionRoot"
    }
    $replacedCount = 0
    if (-not [int]::TryParse(
            $root.GetAttribute('replacedCount'),
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$replacedCount) -or
        $replacedCount -lt 0 -or $replacedCount -gt 1) {
        throw "Promotion recovery journal replacement count is invalid; evidence was preserved: $TransactionRoot"
    }
    $expectedNames = @($archiveName)
    $nodes = @($root.ChildNodes | Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element })
    if ($nodes.Count -ne $expectedNames.Count -or
        $root.ChildNodes.Count -ne $expectedNames.Count) {
        throw "Promotion recovery journal item count is invalid; evidence was preserved: $TransactionRoot"
    }
    $items = @()
    for ($index = 0; $index -lt $expectedNames.Count; $index++) {
        $node = $nodes[$index]
        $name = $expectedNames[$index]
        $target = Join-Path $DestinationRoot $name
        $staged = Join-Path $TransactionRoot "$name.new"
        $backup = Join-Path $TransactionRoot "$name.bak"
        $discard = Join-Path $TransactionRoot "$name.discard"
        $invalidFields = New-Object Collections.Generic.List[string]
        if ($node.LocalName -cne 'item') { $invalidFields.Add('element') }
        if ($node.Attributes.Count -ne 9) { $invalidFields.Add('attribute-count') }
        if ($node.GetAttribute('index') -cne $index.ToString(
                [Globalization.CultureInfo]::InvariantCulture)) {
            $invalidFields.Add('index')
        }
        if ($node.GetAttribute('name') -cne $name) { $invalidFields.Add('name') }
        if (-not [string]::Equals(
                $node.GetAttribute('target'),
                $target,
                [StringComparison]::OrdinalIgnoreCase)) {
            $invalidFields.Add('target')
        }
        if (-not [string]::Equals(
                $node.GetAttribute('staged'),
                $staged,
                [StringComparison]::OrdinalIgnoreCase)) {
            $invalidFields.Add('staged')
        }
        if (-not [string]::Equals(
                $node.GetAttribute('backup'),
                $backup,
                [StringComparison]::OrdinalIgnoreCase)) {
            $invalidFields.Add('backup')
        }
        if (-not [string]::Equals(
                $node.GetAttribute('discard'),
                $discard,
                [StringComparison]::OrdinalIgnoreCase)) {
            $invalidFields.Add('discard')
        }
        if ($node.GetAttribute('existed') -cnotmatch '^(?:true|false)$') {
            $invalidFields.Add('existed')
        }
        if ($node.GetAttribute('oldSha256') -cnotmatch '^(?:NONE|[0-9A-F]{64})$') {
            $invalidFields.Add('oldSha256')
        }
        if ($node.GetAttribute('newSha256') -cnotmatch '^[0-9A-F]{64}$') {
            $invalidFields.Add('newSha256')
        }
        if ($invalidFields.Count -ne 0) {
            throw "Promotion recovery journal item $index is invalid ($($invalidFields -join ', ')); evidence was preserved: $TransactionRoot"
        }
        $existed = $node.GetAttribute('existed') -ceq 'true'
        if (($existed -and $node.GetAttribute('oldSha256') -ceq 'NONE') -or
            (-not $existed -and $node.GetAttribute('oldSha256') -cne 'NONE')) {
            throw "Promotion recovery journal old-state identity is invalid; evidence was preserved: $TransactionRoot"
        }
        $items += [pscustomobject]@{
            Name = $name
            Target = $target
            Staged = $staged
            Backup = $backup
            Discard = $discard
            Existed = $existed
            OldHash = $node.GetAttribute('oldSha256')
            NewHash = $node.GetAttribute('newSha256')
        }
    }
    return [pscustomobject]@{
        Phase = $root.GetAttribute('phase')
        ReplacedCount = $replacedCount
        Items = $items
    }
}

function Remove-PromotionTransactionDirectory {
    param([string]$TransactionRoot)

    Assert-NoReparseAncestor -Path $TransactionRoot -Description 'Promotion transaction directory'
    $journalPath = Join-Path $TransactionRoot 'transaction.xml'
    $deletedCount = 0
    foreach ($entry in @(
            Get-ChildItem -LiteralPath $TransactionRoot -Force |
                Sort-Object FullName)) {
        if ($entry.PSIsContainer -or
            (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Promotion transaction cleanup found an unsafe entry: $($entry.FullName)"
        }
        if (-not [string]::Equals(
                $entry.FullName,
                $journalPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $entry.FullName -Force
            $deletedCount++
            if ($crashAfterCleanupDeletionForTests -eq $deletedCount) {
                $crashMarker = Join-Path $SourceRoot (
                    'tests\build-artifacts\promotion-cleanup-crash-once-' +
                    $deletedCount.ToString(
                        [Globalization.CultureInfo]::InvariantCulture) +
                    '.txt')
                if (-not (Test-Path -LiteralPath $crashMarker -PathType Leaf)) {
                    [IO.File]::WriteAllText(
                        $crashMarker,
                        $TransactionRoot,
                        $utf8NoBom)
                    [Environment]::Exit(198)
                }
            }
        }
    }
    if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
        Remove-Item -LiteralPath $journalPath -Force
    }
    Remove-Item -LiteralPath $TransactionRoot -Force
}

function Restore-PromotionTransaction {
    param(
        [string]$TransactionRoot,
        [object]$Journal,
        [switch]$PreferRollback
    )

    $allowedArtifacts = New-Object `
        'System.Collections.Generic.Dictionary[string,bool]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @(
            (Join-Path $TransactionRoot 'transaction.xml'),
            (Join-Path $TransactionRoot 'transaction.next'),
            (Join-Path $TransactionRoot 'transaction.previous'))) {
        $allowedArtifacts[[IO.Path]::GetFullPath($path)] = $true
    }
    foreach ($item in $Journal.Items) {
        foreach ($path in @($item.Staged, $item.Backup, $item.Discard)) {
            $allowedArtifacts[[IO.Path]::GetFullPath($path)] = $true
        }
    }
    foreach ($entry in @(Get-ChildItem -LiteralPath $TransactionRoot -Force)) {
        if ($entry.PSIsContainer -or
            (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
            -not $allowedArtifacts.ContainsKey(
                [IO.Path]::GetFullPath($entry.FullName))) {
            throw "Promotion recovery found an unexpected transaction artifact; evidence was preserved: $($entry.FullName)"
        }
    }

    $allNew = $true
    $targetStates = @{}
    foreach ($item in $Journal.Items) {
        foreach ($artifact in @($item.Target, $item.Staged, $item.Backup, $item.Discard)) {
            if (Test-Path -LiteralPath $artifact) {
                $attributes = [IO.File]::GetAttributes($artifact)
                if (($attributes -band [IO.FileAttributes]::Directory) -ne 0 -or
                    ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Promotion recovery found an unsafe artifact; evidence was preserved: $artifact"
                }
            }
        }
        if (Test-Path -LiteralPath $item.Staged -PathType Leaf) {
            if (-not (Test-PromotionFileHash `
                    -Path $item.Staged `
                    -Expected $item.NewHash)) {
                throw "Promotion recovery staged file is invalid; evidence was preserved: $($item.Staged)"
            }
        }
        if (Test-Path -LiteralPath $item.Backup -PathType Leaf) {
            if (-not $item.Existed -or
                -not (Test-PromotionFileHash `
                    -Path $item.Backup `
                    -Expected $item.OldHash)) {
                throw "Promotion recovery backup is invalid; evidence was preserved: $($item.Backup)"
            }
        }
        if (Test-Path -LiteralPath $item.Discard -PathType Leaf) {
            if (-not (Test-PromotionFileHash `
                    -Path $item.Discard `
                    -Expected $item.NewHash)) {
                throw "Promotion recovery discard file is invalid; evidence was preserved: $($item.Discard)"
            }
        }
        $targetIsNew = Test-PromotionFileHash -Path $item.Target -Expected $item.NewHash
        $targetIsOld = $item.Existed -and
            (Test-PromotionFileHash -Path $item.Target -Expected $item.OldHash)
        $targetMissing = -not (Test-Path -LiteralPath $item.Target)
        if (-not $targetIsNew) {
            $allNew = $false
        }
        if (-not $targetIsNew -and -not $targetIsOld -and -not $targetMissing) {
            throw "Promotion recovery target has unknown bytes; evidence was preserved: $($item.Target)"
        }
        $targetStates[$item.Name] = if ($targetIsNew) {
            'new'
        }
        elseif ($targetIsOld) {
            'old'
        }
        else {
            'missing'
        }
    }
    if ($allNew -and
        -not $PreferRollback -and
        $Journal.Phase -ceq 'committed' -and
        $Journal.ReplacedCount -eq $Journal.Items.Count) {
        Remove-PromotionTransactionDirectory -TransactionRoot $TransactionRoot
        Write-Host "PROMOTION RECOVERED COMMITTED | cleaned=$TransactionRoot"
        return 'committed'
    }

    # Complete the entire rollback preflight before modifying any target. A
    # target that is already old no longer needs its consumed backup; every
    # other originally present target must still have the exact old bytes.
    foreach ($item in $Journal.Items) {
        $state = $targetStates[$item.Name]
        if ($item.Existed -and $state -cne 'old' -and
            -not (Test-PromotionFileHash `
                -Path $item.Backup `
                -Expected $item.OldHash)) {
            throw "Promotion recovery backup is missing or invalid; evidence was preserved: $($item.Backup)"
        }
    }
    for ($index = $Journal.Items.Count - 1; $index -ge 0; $index--) {
        $item = $Journal.Items[$index]
        if ($item.Existed) {
            if (-not (Test-PromotionFileHash -Path $item.Target -Expected $item.OldHash)) {
                if (Test-Path -LiteralPath $item.Target -PathType Leaf) {
                    [IO.File]::Replace($item.Backup, $item.Target, $item.Discard)
                }
                else {
                    [IO.File]::Move($item.Backup, $item.Target)
                }
            }
            if (-not (Test-PromotionFileHash -Path $item.Target -Expected $item.OldHash)) {
                throw "Promotion recovery failed to restore original bytes; evidence was preserved: $($item.Target)"
            }
        }
        elseif (Test-Path -LiteralPath $item.Target -PathType Leaf) {
            Remove-Item -LiteralPath $item.Target -Force
        }
    }
    foreach ($item in $Journal.Items) {
        if ($item.Existed) {
            if (-not (Test-PromotionFileHash `
                    -Path $item.Target `
                    -Expected $item.OldHash)) {
                throw "Promotion recovery did not restore the complete original snapshot; evidence was preserved: $($item.Target)"
            }
        }
        elseif (Test-Path -LiteralPath $item.Target) {
            throw "Promotion recovery did not restore an originally absent target; evidence was preserved: $($item.Target)"
        }
    }
    Remove-PromotionTransactionDirectory -TransactionRoot $TransactionRoot
    Write-Host "PROMOTION RECOVERED ROLLED BACK | restored=$DestinationRoot"
    return 'rolledback'
}

function Invoke-PromotionStartupRecovery {
    param([string]$TransactionBase)

    if (-not (Test-Path -LiteralPath $TransactionBase)) {
        return $false
    }
    Assert-NoReparseAncestor -Path $TransactionBase -Description 'Promotion transaction base'
    if (-not (Test-Path -LiteralPath $TransactionBase -PathType Container)) {
        throw "Promotion transaction base is not a directory: $TransactionBase"
    }
    $entries = @(Get-ChildItem -LiteralPath $TransactionBase -Force)
    if ($entries.Count -gt 1) {
        throw "Multiple promotion recovery transactions require manual inspection; evidence was preserved: $TransactionBase"
    }
    if ($entries.Count -eq 0) {
        return $false
    }
    $entry = $entries[0]
    if (-not $entry.PSIsContainer -or $entry.Name -cnotmatch '^[0-9a-f]{32}$' -or
        (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Promotion transaction base contains an unsafe entry; evidence was preserved: $($entry.FullName)"
    }
    $transactionEntries = @(
        Get-ChildItem -LiteralPath $entry.FullName -Force)
    if ($transactionEntries.Count -eq 0) {
        Remove-Item -LiteralPath $entry.FullName -Force
        Write-Host "PROMOTION RECOVERED CLEANUP | removed empty transaction=$($entry.FullName)"
        return $true
    }
    $journalPath = Join-Path $entry.FullName 'transaction.xml'
    if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) {
        $previousJournalPath = Join-Path $entry.FullName 'transaction.previous'
        $nextJournalPath = Join-Path $entry.FullName 'transaction.next'
        $fallbackJournalPath = if (Test-Path `
                -LiteralPath $previousJournalPath `
                -PathType Leaf) {
            $previousJournalPath
        }
        elseif (Test-Path -LiteralPath $nextJournalPath -PathType Leaf) {
            $nextJournalPath
        }
        else {
            $null
        }
        if ($null -ne $fallbackJournalPath) {
            # File.Replace can leave its durable previous/next journal as the
            # only recovery record after a power or file-system failure.
            [IO.File]::Move($fallbackJournalPath, $journalPath)
            $journal = Read-PromotionJournal -TransactionRoot $entry.FullName
            [void](Restore-PromotionTransaction `
                -TransactionRoot $entry.FullName `
                -Journal $journal)
            return $true
        }

        $safePreJournalNames = New-Object `
            'System.Collections.Generic.Dictionary[string,bool]' `
            ([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in @("$archiveName.new")) {
            $safePreJournalNames[$name] = $true
        }
        foreach ($artifact in $transactionEntries) {
            if ($artifact.PSIsContainer -or
                (($artifact.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
                -not $safePreJournalNames.ContainsKey($artifact.Name)) {
                throw "Promotion transaction without a durable journal contains unsafe evidence; manual inspection is required: $($artifact.FullName)"
            }
        }
        Remove-PromotionTransactionDirectory -TransactionRoot $entry.FullName
        Write-Host "PROMOTION RECOVERED PRE-JOURNAL CLEANUP | removed staging-only transaction=$($entry.FullName)"
        return $true
    }
    $journal = Read-PromotionJournal -TransactionRoot $entry.FullName
    [void](Restore-PromotionTransaction `
        -TransactionRoot $entry.FullName `
        -Journal $journal)
    return $true
}

function Assert-DeliveryInventory {
    param(
        [switch]$RequireComplete,
        [string[]]$AllowedTransientNames = @()
    )

    $allowedNames = New-Object `
        'System.Collections.Generic.Dictionary[string,bool]' `
        ([StringComparer]::Ordinal)
    foreach ($name in @($archiveName)) {
        $allowedNames[$name] = $true
    }
    $transientNames = New-Object `
        'System.Collections.Generic.Dictionary[string,bool]' `
        ([StringComparer]::Ordinal)
    foreach ($name in @($AllowedTransientNames)) {
        if (-not [string]::IsNullOrEmpty($name)) {
            $transientNames[$name] = $true
        }
    }

    $actualNames = New-Object Collections.Generic.List[string]
    foreach ($entry in @([IO.Directory]::GetFileSystemEntries(
            $DestinationRoot))) {
        $name = [IO.Path]::GetFileName($entry)
        $isDeliveryFile = $allowedNames.ContainsKey($name)
        if (-not $isDeliveryFile -and -not $transientNames.ContainsKey($name)) {
            throw "The candidate delivery directory contains an obsolete or unauthorized entry: $name`nRemove old delivery assets in source candidate commit C before promotion."
        }
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            throw "The candidate delivery entry must be a file: $name"
        }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The candidate delivery entry must not be a reparse point: $name"
        }
        if ($isDeliveryFile) {
            [void]$actualNames.Add($name)
        }
    }

    if ($RequireComplete -and $actualNames.Count -ne $allowedNames.Count) {
        $missing = @($allowedNames.Keys | Where-Object {
            -not (Test-Path -LiteralPath (
                Join-Path $DestinationRoot $_) -PathType Leaf)
        })
        throw "The promoted candidate delivery inventory is incomplete: $($missing -join ', ')"
    }
}

function Read-CanonicalReceipt {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The successful release-candidate receipt is missing: $Path"
    }
    if ((Get-Item -LiteralPath $Path).Length -gt 16384) {
        throw 'The release-candidate receipt exceeds the 16 KB safety limit.'
    }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        throw 'The release-candidate receipt must be UTF-8 without BOM.'
    }
    $text = $strictUtf8.GetString($bytes)
    $pattern = '\A' +
        'FilePromptAI-Release-Receipt: 2\r\n' +
        'Suite: tests/RunAllSmokeTests\.ps1\r\n' +
        'Result: PASS\r\n' +
        'Version: (?<Version>[0-9A-Za-z](?:[0-9A-Za-z._-]{0,30}[0-9A-Za-z])?)\r\n' +
        'Candidate-Commit: (?<Candidate>[0-9a-f]{40}(?:[0-9a-f]{24})?)\r\n' +
        'Archive-Name: (?<Archive>[0-9A-Za-z._-]+)\r\n' +
        'Archive-SHA256: (?<Hash>[0-9A-F]{64})\r\n' +
        'Package-Manifest-Name: PACKAGE-CHECKSUMS-SHA256\.txt\r\n' +
        'Package-Manifest-SHA256: (?<ManifestHash>[0-9A-F]{64})\r\n' +
        'Package-Manifest-Entry-Count: (?<ManifestCount>[1-9][0-9]{0,8})\r\n\z'
    $match = [Text.RegularExpressions.Regex]::Match(
        $text,
        $pattern,
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw 'The release-candidate receipt has an invalid or non-canonical format.'
    }
    $manifestCount = 0
    if (-not [int]::TryParse(
            $match.Groups['ManifestCount'].Value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$manifestCount)) {
        throw 'The release-candidate receipt has an invalid manifest count.'
    }
    return [pscustomobject]@{
        Bytes = $bytes
        Version = $match.Groups['Version'].Value
        Candidate = $match.Groups['Candidate'].Value
        Archive = $match.Groups['Archive'].Value
        Hash = $match.Groups['Hash'].Value
        ManifestHash = $match.Groups['ManifestHash'].Value
        ManifestCount = $manifestCount
    }
}

function Write-AtomicBytes {
    param(
        [string]$Path,
        [byte[]]$Bytes
    )

    $temporary = Join-Path (Split-Path -Parent $Path) (
        '.' + (Split-Path -Leaf $Path) + '.' +
        [Guid]::NewGuid().ToString('N') + '.tmp')
    $backup = "$temporary.bak"
    try {
        [IO.File]::WriteAllBytes($temporary, $Bytes)
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [IO.File]::Replace($temporary, $Path, $backup)
            Remove-Item -LiteralPath $backup -Force
        }
        else {
            [IO.File]::Move($temporary, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            Remove-Item -LiteralPath $backup -Force
        }
    }
}

function Copy-AtomicFile {
    param(
        [string]$Source,
        [string]$Destination
    )

    $temporary = Join-Path (Split-Path -Parent $Destination) (
        '.' + (Split-Path -Leaf $Destination) + '.' +
        [Guid]::NewGuid().ToString('N') + '.tmp')
    $backup = "$temporary.bak"
    $input = $null
    $output = $null
    try {
        $input = [IO.File]::Open(
            $Source,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $output = [IO.File]::Open(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $input.CopyTo($output, 1048576)
        $output.Flush($true)
        $output.Dispose()
        $output = $null
        $input.Dispose()
        $input = $null
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            [IO.File]::Replace($temporary, $Destination, $backup)
            Remove-Item -LiteralPath $backup -Force
        }
        else {
            [IO.File]::Move($temporary, $Destination)
        }
    }
    finally {
        if ($null -ne $output) {
            $output.Dispose()
        }
        if ($null -ne $input) {
            $input.Dispose()
        }
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            Remove-Item -LiteralPath $backup -Force
        }
    }
}

function Install-PromotionTransaction {
    param(
        [object[]]$Items,
        [scriptblock]$Validate,
        [string]$TransactionRoot,
        [string]$TransactionId
    )

    $journalWritten = $false
    $replacementCount = 0
    $transactionComplete = $false
    $rollbackComplete = $false
    $preserveTransaction = $false
    try {
        foreach ($item in $Items) {
            if (Test-Path -LiteralPath $item.Target -PathType Container) {
                throw "A candidate delivery target is a directory: $($item.Target)"
            }
            if ($item.Existed) {
                if (-not (Test-PromotionFileHash `
                        -Path $item.Target `
                        -Expected $item.OldHash)) {
                    throw "A candidate delivery target changed before promotion: $($item.Target)"
                }
            }
            elseif (Test-Path -LiteralPath $item.Target) {
                throw "A candidate delivery target appeared before promotion: $($item.Target)"
            }
            if (-not (Test-PromotionFileHash `
                    -Path $item.Staged `
                    -Expected $item.NewHash)) {
                throw "A staged candidate file changed before promotion: $($item.Staged)"
            }
        }

        Write-PromotionJournal `
            -TransactionRoot $TransactionRoot `
            -TransactionId $TransactionId `
            -Items $Items `
            -Phase 'prepared' `
            -ReplacedCount 0
        $journalWritten = $true

        foreach ($item in $Items) {
            if ($item.Existed) {
                [IO.File]::Replace($item.Staged, $item.Target, $item.Backup)
            }
            else {
                [IO.File]::Move($item.Staged, $item.Target)
            }
            $replacementCount++
            if ($throwAfterReplacementForTests -eq $replacementCount) {
                throw "injected failure after replacement $replacementCount before journal update"
            }
            if ($crashAfterReplacementForTests -eq $replacementCount) {
                $crashMarker = Join-Path $SourceRoot (
                    'tests\build-artifacts\promotion-crash-once.txt')
                if (-not (Test-Path -LiteralPath $crashMarker -PathType Leaf)) {
                    [IO.File]::WriteAllText(
                        $crashMarker,
                        $replacementCount.ToString(
                            [Globalization.CultureInfo]::InvariantCulture),
                        $utf8NoBom)
                    [Environment]::Exit(197)
                }
            }
            Write-PromotionJournal `
                -TransactionRoot $TransactionRoot `
                -TransactionId $TransactionId `
                -Items $Items `
                -Phase 'installing' `
                -ReplacedCount $replacementCount
        }
        & $Validate
        Write-PromotionJournal `
            -TransactionRoot $TransactionRoot `
            -TransactionId $TransactionId `
            -Items $Items `
            -Phase 'committed' `
            -ReplacedCount $Items.Count
        $transactionComplete = $true
    }
    catch {
        $installError = $_
        if (-not $journalWritten) {
            try {
                if (Test-Path -LiteralPath $TransactionRoot -PathType Container) {
                    Remove-PromotionTransactionDirectory `
                        -TransactionRoot $TransactionRoot
                }
            }
            catch {
                throw "Candidate promotion failed before its recovery journal was committed, and temporary cleanup also failed. No delivery target was changed. Temporary files were preserved in: $TransactionRoot`nPromotion error: $($installError.Exception.Message)`nCleanup error: $($_.Exception.Message)"
            }
            throw $installError
        }

        try {
            $journal = Read-PromotionJournal -TransactionRoot $TransactionRoot
            [void](Restore-PromotionTransaction `
                -TransactionRoot $TransactionRoot `
                -Journal $journal `
                -PreferRollback)
            $rollbackComplete = $true
        }
        catch {
            $preserveTransaction = $true
            throw "Candidate promotion failed and rollback was incomplete. " +
                "Recovery files were preserved in: $TransactionRoot`n" +
                "Install error: " +
                "$($installError.Exception.Message)`nRollback error: " +
                "$($_.Exception.Message)"
        }
        throw $installError
    }
    finally {
        if (-not $preserveTransaction -and
            ($transactionComplete -or $rollbackComplete)) {
            try {
                if ($transactionComplete -and
                    $postCommitCleanupFailureForTests) {
                    throw 'injected post-commit cleanup failure'
                }
                if (Test-Path -LiteralPath $TransactionRoot -PathType Container) {
                    Remove-PromotionTransactionDirectory `
                        -TransactionRoot $TransactionRoot
                }
            }
            catch {
                $state = if ($transactionComplete) {
                    'PROMOTION COMMITTED'
                }
                else {
                    'PROMOTION ROLLED BACK'
                }
                [Console]::Error.WriteLine(
                    "$state cleanup warning | temporary cleanup is incomplete, " +
                    "but delivery state is unambiguous. Cleanup path: " +
                    "$TransactionRoot. Error: $($_.Exception.Message)")
            }
        }
    }
}

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "The source project directory is missing: $SourceRoot"
}
if (Test-Path -LiteralPath $DestinationRoot) {
    if (-not (Test-Path -LiteralPath $DestinationRoot -PathType Container)) {
        throw "The candidate delivery path is not a directory: $DestinationRoot"
    }
}
else {
    Assert-NoReparseAncestor `
        -Path $DestinationRoot `
        -Description 'Candidate delivery directory'
    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
}
Assert-NoReparseAncestor -Path $SourceRoot -Description 'Source project directory'
Assert-NoReparseAncestor -Path $DestinationRoot -Description 'Candidate delivery directory'
$buildArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $SourceRoot (
    'tests\build-artifacts'))).TrimEnd('\')
Assert-NoReparseAncestor `
    -Path $buildArtifactsRoot `
    -Description 'Promotion build-artifacts directory'
if (-not (Test-Path -LiteralPath $buildArtifactsRoot)) {
    New-Item -ItemType Directory -Path $buildArtifactsRoot -Force | Out-Null
}
Assert-NoReparseAncestor `
    -Path $buildArtifactsRoot `
    -Description 'Promotion build-artifacts directory'
if (-not (Test-Path -LiteralPath $buildArtifactsRoot -PathType Container)) {
    throw "Promotion build-artifacts path is not a directory: $buildArtifactsRoot"
}
$lockPath = Join-Path $buildArtifactsRoot 'promotion.lock'
if (Test-Path -LiteralPath $lockPath) {
    $lockAttributes = [IO.File]::GetAttributes($lockPath)
    if (($lockAttributes -band [IO.FileAttributes]::Directory) -ne 0 -or
        ($lockAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Promotion lock path is unsafe: $lockPath"
    }
}
$promotionLock = $null
try {
    try {
        $promotionLock = [IO.File]::Open(
            $lockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    catch {
        throw "Another candidate promotion is already running, or the promotion lock cannot be acquired: $lockPath`n$($_.Exception.Message)"
    }
    $lockAttributes = [IO.File]::GetAttributes($lockPath)
    if (($lockAttributes -band [IO.FileAttributes]::Directory) -ne 0 -or
        ($lockAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Promotion lock path changed identity after acquisition: $lockPath"
    }
    if ($holdPromotionLockMillisecondsForTests -gt 0) {
        Start-Sleep -Milliseconds $holdPromotionLockMillisecondsForTests
    }

$transactionBase = [IO.Path]::GetFullPath((Join-Path $SourceRoot (
    'tests\build-artifacts\promotion'))).TrimEnd('\')
Assert-NoReparseAncestor -Path $transactionBase -Description 'Promotion transaction base'
$sourceVolume = Get-PhysicalVolumeIdentity -Path $SourceRoot
$destinationVolume = Get-PhysicalVolumeIdentity -Path $DestinationRoot
$transactionVolume = Get-PhysicalVolumeIdentity -Path $transactionBase
if (-not [string]::Equals(
        $sourceVolume,
        $destinationVolume,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals(
        $transactionVolume,
        $destinationVolume,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source, delivery, and promotion transaction paths must resolve to the same physical volume.'
}
$recoveredPromotion = Invoke-PromotionStartupRecovery `
    -TransactionBase $transactionBase
if ($recoveredPromotion -and $stopAfterRecoveryForTests) {
    Write-Host 'PROMOTION RECOVERY TEST STOP | delivery restored before promotion gates.'
    exit 0
}

foreach ($required in @(
        $sourceArchive,
        $receiptPath,
        $evidenceHelper,
        $installedJourneyScript,
        $windowsPowerShell)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required tested-candidate input is missing: $required"
    }
}
Assert-DeliveryInventory

. $evidenceHelper
$receipt = Read-CanonicalReceipt -Path $receiptPath
if ($receipt.Version -cne $Version -or $receipt.Archive -cne $archiveName) {
    throw 'The release-candidate receipt is for a different version or archive.'
}

$headCommit = (& git -C $SourceRoot rev-parse --verify HEAD 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    -not [string]::Equals(
        $headCommit,
        $receipt.Candidate,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The tested candidate receipt does not match HEAD: receipt=$($receipt.Candidate); HEAD=$headCommit"
}
$statusLines = @(& git -C $SourceRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the tested candidate working tree.'
}
if ($statusLines.Count -ne 0) {
    throw "Candidate promotion requires a clean tested commit.`n$($statusLines -join "`n")"
}

$gitRoot = (& git -C $SourceRoot rev-parse --show-toplevel 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Candidate promotion requires a Git worktree.'
}
$lfsVersion = (& git -C $gitRoot lfs version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $lfsVersion -notmatch '^git-lfs/') {
    throw 'Candidate promotion requires Git LFS.'
}
$gitRootPrefix = [IO.Path]::GetFullPath($gitRoot).TrimEnd('\') + '\'
$receiptFullPath = [IO.Path]::GetFullPath($receiptPath)
if (-not $receiptFullPath.StartsWith(
        $gitRootPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release-candidate receipt must be inside the tested Git worktree.'
}
$gitReceipt = $receiptFullPath.Substring(
    $gitRootPrefix.Length).Replace('\', '/')
& git -C $gitRoot check-ignore -q -- $gitReceipt
if ($LASTEXITCODE -ne 0) {
    throw 'The local release-candidate receipt must remain ignored by Git.'
}
$destinationFullPath = [IO.Path]::GetFullPath($destinationArchive)
if (-not $destinationFullPath.StartsWith(
        $gitRootPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The candidate delivery directory must be inside the tested Git worktree.'
}
$gitDestination = $destinationFullPath.Substring(
    $gitRootPrefix.Length).Replace('\', '/')
$lfsFilter = (& git -C $gitRoot check-attr filter -- $gitDestination 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    -not $lfsFilter.EndsWith(': filter: lfs', [StringComparison]::Ordinal)) {
    throw 'The promoted ZIP path must be tracked through Git LFS.'
}
foreach ($trackablePath in @($destinationArchive)) {
    $gitPath = [IO.Path]::GetFullPath($trackablePath).Substring(
        $gitRootPrefix.Length).Replace('\', '/')
    & git -C $gitRoot check-ignore -q -- $gitPath
    if ($LASTEXITCODE -eq 0) {
        throw "The promoted candidate evidence path must be trackable: $gitPath"
    }
    if ($LASTEXITCODE -ne 1) {
        throw "Unable to check whether the promoted candidate path is ignored: $gitPath"
    }
}

$sourceIdentity = Read-FilePromptReleaseArchiveIdentity `
    -ArchivePath $sourceArchive
if ($sourceIdentity.ArchiveSha256 -cne $receipt.Hash -or
    $sourceIdentity.ManifestSha256 -cne $receipt.ManifestHash -or
    $sourceIdentity.ManifestEntryCount -ne $receipt.ManifestCount) {
    throw 'The source ZIP identity does not match the successful test receipt.'
}
$archiveSize = (Get-Item -LiteralPath $sourceArchive).Length

$transactionId = [Guid]::NewGuid().ToString('N')
$transactionRoot = [IO.Path]::GetFullPath((Join-Path (
    $transactionBase) $transactionId)).TrimEnd('\')
$transactionPrefix = $transactionBase + '\'
if (-not $transactionRoot.StartsWith(
        $transactionPrefix,
        [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $transactionRoot) -cnotmatch '^[0-9a-f]{32}$' -or
    -not [string]::Equals(
        [IO.Path]::GetPathRoot($transactionRoot),
        [IO.Path]::GetPathRoot($DestinationRoot),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The promotion transaction directory is unsafe or not on the delivery volume.'
}
New-Item -ItemType Directory -Path $transactionRoot -Force | Out-Null
$items = @(
    [pscustomobject]@{
        Name = $archiveName
        Target = $destinationArchive
        Staged = Join-Path $transactionRoot "$archiveName.new"
        Backup = Join-Path $transactionRoot "$archiveName.bak"
        Discard = Join-Path $transactionRoot "$archiveName.discard"
        Existed = $false
        OldHash = 'NONE'
        NewHash = ''
    }
)
$transactionStarted = $false
try {
    Copy-AtomicFile -Source $sourceArchive -Destination $items[0].Staged

    $sourceIdentityAfterCopy = Read-FilePromptReleaseArchiveIdentity `
        -ArchivePath $sourceArchive
    $stagedIdentity = Read-FilePromptReleaseArchiveIdentity `
        -ArchivePath $items[0].Staged
    if ($sourceIdentityAfterCopy.ArchiveSha256 -cne $receipt.Hash -or
        $sourceIdentityAfterCopy.ManifestSha256 -cne $receipt.ManifestHash -or
        $sourceIdentityAfterCopy.ManifestEntryCount -ne $receipt.ManifestCount -or
        (Get-Item -LiteralPath $sourceArchive).Length -ne $archiveSize -or
        $stagedIdentity.ArchiveSha256 -cne $receipt.Hash -or
        $stagedIdentity.ManifestSha256 -cne $receipt.ManifestHash -or
        $stagedIdentity.ManifestEntryCount -ne $receipt.ManifestCount -or
        (Get-Item -LiteralPath $items[0].Staged).Length -ne $archiveSize) {
        throw 'The staged ZIP differs from the successfully tested source ZIP.'
    }
    foreach ($item in $items) {
        $item.Existed = Test-Path -LiteralPath $item.Target -PathType Leaf
        $item.OldHash = if ($item.Existed) {
            Get-PromotionFileHash -Path $item.Target
        }
        else {
            'NONE'
        }
        $item.NewHash = Get-PromotionFileHash -Path $item.Staged
    }

    $validateInstalled = {
        $destinationIdentity = Read-FilePromptReleaseArchiveIdentity `
            -ArchivePath $destinationArchive
        if ($destinationIdentity.ArchiveSha256 -cne $receipt.Hash -or
            $destinationIdentity.ManifestSha256 -cne $receipt.ManifestHash -or
            $destinationIdentity.ManifestEntryCount -ne $receipt.ManifestCount -or
            (Get-Item -LiteralPath $destinationArchive).Length -ne $archiveSize) {
            throw 'The promoted ZIP differs from the successfully tested source ZIP.'
        }
        Assert-DeliveryInventory -RequireComplete

        $journeyOutput = @(& $windowsPowerShell `
            -NoLogo `
            -NoProfile `
            -NonInteractive `
            -ExecutionPolicy Bypass `
            -File $installedJourneyScript `
            -Version $Version `
            -ArchivePath $destinationArchive 2>&1)
        $journeyExitCode = $LASTEXITCODE
        foreach ($line in $journeyOutput) {
            Write-Host $line.ToString()
        }
        $expectedJourneyPass =
            "PASS | final ZIP installed user journey | archive=$destinationArchive"
        $journeyPassed = @($journeyOutput | Where-Object {
            $_.ToString() -ceq $expectedJourneyPass
        }).Count -eq 1
        if ($journeyExitCode -ne 0 -or -not $journeyPassed) {
            throw "The promoted ZIP failed its final installed user journey (exit=$journeyExitCode; passMarker=$journeyPassed)."
        }
    }
    $transactionStarted = $true
    Install-PromotionTransaction `
        -Items $items `
        -Validate $validateInstalled `
        -TransactionRoot $transactionRoot `
        -TransactionId $transactionId
    Assert-DeliveryInventory -RequireComplete
}
finally {
    if (-not $transactionStarted -and
        (Test-Path -LiteralPath $transactionRoot -PathType Container)) {
        try {
            Remove-Item -LiteralPath $transactionRoot -Recurse -Force
        }
        catch {
            [Console]::Error.WriteLine(
                "PROMOTION NOT STARTED cleanup warning | temporary cleanup " +
                "is incomplete. Cleanup path: $transactionRoot. Error: " +
                "$($_.Exception.Message)")
        }
    }
}

Write-Host "PROMOTED | $destinationArchive | candidate=$($receipt.Candidate) | sha256=$($receipt.Hash) | bytes=$archiveSize | manifestSha256=$($receipt.ManifestHash) | manifestEntries=$($receipt.ManifestCount)"
Write-Host 'CANDIDATE ONLY | Windows 7 acceptance and release sealing are still required.'
}
finally {
    if ($null -ne $promotionLock) {
        $promotionLock.Dispose()
    }
}
