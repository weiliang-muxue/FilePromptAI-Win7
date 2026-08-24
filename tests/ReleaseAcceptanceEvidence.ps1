Set-StrictMode -Version 2.0

function Get-FilePromptSha256Hex {
    param([byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($Bytes))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-FilePromptStreamSha256Hex {
    param([IO.Stream]$Stream)

    if ($null -eq $Stream -or -not $Stream.CanRead -or -not $Stream.CanSeek) {
        throw 'A readable, seekable stream is required for SHA-256.'
    }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $Stream.Position = 0
        $hash = ([BitConverter]::ToString(
            $algorithm.ComputeHash($Stream))).Replace('-', '')
        $Stream.Position = 0
        return $hash
    }
    finally {
        $algorithm.Dispose()
    }
}

function Read-FilePromptLockedBytes {
    param(
        [string]$Path,
        [int64]$MaximumLength,
        [string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Description path is required."
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Description is missing: $fullPath"
    }
    $stream = [IO.File]::Open(
        $fullPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt $MaximumLength) {
            throw "$Description has an invalid size: $($stream.Length) bytes."
        }
        $bytes = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) {
                throw "$Description ended unexpectedly while being read."
            }
            $offset += $read
        }
        return ,$bytes
    }
    finally {
        $stream.Dispose()
    }
}

function Test-FilePromptCanonicalRelativePath {
    param([string]$Path)

    if ([string]::IsNullOrEmpty($Path) -or
        [IO.Path]::IsPathRooted($Path) -or
        $Path.IndexOf(':') -ge 0 -or
        $Path.IndexOf('/') -ge 0 -or
        $Path[0] -eq '\' -or
        $Path[$Path.Length - 1] -eq '\') {
        return $false
    }
    foreach ($segment in $Path.Split('\')) {
        if ($segment.Length -eq 0 -or
            $segment -eq '.' -or
            $segment -eq '..' -or
            $segment.TrimEnd(' ', '.') -cne $segment) {
            return $false
        }
    }
    return $true
}

function Get-FilePromptPackageManifestIdentity {
    param([byte[]]$Bytes)

    if ($null -eq $Bytes -or $Bytes.Length -le 0 -or $Bytes.Length -gt 4194304) {
        throw 'The package checksum manifest has an invalid size.'
    }
    if ($Bytes.Length -ge 3 -and
        $Bytes[0] -eq 0xEF -and
        $Bytes[1] -eq 0xBB -and
        $Bytes[2] -eq 0xBF) {
        throw 'The package checksum manifest must be UTF-8 without BOM.'
    }
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    try {
        $text = $strictUtf8.GetString($Bytes)
    }
    catch {
        throw 'The package checksum manifest is not valid UTF-8.'
    }
    if ($text -notmatch '\A(?:[^\r\n]+\r\n)+\z') {
        throw 'The package checksum manifest must use canonical CRLF lines.'
    }
    $lines = @($text.Substring(0, $text.Length - 2).Split("`n"))
    $seenPaths = @{}
    foreach ($rawLine in $lines) {
        $line = $rawLine.TrimEnd("`r")
        $match = [Text.RegularExpressions.Regex]::Match(
            $line,
            '\A(?<Hash>[0-9A-F]{64}) \*(?<Path>.+)\z',
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $match.Success) {
            throw 'The package checksum manifest contains a non-canonical line.'
        }
        $relativePath = $match.Groups['Path'].Value
        if (-not (Test-FilePromptCanonicalRelativePath -Path $relativePath) -or
            $seenPaths.ContainsKey($relativePath)) {
            throw "The package checksum manifest contains an unsafe or duplicate path: $relativePath"
        }
        $seenPaths[$relativePath] = $true
    }
    if ($seenPaths.Count -le 0) {
        throw 'The package checksum manifest is empty.'
    }
    return [pscustomobject]@{
        Sha256 = Get-FilePromptSha256Hex -Bytes $Bytes
        EntryCount = $seenPaths.Count
    }
}

function Read-FilePromptPackageManifestIdentity {
    param([string]$Path)

    $bytes = Read-FilePromptLockedBytes `
        -Path $Path `
        -MaximumLength 4194304 `
        -Description 'The package checksum manifest'
    return Get-FilePromptPackageManifestIdentity -Bytes $bytes
}

function Read-FilePromptReleaseArchiveIdentity {
    param([string]$ArchivePath)

    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        throw "The final release ZIP is missing: $ArchivePath"
    }
    Add-Type -AssemblyName System.IO.Compression
    $archiveStream = [IO.File]::Open(
        [IO.Path]::GetFullPath($ArchivePath),
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $archive = $null
    try {
        if ($archiveStream.Length -le 0) {
            throw 'The final release ZIP is empty.'
        }
        $archiveSha256 = Get-FilePromptStreamSha256Hex -Stream $archiveStream
        $archive = New-Object IO.Compression.ZipArchive -ArgumentList @(
            $archiveStream,
            [IO.Compression.ZipArchiveMode]::Read,
            $true)
        $entries = @(
            $archive.Entries |
                Where-Object {
                    [string]::Equals(
                        $_.FullName,
                        'PACKAGE-CHECKSUMS-SHA256.txt',
                        [StringComparison]::Ordinal)
                }
        )
        if ($entries.Count -ne 1) {
            throw 'The final release ZIP must contain exactly one root package checksum manifest.'
        }
        $entry = $entries[0]
        if ($entry.Length -le 0 -or $entry.Length -gt 4194304) {
            throw 'The package checksum manifest ZIP entry has an invalid size.'
        }
        $stream = $entry.Open()
        try {
            $bytes = New-Object byte[] ([int]$entry.Length)
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
                if ($read -eq 0) {
                    throw 'The package checksum manifest ZIP entry ended unexpectedly.'
                }
                $offset += $read
            }
            if ($stream.ReadByte() -ne -1) {
                throw 'The package checksum manifest ZIP entry exceeds its declared size.'
            }
        }
        finally {
            $stream.Dispose()
        }
        $manifestIdentity = Get-FilePromptPackageManifestIdentity -Bytes $bytes
        return [pscustomobject]@{
            ArchiveSha256 = $archiveSha256
            ManifestSha256 = $manifestIdentity.Sha256
            ManifestEntryCount = $manifestIdentity.EntryCount
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        $archiveStream.Dispose()
    }
}


function Read-FilePromptZipManifestIdentity {
    param([string]$ArchivePath)

    $identity = Read-FilePromptReleaseArchiveIdentity `
        -ArchivePath $ArchivePath
    return [pscustomobject]@{
        Sha256 = $identity.ManifestSha256
        EntryCount = $identity.ManifestEntryCount
    }
}

function Assert-FilePromptExactAttributes {
    param(
        [Xml.XmlElement]$Element,
        [string[]]$Names,
        [string]$Description
    )

    if ($null -eq $Element -or $Element.NamespaceURI.Length -ne 0) {
        throw "$Description is missing or uses an XML namespace."
    }
    if ($Element.Attributes.Count -ne $Names.Count) {
        throw "$Description has unexpected XML attributes."
    }
    foreach ($name in $Names) {
        if (-not $Element.HasAttribute($name)) {
            throw "$Description is missing XML attribute '$name'."
        }
    }
}

function Assert-FilePromptSafeXmlNodeTree {
    param([Xml.XmlNode]$Node)

    foreach ($child in $Node.ChildNodes) {
        if ($child.NodeType -notin @(
            [Xml.XmlNodeType]::Element,
            [Xml.XmlNodeType]::Text,
            [Xml.XmlNodeType]::Whitespace,
            [Xml.XmlNodeType]::SignificantWhitespace,
            [Xml.XmlNodeType]::XmlDeclaration)) {
            throw "The Windows 7 acceptance XML report contains forbidden node type '$($child.NodeType)'."
        }
        Assert-FilePromptSafeXmlNodeTree -Node $child
    }
}

function Get-FilePromptDirectElements {
    param(
        [Xml.XmlElement]$Parent,
        [string]$Name
    )

    return ,@(
        $Parent.ChildNodes |
            Where-Object {
                $_.NodeType -eq [Xml.XmlNodeType]::Element -and
                $_.LocalName -eq $Name -and
                $_.NamespaceURI.Length -eq 0
            }
    )
}

function Read-FilePromptAcceptanceEvidence {
    param(
        [string]$Path,
        [string]$Version
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'AcceptanceReportPath is required.'
    }
    if (-not [string]::Equals(
        $Version,
        '1.17',
        [StringComparison]::Ordinal)) {
        throw 'The acceptance evidence reader only supports verifier v1.17.'
    }

    $reportPath = [IO.Path]::GetFullPath($Path)
    $reportBytes = Read-FilePromptLockedBytes `
        -Path $reportPath `
        -MaximumLength 2097152 `
        -Description 'The Windows 7 acceptance XML report'
    $reportHash = Get-FilePromptSha256Hex -Bytes $reportBytes

    $sidecarPath = "$reportPath.sha256.txt"
    $sidecarBytes = Read-FilePromptLockedBytes `
        -Path $sidecarPath `
        -MaximumLength 512 `
        -Description 'The Windows 7 acceptance report sidecar'
    if ($sidecarBytes.Length -ge 3 -and
        $sidecarBytes[0] -eq 0xEF -and
        $sidecarBytes[1] -eq 0xBB -and
        $sidecarBytes[2] -eq 0xBF) {
        throw 'The Windows 7 acceptance report sidecar must be UTF-8 without BOM.'
    }
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    try {
        $sidecarText = $strictUtf8.GetString($sidecarBytes)
    }
    catch {
        throw 'The Windows 7 acceptance report sidecar is not valid UTF-8.'
    }
    $expectedSidecar = "$reportHash *$([IO.Path]::GetFileName($reportPath))`r`n"
    if (-not [string]::Equals(
        $sidecarText,
        $expectedSidecar,
        [StringComparison]::Ordinal)) {
        throw 'The Windows 7 acceptance report sidecar is not canonical or does not match the XML bytes.'
    }

    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 2097152
    $settings.MaxCharactersFromEntities = 0
    $settings.IgnoreComments = $false
    $settings.IgnoreProcessingInstructions = $false
    $stream = New-Object IO.MemoryStream -ArgumentList (,$reportBytes)
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        $document = New-Object Xml.XmlDocument
        $document.PreserveWhitespace = $true
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    catch {
        throw "The Windows 7 acceptance XML report is unsafe or invalid: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        $stream.Dispose()
    }

    Assert-FilePromptSafeXmlNodeTree -Node $document
    $root = $document.DocumentElement
    if ($null -eq $root -or
        -not [string]::Equals(
            $root.LocalName,
            'filePromptAiAcceptance',
            [StringComparison]::Ordinal) -or
        $root.NamespaceURI.Length -ne 0) {
        throw 'The Windows 7 acceptance XML report has an invalid root element.'
    }
    Assert-FilePromptExactAttributes `
        -Element $root `
        -Names @('schemaVersion', 'result', 'exitCode', 'createdUtc', 'verifierVersion') `
        -Description 'The Windows 7 acceptance XML root'
    if ($root.GetAttribute('schemaVersion') -ne '2' -or
        $root.GetAttribute('result') -ne 'pass' -or
        $root.GetAttribute('exitCode') -ne '0' -or
        $root.GetAttribute('verifierVersion') -ne '1.17.0.0') {
        throw 'The Windows 7 acceptance XML report is not a passing v1.17 verifier report.'
    }
    $createdUtc = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact(
        $root.GetAttribute('createdUtc'),
        'o',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$createdUtc) -or
        $createdUtc.Kind -ne [DateTimeKind]::Utc) {
        throw 'The Windows 7 acceptance XML report has an invalid createdUtc value.'
    }

    $expectedChildren = @(
        'packageRoot',
        'reportPath',
        'isolatedDataRoot',
        'is64BitOperatingSystem',
        'clrVersion',
        'packageIdentity',
        'checks'
    )
    $rootElements = @(
        $root.ChildNodes |
            Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element }
    )
    if ($rootElements.Count -ne $expectedChildren.Count) {
        throw 'The Windows 7 acceptance XML report has an unexpected element set.'
    }
    for ($index = 0; $index -lt $expectedChildren.Count; $index++) {
        if ($rootElements[$index].LocalName -cne $expectedChildren[$index] -or
            $rootElements[$index].NamespaceURI.Length -ne 0) {
            throw 'The Windows 7 acceptance XML report has an invalid element order.'
        }
    }
    foreach ($childName in $expectedChildren) {
        if ((Get-FilePromptDirectElements -Parent $root -Name $childName).Count -ne 1) {
            throw "The Windows 7 acceptance XML report must contain exactly one '$childName' element."
        }
    }
    foreach ($simpleName in $expectedChildren[0..4]) {
        $simpleElement = (Get-FilePromptDirectElements `
            -Parent $root `
            -Name $simpleName)[0]
        Assert-FilePromptExactAttributes `
            -Element $simpleElement `
            -Names @() `
            -Description "The Windows 7 acceptance '$simpleName' element"
        if (@($simpleElement.ChildNodes |
            Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element }).Count -ne 0) {
            throw "The Windows 7 acceptance '$simpleName' element must contain only text."
        }
    }

    $identity = (Get-FilePromptDirectElements `
        -Parent $root `
        -Name 'packageIdentity')[0]
    Assert-FilePromptExactAttributes `
        -Element $identity `
        -Names @('status', 'manifestName', 'manifestSha256', 'manifestEntryCount') `
        -Description 'The Windows 7 acceptance package identity'
    $manifestSha256 = $identity.GetAttribute('manifestSha256')
    $manifestEntryText = $identity.GetAttribute('manifestEntryCount')
    $manifestEntryCount = 0
    if ($identity.GetAttribute('status') -ne 'verified' -or
        $identity.GetAttribute('manifestName') -ne 'PACKAGE-CHECKSUMS-SHA256.txt' -or
        $manifestSha256 -cnotmatch '^[0-9A-F]{64}$' -or
        -not [int]::TryParse(
            $manifestEntryText,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$manifestEntryCount) -or
        $manifestEntryCount -le 0 -or
        $identity.ChildNodes.Count -ne 0) {
        throw 'The Windows 7 acceptance package manifest identity is invalid or unverified.'
    }

    $checksElement = (Get-FilePromptDirectElements -Parent $root -Name 'checks')[0]
    Assert-FilePromptExactAttributes `
        -Element $checksElement `
        -Names @() `
        -Description 'The Windows 7 acceptance checks container'
    $requiredChecks = @(
        'os.win7-sp1',
        'runtime.dotnet-4.8',
        'display.fullhd-100-percent',
        'package.checksums',
        'files.extract',
        'files.export',
        'api.models',
        'api.chat-completions',
        'application.launch',
        'application.cleanup'
    )
    $checkElements = @(
        $checksElement.ChildNodes |
            Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element }
    )
    if ($checkElements.Count -ne $requiredChecks.Count) {
        throw 'The Windows 7 acceptance XML report has an unexpected check set.'
    }
    $seenChecks = @{}
    foreach ($check in $checkElements) {
        if ($check.LocalName -ne 'check' -or $check.NamespaceURI.Length -ne 0) {
            throw 'The Windows 7 acceptance XML report contains an unexpected checks child.'
        }
        Assert-FilePromptExactAttributes `
            -Element $check `
            -Names @('id', 'status', 'durationMs') `
            -Description 'A Windows 7 acceptance check'
        $identifier = $check.GetAttribute('id')
        $duration = [int64]0
        if ($requiredChecks -cnotcontains $identifier -or
            $seenChecks.ContainsKey($identifier) -or
            $check.GetAttribute('status') -ne 'pass' -or
            -not [int64]::TryParse(
                $check.GetAttribute('durationMs'),
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$duration) -or
            $duration -lt 0) {
            throw "The Windows 7 acceptance check '$identifier' is unknown, duplicated, failed, or invalid."
        }
        $messages = Get-FilePromptDirectElements -Parent $check -Name 'message'
        $evidenceElements = Get-FilePromptDirectElements -Parent $check -Name 'evidence'
        if ($messages.Count -ne 1 -or $evidenceElements.Count -ne 1) {
            throw "The Windows 7 acceptance check '$identifier' has invalid evidence elements."
        }
        $childElements = @(
            $check.ChildNodes |
                Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element }
        )
        if ($childElements.Count -ne 2) {
            throw "The Windows 7 acceptance check '$identifier' has unexpected child elements."
        }
        if ($childElements[0].LocalName -cne 'message' -or
            $childElements[1].LocalName -cne 'evidence') {
            throw "The Windows 7 acceptance check '$identifier' has invalid detail order."
        }
        foreach ($detailElement in @($messages[0], $evidenceElements[0])) {
            Assert-FilePromptExactAttributes `
                -Element $detailElement `
                -Names @() `
                -Description "A Windows 7 acceptance check detail"
            if (@($detailElement.ChildNodes |
                Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element }).Count -ne 0) {
                throw "The Windows 7 acceptance check '$identifier' contains nested detail elements."
            }
        }
        $seenChecks[$identifier] = $true
    }
    foreach ($requiredCheck in $requiredChecks) {
        if (-not $seenChecks.ContainsKey($requiredCheck)) {
            throw "The Windows 7 acceptance XML report is missing required PASS check '$requiredCheck'."
        }
    }

    return [pscustomobject]@{
        ReportPath = $reportPath
        ReportSha256 = $reportHash
        ManifestSha256 = $manifestSha256
        ManifestEntryCount = $manifestEntryCount
        VerifierVersion = $root.GetAttribute('verifierVersion')
        CreatedUtc = $createdUtc
    }
}
