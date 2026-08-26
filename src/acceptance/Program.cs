using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class AcceptanceProgram
{
    private const int ExitOperatingSystem = 1;
    private const int ExitDotNet = 2;
    private const int ExitDisplay = 4;
    private const int ExitPackage = 8;
    private const int ExitLaunch = 16;
    private const int ExitApi = 32;
    private const int ExitFiles = 64;
    private const int ExitInternal = 128;
    private const int MinimumDotNet48Release = 528040;
    private const string TrustedPayloadResourceName =
        "FilePromptAI.Acceptance.TrustedPayload.sha256";
    private const string VerifierRelativePath = "Verify-FilePromptAI.exe";
    private const string ArchiveName = "FilePromptAI-Win7-Full-v1.18.zip";
    private const string WindowTitle =
        "FilePrompt AI  \u00b7  \u5185\u7f51\u6587\u4ef6\u95ee\u7b54\u5de5\u4f5c\u53f0";

    private static readonly List<CheckResult> Results =
        new List<CheckResult>();
    private static readonly object ApiCallbackLock = new object();
    private static StringBuilder apiDelta;
    private static StringBuilder apiStatus;

    private delegate string CheckAction();

    private static int Main(string[] args)
    {
        string packageRoot = Path.GetFullPath(
            AppDomain.CurrentDomain.BaseDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        string nonce = Guid.NewGuid().ToString("N");
        string reportPath = string.Empty;
        string isolatedDataRoot = string.Empty;
        VerifiedArchiveLease verifiedArchive = null;
        VerifiedPackageLease verifiedPackage = null;
        int exitCode = 0;

        try
        {
            reportPath = BuildOutsidePackagePath(
                packageRoot,
                "AcceptanceReports",
                "FilePromptAI-Acceptance-" +
                    DateTime.UtcNow.ToString(
                        "yyyyMMddTHHmmssZ",
                        CultureInfo.InvariantCulture) +
                    "-" + nonce + ".xml",
                false);
            isolatedDataRoot = BuildOutsidePackagePath(
                packageRoot,
                "AcceptanceData",
                "FilePromptAI-Acceptance-Data-" + nonce,
                true);

            if (args.Length != 2 ||
                !string.Equals(args[0], "--archive", StringComparison.Ordinal))
            {
                throw new AcceptanceFailure(
                    "Usage: Verify-FilePromptAI.exe --archive <FilePromptAI-Win7-Full-v1.18.zip>",
                    string.Empty);
            }

            CheckResult os = RunCheck(
                "os.win7-sp1",
                ExitOperatingSystem,
                CheckOperatingSystem,
                ref exitCode);
            CheckResult dotNet = RunCheck(
                "runtime.dotnet-4.8",
                ExitDotNet,
                CheckDotNet48,
                ref exitCode);
            RunCheck(
                "display.fullhd-100-percent",
                ExitDisplay,
                CheckDisplay,
                ref exitCode);
            CheckResult archive = RunCheck(
                "archive.identity",
                ExitPackage,
                delegate
                {
                    verifiedArchive = CheckArchive(args[1], packageRoot);
                    return verifiedArchive.Evidence;
                },
                ref exitCode);
            CheckResult package = RunCheck(
                "package.checksums",
                ExitPackage,
                delegate
                {
                    verifiedPackage = CheckPackage(packageRoot);
                    AssertArchiveMatchesPackage(
                        verifiedArchive,
                        verifiedPackage);
                    return verifiedPackage.Evidence;
                },
                ref exitCode);

            if (dotNet.Status == "pass" &&
                archive.Status == "pass" &&
                package.Status == "pass")
            {
                ApplicationRuntime runtime = null;
                try
                {
                    runtime = new ApplicationRuntime(
                        packageRoot,
                        verifiedPackage);
                    RunCheck(
                        "files.extract",
                        ExitFiles,
                        delegate { return CheckFileExtraction(runtime, packageRoot); },
                        ref exitCode);
                    RunCheck(
                        "files.export",
                        ExitFiles,
                        delegate { return CheckFileExport(runtime); },
                        ref exitCode);
                    RunCheck(
                        "api.models",
                        ExitApi,
                        delegate { return CheckModels(runtime, nonce); },
                        ref exitCode);
                    RunCheck(
                        "api.chat-completions",
                        ExitApi,
                        delegate { return CheckChatCompletions(runtime, nonce); },
                        ref exitCode);
                }
                catch (Exception exception)
                {
                    Results.Add(new CheckResult(
                        "application.load",
                        "error",
                        "The packaged application could not be loaded.",
                        DescribeException(exception),
                        0));
                    exitCode |= ExitInternal | ExitApi | ExitFiles;
                }

                CheckResult launch = RunCheck(
                    "application.launch",
                    ExitLaunch,
                    delegate
                    {
                        return CheckApplicationLaunch(
                            packageRoot,
                            isolatedDataRoot,
                            verifiedPackage);
                    },
                    ref exitCode);
                if (launch.Status == "pass")
                {
                    RunCheck(
                        "application.ui-journey",
                        ExitLaunch | ExitApi | ExitFiles,
                        delegate
                        {
                            verifiedPackage.AssertIntact();
                            string evidence = PackagedUiJourney.Run(
                                verifiedPackage.GetVerifiedPath(
                                    @"app\FilePromptAI.exe"),
                                isolatedDataRoot);
                            verifiedPackage.AssertIntact();
                            return evidence;
                        },
                        ref exitCode);
                }
                else
                {
                    AddSkipped(
                        "application.ui-journey",
                        "Requires the packaged application to launch through the root launcher.",
                        ExitLaunch | ExitApi | ExitFiles,
                        ref exitCode);
                }
            }
            else
            {
                string prerequisite = dotNet.Status != "pass"
                    ? "Requires .NET Framework 4.8."
                    : archive.Status != "pass"
                        ? "Requires a successfully locked release ZIP."
                        : "Requires a successful package checksum check.";
                AddSkipped(
                    "files.extract",
                    prerequisite,
                    ExitFiles,
                    ref exitCode);
                AddSkipped(
                    "files.export",
                    prerequisite,
                    ExitFiles,
                    ref exitCode);
                AddSkipped(
                    "api.models",
                    prerequisite,
                    ExitApi,
                    ref exitCode);
                AddSkipped(
                    "api.chat-completions",
                    prerequisite,
                    ExitApi,
                    ref exitCode);
                AddSkipped(
                    "application.launch",
                    prerequisite,
                    ExitLaunch,
                    ref exitCode);
                AddSkipped(
                    "application.ui-journey",
                    prerequisite,
                    ExitLaunch | ExitApi | ExitFiles,
                    ref exitCode);
            }

            // Keep the OS result live even when other checks pass. A non-Windows 7
            // host must never be able to produce an acceptance PASS.
            if (os.Status != "pass")
            {
                exitCode |= ExitOperatingSystem;
            }
        }
        catch (Exception exception)
        {
            Results.Add(new CheckResult(
                "verifier.internal",
                "error",
                "The verifier encountered an unexpected error.",
                DescribeException(exception),
                0));
            exitCode |= ExitInternal;
        }
        finally
        {
            if (!string.IsNullOrEmpty(isolatedDataRoot))
            {
                RunCheck(
                    "application.cleanup",
                    ExitInternal,
                    delegate
                    {
                        return DeleteAcceptanceData(
                            isolatedDataRoot,
                            packageRoot);
                    },
                    ref exitCode);
            }
        }

        bool passed = exitCode == 0 && AllChecksPassed();
        if (!passed && exitCode == 0)
        {
            exitCode = ExitInternal;
        }

        try
        {
            if (string.IsNullOrEmpty(reportPath))
            {
                throw new InvalidOperationException(
                    "No safe acceptance report path is available.");
            }
            WriteReport(
                reportPath,
                packageRoot,
                isolatedDataRoot,
                verifiedArchive,
                verifiedPackage,
                passed,
                exitCode);
            WriteReportChecksum(reportPath, packageRoot);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "ERROR | report | " + DescribeException(exception));
            exitCode |= ExitInternal;
            passed = false;
            try
            {
                // If the XML was created but its checksum failed, rewrite the
                // report as a failure so no standalone PASS report survives.
                WriteReport(
                    reportPath,
                    packageRoot,
                    isolatedDataRoot,
                    verifiedArchive,
                    verifiedPackage,
                    false,
                    exitCode);
            }
            catch
            {
                // The console result and non-zero exit code remain authoritative.
            }
        }

        if (verifiedPackage != null)
        {
            verifiedPackage.Dispose();
        }
        if (verifiedArchive != null)
        {
            verifiedArchive.Dispose();
        }
        PrintResults(passed, exitCode, reportPath);
        return exitCode & 255;
    }

    private static string BuildOutsidePackagePath(
        string packageRoot,
        string fallbackCategory,
        string leafName,
        bool directoryTarget)
    {
        if (string.IsNullOrEmpty(leafName) ||
            !string.Equals(
                Path.GetFileName(leafName),
                leafName,
                StringComparison.Ordinal) ||
            leafName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                "The acceptance output leaf name is unsafe.");
        }

        string candidate;
        string tempFailure;
        if (TryBuildSafeOutputPath(
            Path.GetTempPath(),
            packageRoot,
            leafName,
            directoryTarget,
            out candidate,
            out tempFailure))
        {
            return candidate;
        }

        string localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localData))
        {
            throw new InvalidOperationException(
                "TEMP is unsafe and LocalApplicationData is unavailable. " +
                tempFailure);
        }
        string fallbackRoot = Path.Combine(
            Path.Combine(localData, "FilePromptAI-Acceptance"),
            fallbackCategory);
        string fallbackFailure;
        if (TryBuildSafeOutputPath(
            fallbackRoot,
            packageRoot,
            leafName,
            directoryTarget,
            out candidate,
            out fallbackFailure))
        {
            return candidate;
        }

        throw new InvalidOperationException(
            "No safe acceptance output path outside the package is available. " +
            "TEMP=" + tempFailure + "; LocalApplicationData=" + fallbackFailure);
    }

    private static bool TryBuildSafeOutputPath(
        string baseRoot,
        string packageRoot,
        string leafName,
        bool directoryTarget,
        out string candidate,
        out string failure)
    {
        candidate = string.Empty;
        failure = string.Empty;
        try
        {
            if (string.IsNullOrEmpty(baseRoot))
            {
                throw new InvalidOperationException("The output root is empty.");
            }

            string canonicalRoot = EnsureSafeDirectoryPath(
                baseRoot,
                packageRoot);

            string finalRoot = GetFinalDirectoryPath(canonicalRoot);
            string finalPackageRoot = GetFinalDirectoryPath(packageRoot);
            string finalCandidate = Path.GetFullPath(Path.Combine(
                finalRoot,
                leafName));
            if (IsPathInsideRoot(finalCandidate, finalPackageRoot))
            {
                throw new InvalidOperationException(
                    "The resolved output path is inside the package.");
            }

            candidate = Path.GetFullPath(Path.Combine(canonicalRoot, leafName));
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                throw new InvalidOperationException(
                    "The unique output target already exists.");
            }
            if (!directoryTarget && Directory.Exists(candidate))
            {
                throw new InvalidOperationException(
                    "The report target is a directory.");
            }
            return true;
        }
        catch (Exception exception)
        {
            failure = DescribeException(exception);
            candidate = string.Empty;
            return false;
        }
    }

    private static string EnsureSafeDirectoryPath(
        string path,
        string packageRoot)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        DirectoryInfo current = new DirectoryInfo(fullPath);
        List<string> missingSegments = new List<string>();
        while (!current.Exists)
        {
            if (string.IsNullOrEmpty(current.Name) || current.Parent == null)
            {
                throw new InvalidOperationException(
                    "No existing output path ancestor is available.");
            }
            missingSegments.Insert(0, current.Name);
            current = current.Parent;
        }

        AssertNoReparsePointInAncestors(current.FullName);
        string finalPackageRoot = GetFinalDirectoryPath(packageRoot);
        string finalCandidate = GetFinalDirectoryPath(current.FullName);
        for (int index = 0; index < missingSegments.Count; index++)
        {
            finalCandidate = Path.Combine(
                finalCandidate,
                missingSegments[index]);
        }
        if (IsPathInsideRoot(finalCandidate, finalPackageRoot))
        {
            throw new InvalidOperationException(
                "Creating the output directory would write inside the package.");
        }

        string lexicalCurrent = current.FullName;
        for (int index = 0; index < missingSegments.Count; index++)
        {
            lexicalCurrent = Path.Combine(
                lexicalCurrent,
                missingSegments[index]);
            Directory.CreateDirectory(lexicalCurrent);
            DirectoryInfo created = new DirectoryInfo(lexicalCurrent);
            if ((created.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "A created output directory is a reparse point: " +
                    lexicalCurrent);
            }
            string createdFinalPath = GetFinalDirectoryPath(lexicalCurrent);
            if (IsPathInsideRoot(createdFinalPath, finalPackageRoot))
            {
                throw new InvalidOperationException(
                    "A created output directory resolves inside the package.");
            }
        }
        AssertNoReparsePointInAncestors(fullPath);
        return fullPath;
    }

    private static bool IsPathInsideRoot(string path, string root)
    {
        string resolvedPath = Path.GetFullPath(path);
        string resolvedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.Equals(
                resolvedPath,
                resolvedRoot,
                StringComparison.OrdinalIgnoreCase) ||
            resolvedPath.StartsWith(
                resolvedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoReparsePointInAncestors(string path)
    {
        DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(path));
        while (current != null)
        {
            if (!current.Exists)
            {
                throw new InvalidOperationException(
                    "An output path ancestor does not exist: " + current.FullName);
            }
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "An output path ancestor is a reparse point: " + current.FullName);
            }
            current = current.Parent;
        }
    }

    private static string GetFinalDirectoryPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        using (SafeFileHandle handle = NativeMethods.CreateFile(
            fullPath,
            NativeMethods.FileReadAttributes,
            NativeMethods.FileShareRead |
                NativeMethods.FileShareWrite |
                NativeMethods.FileShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics,
            IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                throw new IOException(
                    "CreateFile could not open the output directory. Win32=" +
                    Marshal.GetLastWin32Error().ToString(
                        CultureInfo.InvariantCulture));
            }

            uint capacity = 1024;
            StringBuilder value = new StringBuilder((int)capacity);
            uint length = NativeMethods.GetFinalPathNameByHandle(
                handle,
                value,
                capacity,
                0);
            if (length >= capacity)
            {
                capacity = length + 1;
                value = new StringBuilder((int)capacity);
                length = NativeMethods.GetFinalPathNameByHandle(
                    handle,
                    value,
                    capacity,
                    0);
            }
            if (length == 0 || length >= capacity)
            {
                throw new IOException(
                    "GetFinalPathNameByHandle could not resolve the output directory. Win32=" +
                    Marshal.GetLastWin32Error().ToString(
                        CultureInfo.InvariantCulture));
            }

            string finalPath = value.ToString();
            if (finalPath.StartsWith(
                @"\\?\UNC\",
                StringComparison.OrdinalIgnoreCase))
            {
                finalPath = @"\\" + finalPath.Substring(8);
            }
            else if (finalPath.StartsWith(
                @"\\?\",
                StringComparison.OrdinalIgnoreCase))
            {
                finalPath = finalPath.Substring(4);
            }
            return Path.GetFullPath(finalPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
    }

    private static void AssertSafeOutputTarget(
        string path,
        string packageRoot,
        bool directoryTarget)
    {
        string fullPath = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new InvalidOperationException(
                "The acceptance output parent does not exist.");
        }
        AssertNoReparsePointInAncestors(parent);
        string finalParent = GetFinalDirectoryPath(parent);
        string finalPackageRoot = GetFinalDirectoryPath(packageRoot);
        string finalTarget = Path.GetFullPath(Path.Combine(
            finalParent,
            Path.GetFileName(fullPath)));
        if (IsPathInsideRoot(finalTarget, finalPackageRoot))
        {
            throw new InvalidOperationException(
                "The resolved acceptance output target is inside the package.");
        }
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            FileSystemInfo item = directoryTarget
                ? (FileSystemInfo)new DirectoryInfo(fullPath)
                : new FileInfo(fullPath);
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The acceptance output target is a reparse point.");
            }
        }
    }

    private static CheckResult RunCheck(
        string identifier,
        int failureBit,
        CheckAction action,
        ref int exitCode)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        CheckResult result;
        try
        {
            string evidence = action();
            result = new CheckResult(
                identifier,
                "pass",
                "Required check passed.",
                evidence,
                stopwatch.ElapsedMilliseconds);
        }
        catch (AcceptanceFailure exception)
        {
            result = new CheckResult(
                identifier,
                "fail",
                exception.Message,
                exception.Evidence,
                stopwatch.ElapsedMilliseconds);
            exitCode |= failureBit;
        }
        catch (Exception exception)
        {
            result = new CheckResult(
                identifier,
                "error",
                "Required check raised an unexpected error.",
                DescribeException(exception),
                stopwatch.ElapsedMilliseconds);
            exitCode |= failureBit | ExitInternal;
        }

        stopwatch.Stop();
        Results.Add(result);
        return result;
    }

    private static void AddSkipped(
        string identifier,
        string reason,
        int failureBit,
        ref int exitCode)
    {
        Results.Add(new CheckResult(
            identifier,
            "skip",
            reason,
            string.Empty,
            0));
        exitCode |= failureBit;
    }

    private static string CheckOperatingSystem()
    {
        NativeVersion version = NativeVersion.Read();
        string evidence = string.Format(
            CultureInfo.InvariantCulture,
            "RtlGetVersion={0}.{1}.{2}; servicePack={3}.{4}; " +
                "productType={5}; Environment.OSVersion={6}; " +
                "Environment.ServicePack={7}",
            version.Major,
            version.Minor,
            version.Build,
            version.ServicePackMajor,
            version.ServicePackMinor,
            version.ProductType,
            Environment.OSVersion.Version,
            Environment.OSVersion.ServicePack ?? string.Empty);

        if (version.Major != 6 || version.Minor != 1)
        {
            throw new AcceptanceFailure(
                "This is not Windows 7. A Windows 7 PASS is forbidden on this host.",
                evidence);
        }
        if (version.ProductType != 1)
        {
            throw new AcceptanceFailure(
                "Windows Server 2008 R2 is not accepted as Windows 7.",
                evidence);
        }
        if (version.ServicePackMajor < 1)
        {
            throw new AcceptanceFailure(
                "Windows 7 Service Pack 1 is required.",
                evidence);
        }

        return evidence;
    }

    private static string CheckDotNet48()
    {
        int release = ReadDotNetRelease();
        Version runtime = Environment.Version;
        string evidence = string.Format(
            CultureInfo.InvariantCulture,
            ".NET Release={0}; verifier CLR={1}",
            release,
            runtime);
        if (release < MinimumDotNet48Release)
        {
            throw new AcceptanceFailure(
                ".NET Framework 4.8 is not installed (Release must be at least 528040).",
                evidence);
        }
        if (runtime.Major < 4)
        {
            throw new AcceptanceFailure(
                "The verifier is running under CLR 2 because CLR 4 is unavailable.",
                evidence);
        }

        return evidence;
    }

    private static int ReadDotNetRelease()
    {
        string[] paths =
        {
            @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
            @"SOFTWARE\Wow6432Node\Microsoft\NET Framework Setup\NDP\v4\Full"
        };
        int highest = 0;
        for (int index = 0; index < paths.Length; index++)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(paths[index]))
            {
                object value = key == null ? null : key.GetValue("Release");
                int parsed;
                if (value != null && int.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out parsed))
                {
                    highest = Math.Max(highest, parsed);
                }
            }
        }

        return highest;
    }

    private static string CheckDisplay()
    {
        DisplayEvidence display = DisplayEvidence.Read();
        string evidence = display.ToString();
        if (!display.DpiAware)
        {
            throw new AcceptanceFailure(
                "The verifier DPI-awareness manifest was not applied.",
                evidence);
        }
        if (display.MetricsWidth != 1920 || display.MetricsHeight != 1080)
        {
            throw new AcceptanceFailure(
                "Primary screen metrics must be exactly 1920x1080.",
                evidence);
        }
        if (display.ModeWidth != 1920 || display.ModeHeight != 1080)
        {
            throw new AcceptanceFailure(
                "Primary display mode must be exactly 1920x1080.",
                evidence);
        }
        if (display.MetricsWidth != display.ModeWidth ||
            display.MetricsHeight != display.ModeHeight)
        {
            throw new AcceptanceFailure(
                "Primary screen metrics do not match the physical display mode.",
                evidence);
        }
        if (display.DpiX != 96 || display.DpiY != 96)
        {
            throw new AcceptanceFailure(
                "Display scaling must be exactly 100 percent (96x96 DPI).",
                evidence);
        }

        return evidence;
    }

    private static VerifiedArchiveLease CheckArchive(
        string archivePath,
        string packageRoot)
    {
        if (string.IsNullOrEmpty(archivePath))
        {
            throw new AcceptanceFailure(
                "The original release ZIP path is required.",
                string.Empty);
        }

        string fullPath = Path.GetFullPath(archivePath);
        if (!string.Equals(
            Path.GetFileName(fullPath),
            ArchiveName,
            StringComparison.Ordinal))
        {
            throw new AcceptanceFailure(
                "The original release ZIP has a non-canonical name.",
                fullPath);
        }
        if (!File.Exists(fullPath))
        {
            throw new AcceptanceFailure(
                "The original release ZIP is missing.",
                fullPath);
        }
        if (IsPathInsideRoot(fullPath, packageRoot))
        {
            throw new AcceptanceFailure(
                "The original release ZIP must remain outside the extracted package.",
                fullPath);
        }

        string parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent))
        {
            throw new AcceptanceFailure(
                "The original release ZIP parent is invalid.",
                fullPath);
        }
        AssertNoReparsePointInAncestors(parent);
        FileInfo item = new FileInfo(fullPath);
        if ((item.Attributes &
            (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new AcceptanceFailure(
                "The original release ZIP is not a regular file.",
                fullPath);
        }

        FileStream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        try
        {
            if (stream.Length <= 0)
            {
                throw new AcceptanceFailure(
                    "The original release ZIP is empty.",
                    fullPath);
            }
            string sha256 = ComputeSha256(stream);
            ZipManifestIdentity manifest = ReadArchiveManifestIdentity(stream);
            return new VerifiedArchiveLease(
                fullPath,
                sha256,
                stream.Length,
                manifest.Sha256,
                manifest.EntryCount,
                stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static VerifiedPackageLease CheckPackage(string packageRoot)
    {
        VerifiedPackageLease lease = new VerifiedPackageLease(packageRoot);
        try
        {
            AssertNoReparsePoints(packageRoot);
            lease.LockDirectories();
            AssertNoReparsePoints(packageRoot);
            Dictionary<string, string> trustedEntries =
                ReadTrustedPayloadEntries();
            const string manifestRelativePath =
                "PACKAGE-CHECKSUMS-SHA256.txt";
            string manifestPath = Path.Combine(
                packageRoot,
                manifestRelativePath);
            if (!File.Exists(manifestPath))
            {
                throw new AcceptanceFailure(
                    "Package checksum manifest is missing.",
                    manifestPath);
            }

            string rootPrefix = packageRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            Dictionary<string, string> entries =
                new Dictionary<string, string>(StringComparer.Ordinal);
            FileStream manifestStream = lease.LockFile(
                manifestRelativePath);
            string manifestSha256 = ComputeSha256(manifestStream);
            string[] lines = ReadLockedUtf8Lines(manifestStream);
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];
                if (line.Length < 67 || line[64] != ' ' || line[65] != '*')
                {
                    throw new AcceptanceFailure(
                        "Package checksum manifest contains an invalid line.",
                        "line=" + (lineIndex + 1).ToString(
                            CultureInfo.InvariantCulture));
                }

                string expectedHash = line.Substring(0, 64);
                string relativePath = line.Substring(66);
                if (!IsUpperHexSha256(expectedHash) ||
                    !IsSafeCanonicalRelativePath(relativePath))
                {
                    throw new AcceptanceFailure(
                        "Package checksum manifest contains an unsafe or non-canonical entry.",
                        "line=" + (lineIndex + 1).ToString(
                            CultureInfo.InvariantCulture) +
                            "; path=" + relativePath);
                }
                if (entries.ContainsKey(relativePath))
                {
                    throw new AcceptanceFailure(
                        "Package checksum manifest contains a duplicate path.",
                        relativePath);
                }

                string fullPath = Path.GetFullPath(Path.Combine(
                    packageRoot,
                    relativePath));
                if (!fullPath.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        fullPath.Substring(rootPrefix.Length),
                        relativePath,
                        StringComparison.Ordinal))
                {
                    throw new AcceptanceFailure(
                        "Package checksum path does not resolve canonically.",
                        relativePath);
                }
                if (!File.Exists(fullPath))
                {
                    throw new AcceptanceFailure(
                        "A package file listed in the checksum manifest is missing.",
                        relativePath);
                }

                FileStream payloadStream = lease.LockFile(relativePath);
                string actualHash = ComputeSha256(payloadStream);
                if (!string.Equals(
                    actualHash,
                    expectedHash,
                    StringComparison.Ordinal))
                {
                    throw new AcceptanceFailure(
                        "A package file failed SHA-256 verification.",
                        relativePath + "; expected=" + expectedHash +
                            "; actual=" + actualHash);
                }
                entries.Add(relativePath, expectedHash);
            }

            if (trustedEntries.ContainsKey(VerifierRelativePath) ||
                trustedEntries.ContainsKey(manifestRelativePath))
            {
                throw new AcceptanceFailure(
                    "The embedded trusted payload set contains a forbidden self-reference.",
                    string.Empty);
            }
            if (!entries.ContainsKey(VerifierRelativePath) ||
                entries.Count != trustedEntries.Count + 1)
            {
                throw new AcceptanceFailure(
                    "The package manifest does not match the embedded trusted payload set.",
                    "packageEntries=" + entries.Count.ToString(
                        CultureInfo.InvariantCulture) +
                        "; trustedEntries=" + trustedEntries.Count.ToString(
                            CultureInfo.InvariantCulture));
            }
            foreach (KeyValuePair<string, string> trusted in trustedEntries)
            {
                string packageHash;
                if (!entries.TryGetValue(trusted.Key, out packageHash) ||
                    !string.Equals(
                        packageHash,
                        trusted.Value,
                        StringComparison.Ordinal))
                {
                    throw new AcceptanceFailure(
                        "The package manifest differs from the embedded trusted payload set.",
                        trusted.Key + "; trusted=" + trusted.Value +
                            "; package=" + (packageHash ?? "missing"));
                }
            }
            foreach (string relativePath in entries.Keys)
            {
                if (!string.Equals(
                        relativePath,
                        VerifierRelativePath,
                        StringComparison.Ordinal) &&
                    !trustedEntries.ContainsKey(relativePath))
                {
                    throw new AcceptanceFailure(
                        "The package manifest contains an untrusted payload entry.",
                        relativePath);
                }
            }

            List<string> actualFiles = new List<string>();
            string[] files = Directory.GetFiles(
                packageRoot,
                "*",
                SearchOption.AllDirectories);
            for (int index = 0; index < files.Length; index++)
            {
                string fullPath = Path.GetFullPath(files[index]);
                if (!fullPath.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new AcceptanceFailure(
                        "Package file enumeration escaped the release root.",
                        fullPath);
                }
                if (string.Equals(
                    fullPath,
                    manifestPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativePath = fullPath.Substring(rootPrefix.Length);
                actualFiles.Add(relativePath);
                if (!entries.ContainsKey(relativePath))
                {
                    throw new AcceptanceFailure(
                        "The package contains a file absent from the checksum manifest.",
                        relativePath);
                }
            }

            foreach (string relativePath in entries.Keys)
            {
                if (!actualFiles.Contains(relativePath))
                {
                    throw new AcceptanceFailure(
                        "The checksum manifest contains a file absent from the package.",
                        relativePath);
                }
            }

            string[] requiredFiles =
            {
                "Start-FilePromptAI.exe",
                "Start-FilePromptAI.exe.config",
                "Uninstall-FilePromptAI.exe",
                "Uninstall-FilePromptAI.exe.config",
                "Verify-FilePromptAI.exe",
                "Verify-FilePromptAI.exe.config",
                "OFFLINE-README.txt",
                @"runtime\NDP48-x86-x64-AllOS-ENU.exe",
                @"app\FilePromptAI.exe",
                @"app\FilePromptAI.exe.config",
                @"acceptance\fixtures\acceptance.txt"
            };
            for (int index = 0; index < requiredFiles.Length; index++)
            {
                if (!entries.ContainsKey(requiredFiles[index]))
                {
                    throw new AcceptanceFailure(
                        "A required release artifact is absent from the package manifest.",
                        requiredFiles[index]);
                }
            }

            lease.Seal(entries.Keys);
            lease.AssertIntact();
            lease.SetVerifiedManifestIdentity(
                manifestSha256,
                entries.Count);
            lease.Evidence = "manifestEntries=" + entries.Count.ToString(
                CultureInfo.InvariantCulture) +
                "; embeddedTrustedEntries=" + trustedEntries.Count.ToString(
                    CultureInfo.InvariantCulture) +
                "; exactFileSet=true; lockedFiles=" +
                    lease.LockedFileCount.ToString(CultureInfo.InvariantCulture) +
                "; manifest=" + manifestPath;
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void AssertArchiveMatchesPackage(
        VerifiedArchiveLease archive,
        VerifiedPackageLease package)
    {
        if (archive == null || package == null ||
            !archive.HasVerifiedIdentity ||
            !package.HasVerifiedManifestIdentity ||
            !string.Equals(
                archive.ManifestSha256,
                package.ManifestSha256,
                StringComparison.Ordinal) ||
            archive.ManifestEntryCount != package.ManifestEntryCount)
        {
            throw new AcceptanceFailure(
                "The original release ZIP and extracted package have different manifest identities.",
                archive == null
                    ? string.Empty
                    : "archiveManifestSha256=" + archive.ManifestSha256 +
                        "; archiveManifestEntries=" +
                        archive.ManifestEntryCount.ToString(
                            CultureInfo.InvariantCulture));
        }
    }

    private static Dictionary<string, string> ReadTrustedPayloadEntries()
    {
        Dictionary<string, string> entries =
            new Dictionary<string, string>(StringComparer.Ordinal);
        Assembly assembly = Assembly.GetExecutingAssembly();
        using (Stream stream = assembly.GetManifestResourceStream(
            TrustedPayloadResourceName))
        {
            if (stream == null)
            {
                throw new AcceptanceFailure(
                    "The verifier embedded trusted payload set is missing.",
                    TrustedPayloadResourceName);
            }
            using (StreamReader reader = new StreamReader(
                stream,
                new UTF8Encoding(false, true),
                true))
            {
                string line;
                int lineNumber = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (line.Length < 67 || line[64] != ' ' || line[65] != '*')
                    {
                        throw new AcceptanceFailure(
                            "The verifier embedded trusted payload set contains an invalid line.",
                            "line=" + lineNumber.ToString(
                                CultureInfo.InvariantCulture));
                    }

                    string hash = line.Substring(0, 64);
                    string relativePath = line.Substring(66);
                    if (!IsUpperHexSha256(hash) ||
                        !IsSafeCanonicalRelativePath(relativePath) ||
                        entries.ContainsKey(relativePath))
                    {
                        throw new AcceptanceFailure(
                            "The verifier embedded trusted payload set is unsafe or non-canonical.",
                            "line=" + lineNumber.ToString(
                                CultureInfo.InvariantCulture) +
                                "; path=" + relativePath);
                    }
                    entries.Add(relativePath, hash);
                }
            }
        }
        if (entries.Count == 0)
        {
            throw new AcceptanceFailure(
                "The verifier embedded trusted payload set is empty.",
                TrustedPayloadResourceName);
        }
        return entries;
    }

    private static void AssertNoReparsePoints(string packageRoot)
    {
        DirectoryInfo root = new DirectoryInfo(packageRoot);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AcceptanceFailure(
                "The package root must not be a reparse point.",
                packageRoot);
        }

        Queue<DirectoryInfo> pending = new Queue<DirectoryInfo>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            DirectoryInfo current = pending.Dequeue();
            FileSystemInfo[] children;
            try
            {
                children = current.GetFileSystemInfos();
            }
            catch (Exception exception)
            {
                throw new AcceptanceFailure(
                    "The package directory could not be enumerated.",
                    current.FullName + "; " + exception.Message);
            }

            for (int index = 0; index < children.Length; index++)
            {
                if ((children[index].Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AcceptanceFailure(
                        "The package must not contain reparse points.",
                        children[index].FullName);
                }
                DirectoryInfo directory = children[index] as DirectoryInfo;
                if (directory != null)
                {
                    pending.Enqueue(directory);
                }
            }
        }
    }

    private static bool IsUpperHexSha256(string value)
    {
        if (value == null || value.Length != 64)
        {
            return false;
        }
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (!((character >= '0' && character <= '9') ||
                (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSafeCanonicalRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.IndexOf(':') >= 0 ||
            relativePath.IndexOf('/') >= 0 ||
            relativePath[0] == '\\' ||
            relativePath[relativePath.Length - 1] == '\\')
        {
            return false;
        }
        string[] segments = relativePath.Split('\\');
        for (int index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length == 0 ||
                segments[index] == "." ||
                segments[index] == ".." ||
                segments[index].TrimEnd(' ', '.') != segments[index])
            {
                return false;
            }
        }
        return true;
    }

    private static string ComputeSha256(string path)
    {
        using (FileStream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            return ComputeSha256(stream);
        }
    }

    private static string ComputeSha256(Stream stream)
    {
        if (stream == null || !stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "A readable, seekable stream is required.",
                "stream");
        }
        stream.Position = 0;
        using (SHA256CryptoServiceProvider algorithm =
            new SHA256CryptoServiceProvider())
        {
            byte[] hash = algorithm.ComputeHash(stream);
            stream.Position = 0;
            StringBuilder value = new StringBuilder(hash.Length * 2);
            for (int index = 0; index < hash.Length; index++)
            {
                value.Append(hash[index].ToString(
                    "X2",
                    CultureInfo.InvariantCulture));
            }
            return value.ToString();
        }
    }

    private static string[] ReadLockedUtf8Lines(Stream stream)
    {
        if (stream == null || !stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "A readable, seekable stream is required.",
                "stream");
        }
        if (stream.Length > Int32.MaxValue)
        {
            throw new InvalidDataException("The UTF-8 file is too large.");
        }

        stream.Position = 0;
        byte[] bytes = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0)
            {
                throw new EndOfStreamException(
                    "The locked UTF-8 file ended unexpectedly.");
            }
            offset += count;
        }
        stream.Position = 0;

        List<string> lines = new List<string>();
        using (MemoryStream memory = new MemoryStream(bytes, false))
        using (StreamReader reader = new StreamReader(
            memory,
            new UTF8Encoding(false, true),
            true))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }
        }
        return lines.ToArray();
    }

    private static ZipManifestIdentity ReadArchiveManifestIdentity(Stream stream)
    {
        const string manifestName = "PACKAGE-CHECKSUMS-SHA256.txt";
        const int maximumManifestLength = 4 * 1024 * 1024;
        if (stream == null || !stream.CanRead || !stream.CanSeek ||
            stream.Length < 22)
        {
            throw new AcceptanceFailure(
                "The original release ZIP is unreadable or truncated.",
                string.Empty);
        }

        int tailLength = (int)Math.Min(stream.Length, 65535L + 22L);
        byte[] tail = new byte[tailLength];
        ReadExactlyAt(stream, stream.Length - tailLength, tail);
        int endOffset = -1;
        for (int index = tail.Length - 22; index >= 0; index--)
        {
            if (ReadUInt32(tail, index) == 0x06054B50U)
            {
                int commentLength = ReadUInt16(tail, index + 20);
                if (index + 22 + commentLength == tail.Length)
                {
                    endOffset = index;
                    break;
                }
            }
        }
        if (endOffset < 0 ||
            ReadUInt16(tail, endOffset + 4) != 0 ||
            ReadUInt16(tail, endOffset + 6) != 0)
        {
            throw new AcceptanceFailure(
                "The original release ZIP has an invalid end record.",
                string.Empty);
        }

        int entryCount = ReadUInt16(tail, endOffset + 10);
        if (entryCount <= 0 ||
            ReadUInt16(tail, endOffset + 8) != entryCount)
        {
            throw new AcceptanceFailure(
                "The original release ZIP has an invalid central-directory entry count.",
                string.Empty);
        }
        long centralSize = ReadUInt32(tail, endOffset + 12);
        long centralOffset = ReadUInt32(tail, endOffset + 16);
        long absoluteEndOffset = stream.Length - tailLength + endOffset;
        if (centralSize <= 0 ||
            centralOffset < 0 ||
            centralOffset + centralSize != absoluteEndOffset ||
            centralSize > Int32.MaxValue)
        {
            throw new AcceptanceFailure(
                "The original release ZIP uses an unsupported or inconsistent central directory.",
                string.Empty);
        }

        byte[] central = new byte[(int)centralSize];
        ReadExactlyAt(stream, centralOffset, central);
        byte[] expectedName = Encoding.ASCII.GetBytes(manifestName);
        ZipEntryLocation manifest = null;
        int cursor = 0;
        for (int index = 0; index < entryCount; index++)
        {
            if (cursor > central.Length - 46 ||
                ReadUInt32(central, cursor) != 0x02014B50U)
            {
                throw new AcceptanceFailure(
                    "The original release ZIP central directory is invalid.",
                    "entry=" + index.ToString(CultureInfo.InvariantCulture));
            }
            int flags = ReadUInt16(central, cursor + 8);
            int method = ReadUInt16(central, cursor + 10);
            uint crc32 = ReadUInt32(central, cursor + 16);
            long compressedSize = ReadUInt32(central, cursor + 20);
            long uncompressedSize = ReadUInt32(central, cursor + 24);
            int nameLength = ReadUInt16(central, cursor + 28);
            int extraLength = ReadUInt16(central, cursor + 30);
            int commentLength = ReadUInt16(central, cursor + 32);
            int disk = ReadUInt16(central, cursor + 34);
            long localOffset = ReadUInt32(central, cursor + 42);
            long recordLength = 46L + nameLength + extraLength + commentLength;
            if (nameLength <= 0 || recordLength > central.Length - cursor ||
                disk != 0 || compressedSize == UInt32.MaxValue ||
                uncompressedSize == UInt32.MaxValue ||
                localOffset == UInt32.MaxValue)
            {
                throw new AcceptanceFailure(
                    "The original release ZIP contains an unsupported central-directory entry.",
                    "entry=" + index.ToString(CultureInfo.InvariantCulture));
            }

            if (BytesEqual(central, cursor + 46, nameLength, expectedName))
            {
                if (manifest != null ||
                    (flags & 1) != 0 ||
                    (method != 0 && method != 8) ||
                    compressedSize < 0 ||
                    compressedSize > maximumManifestLength ||
                    uncompressedSize <= 0 ||
                    uncompressedSize > maximumManifestLength)
                {
                    throw new AcceptanceFailure(
                        "The original release ZIP manifest entry is duplicated, encrypted, or unsupported.",
                        string.Empty);
                }
                manifest = new ZipEntryLocation(
                    flags,
                    method,
                    crc32,
                    compressedSize,
                    uncompressedSize,
                    localOffset,
                    expectedName);
            }
            cursor += (int)recordLength;
        }
        if (cursor != central.Length || manifest == null)
        {
            throw new AcceptanceFailure(
                "The original release ZIP must contain exactly one root package checksum manifest.",
                string.Empty);
        }

        byte[] header = new byte[30];
        ReadExactlyAt(stream, manifest.LocalOffset, header);
        int localNameLength = ReadUInt16(header, 26);
        int localExtraLength = ReadUInt16(header, 28);
        if (ReadUInt32(header, 0) != 0x04034B50U ||
            ReadUInt16(header, 6) != manifest.Flags ||
            ReadUInt16(header, 8) != manifest.Method ||
            localNameLength != manifest.Name.Length)
        {
            throw new AcceptanceFailure(
                "The original release ZIP manifest local header is inconsistent.",
                string.Empty);
        }
        byte[] localName = new byte[localNameLength];
        ReadExactlyAt(stream, manifest.LocalOffset + 30, localName);
        if (!BytesEqual(localName, 0, localName.Length, manifest.Name))
        {
            throw new AcceptanceFailure(
                "The original release ZIP manifest local name is inconsistent.",
                string.Empty);
        }

        long dataOffset = manifest.LocalOffset + 30L +
            localNameLength + localExtraLength;
        if (dataOffset < 0 ||
            dataOffset + manifest.CompressedSize > centralOffset)
        {
            throw new AcceptanceFailure(
                "The original release ZIP manifest data range is invalid.",
                string.Empty);
        }
        byte[] compressed = new byte[(int)manifest.CompressedSize];
        ReadExactlyAt(stream, dataOffset, compressed);
        byte[] bytes;
        if (manifest.Method == 0)
        {
            bytes = compressed;
        }
        else
        {
            using (MemoryStream input = new MemoryStream(compressed, false))
            using (DeflateStream inflater = new DeflateStream(
                input,
                CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = inflater.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + count > maximumManifestLength)
                    {
                        throw new AcceptanceFailure(
                            "The original release ZIP manifest exceeds its size limit.",
                            string.Empty);
                    }
                    output.Write(buffer, 0, count);
                }
                bytes = output.ToArray();
            }
        }
        if (bytes.Length != manifest.UncompressedSize ||
            ComputeCrc32(bytes) != manifest.Crc32)
        {
            throw new AcceptanceFailure(
                "The original release ZIP manifest size or CRC-32 is invalid.",
                string.Empty);
        }
        return GetManifestIdentity(bytes);
    }

    private static ZipManifestIdentity GetManifestIdentity(byte[] bytes)
    {
        if (bytes == null || bytes.Length <= 0 || bytes.Length > 4 * 1024 * 1024 ||
            (bytes.Length >= 3 && bytes[0] == 0xEF &&
                bytes[1] == 0xBB && bytes[2] == 0xBF))
        {
            throw new AcceptanceFailure(
                "The original release ZIP manifest has invalid UTF-8 bytes.",
                string.Empty);
        }
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new AcceptanceFailure(
                "The original release ZIP manifest is not valid UTF-8.",
                string.Empty);
        }
        if (!text.EndsWith("\r\n", StringComparison.Ordinal) ||
            text.IndexOf("\r\n", StringComparison.Ordinal) < 0)
        {
            throw new AcceptanceFailure(
                "The original release ZIP manifest is not canonical CRLF text.",
                string.Empty);
        }
        string body = text.Substring(0, text.Length - 2);
        string[] lines = body.Split(new string[] { "\r\n" },
            StringSplitOptions.None);
        Dictionary<string, bool> paths = new Dictionary<string, bool>(
            StringComparer.Ordinal);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.IndexOf('\r') >= 0 || line.IndexOf('\n') >= 0 ||
                line.Length < 67 || line[64] != ' ' || line[65] != '*')
            {
                throw new AcceptanceFailure(
                    "The original release ZIP manifest contains a non-canonical line.",
                    "line=" + (index + 1).ToString(CultureInfo.InvariantCulture));
            }
            string hash = line.Substring(0, 64);
            string path = line.Substring(66);
            if (!IsUpperHexSha256(hash) ||
                !IsSafeCanonicalRelativePath(path) ||
                paths.ContainsKey(path))
            {
                throw new AcceptanceFailure(
                    "The original release ZIP manifest contains an unsafe or duplicate path.",
                    path);
            }
            paths.Add(path, true);
        }
        using (MemoryStream memory = new MemoryStream(bytes, false))
        {
            return new ZipManifestIdentity(
                ComputeSha256(memory),
                paths.Count);
        }
    }

    private static void ReadExactlyAt(Stream stream, long position, byte[] bytes)
    {
        if (position < 0 || bytes == null || position > stream.Length - bytes.Length)
        {
            throw new AcceptanceFailure(
                "The original release ZIP contains an invalid byte range.",
                string.Empty);
        }
        stream.Position = position;
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count <= 0)
            {
                throw new EndOfStreamException(
                    "The original release ZIP ended unexpectedly.");
            }
            offset += count;
        }
        stream.Position = 0;
    }

    private static int ReadUInt16(byte[] bytes, int offset)
    {
        if (bytes == null || offset < 0 || offset > bytes.Length - 2)
        {
            throw new InvalidDataException("A ZIP UInt16 field is truncated.");
        }
        return bytes[offset] | (bytes[offset + 1] << 8);
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        if (bytes == null || offset < 0 || offset > bytes.Length - 4)
        {
            throw new InvalidDataException("A ZIP UInt32 field is truncated.");
        }
        return (uint)(bytes[offset] |
            (bytes[offset + 1] << 8) |
            (bytes[offset + 2] << 16) |
            (bytes[offset + 3] << 24));
    }

    private static bool BytesEqual(
        byte[] bytes,
        int offset,
        int length,
        byte[] expected)
    {
        if (bytes == null || expected == null ||
            length != expected.Length || offset < 0 ||
            offset > bytes.Length - length)
        {
            return false;
        }
        for (int index = 0; index < length; index++)
        {
            if (bytes[offset + index] != expected[index])
            {
                return false;
            }
        }
        return true;
    }

    private static uint ComputeCrc32(byte[] bytes)
    {
        uint value = UInt32.MaxValue;
        for (int index = 0; index < bytes.Length; index++)
        {
            value ^= bytes[index];
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? (value >> 1) ^ 0xEDB88320U
                    : value >> 1;
            }
        }
        return ~value;
    }

    private static string CheckFileExtraction(
        ApplicationRuntime runtime,
        string packageRoot)
    {
        object extractor = Activator.CreateInstance(runtime.ExtractorType, true);
        MethodInfo extract = runtime.ExtractorType.GetMethod(
            "ExtractFile",
            BindingFlags.Instance | BindingFlags.Public);
        if (extract == null)
        {
            throw new AcceptanceFailure(
                "The real FileContentExtractor.ExtractFile method was not found.",
                runtime.ApplicationPath);
        }
        string fixtureRoot = Path.Combine(packageRoot, @"acceptance\fixtures");
        ExtractionExpectation[] expectations =
        {
            new ExtractionExpectation(
                "acceptance.txt",
                "Text",
                "FilePromptAI offline acceptance text fixture 2026.",
                false),
            new ExtractionExpectation(
                "sample.pdf",
                "Text",
                "Hello PDF 123",
                false),
            new ExtractionExpectation(
                "sample.docx",
                "Text",
                "Hello DOCX",
                false),
            new ExtractionExpectation(
                "sample.png",
                "Image",
                string.Empty,
                true)
        };
        StringBuilder evidence = new StringBuilder("realExtractor=true");
        for (int index = 0; index < expectations.Length; index++)
        {
            ExtractionExpectation expected = expectations[index];
            string fixture = Path.Combine(fixtureRoot, expected.Name);
            if (!File.Exists(fixture))
            {
                throw new AcceptanceFailure(
                    "An acceptance parsing fixture is missing.",
                    fixture);
            }
            object item = InvokeAndUnwrap(
                extract,
                extractor,
                new object[] { fixture });
            Type itemType = item.GetType();
            string name = Convert.ToString(
                itemType.GetProperty("Name").GetValue(item, null),
                CultureInfo.InvariantCulture);
            string text = Convert.ToString(
                itemType.GetProperty("TextContent").GetValue(item, null),
                CultureInfo.InvariantCulture);
            string kind = Convert.ToString(
                itemType.GetProperty("Kind").GetValue(item, null),
                CultureInfo.InvariantCulture);
            byte[] binary = itemType.GetProperty("BinaryData")
                .GetValue(item, null) as byte[];
            bool valid = name == expected.Name && kind == expected.Kind;
            if (expected.Binary)
            {
                valid = valid && binary != null && binary.Length > 0;
            }
            else
            {
                valid = valid &&
                    text != null &&
                    text.IndexOf(expected.TextMarker, StringComparison.Ordinal) >= 0 &&
                    (binary == null || binary.Length == 0);
            }
            if (!valid)
            {
                throw new AcceptanceFailure(
                    "The packaged FileContentExtractor did not parse a fixture correctly.",
                    "fixture=" + expected.Name +
                        "; name=" + name +
                        "; kind=" + kind +
                        "; textLength=" + (text == null ? 0 : text.Length).ToString(
                            CultureInfo.InvariantCulture) +
                        "; binaryLength=" + (binary == null ? 0 : binary.Length).ToString(
                            CultureInfo.InvariantCulture));
            }
            evidence.Append("; ");
            evidence.Append(expected.Name);
            evidence.Append("=");
            evidence.Append(expected.Binary
                ? binary.Length.ToString(CultureInfo.InvariantCulture) + " bytes"
                : text.Length.ToString(CultureInfo.InvariantCulture) + " chars");
        }

        return evidence.ToString();
    }

    private static string CheckFileExport(ApplicationRuntime runtime)
    {
        string markdown =
            "# Offline acceptance export\n\n" +
            "ASCII paragraph for Windows 7 export verification.\n\n" +
            "| Name | Value |\n| --- | --- |\n| alpha | 42 |";
        byte[] docx = InvokeByteExporter(runtime, "DocxExporter", markdown);
        byte[] pdf = InvokeByteExporter(runtime, "PdfExporter", markdown);
        byte[] pptx = InvokeByteExporter(runtime, "PptxExporter", markdown);
        byte[] xlsx = InvokeByteExporter(runtime, "XlsxExporter", markdown);
        byte[] xmind = InvokeByteExporter(runtime, "XMindExporter", markdown);
        Type csvType = runtime.RequireApplicationType(
            "FilePromptAIWin7.CsvExporter");
        MethodInfo toCsv = csvType.GetMethod(
            "ToCsv",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new Type[] { typeof(string) },
            null);
        string csv = Convert.ToString(
            InvokeAndUnwrap(toCsv, null, new object[] { markdown }),
            CultureInfo.InvariantCulture);

        AssertZipSignature("DOCX", docx, 500);
        AssertPdfSignature(pdf);
        AssertZipSignature("PPTX", pptx, 500);
        AssertZipSignature("XLSX", xlsx, 500);
        AssertZipSignature("XMind", xmind, 300);
        if (csv == null ||
            csv.IndexOf("Name,Value", StringComparison.Ordinal) < 0 ||
            csv.IndexOf("alpha,42", StringComparison.Ordinal) < 0)
        {
            throw new AcceptanceFailure(
                "The real CSV exporter did not preserve the expected table.",
                "csvLength=" + (csv == null ? 0 : csv.Length).ToString(
                    CultureInfo.InvariantCulture));
        }

        return "realExporters=true; docx=" + docx.Length.ToString(
            CultureInfo.InvariantCulture) +
            "; pdf=" + pdf.Length.ToString(CultureInfo.InvariantCulture) +
            "; pptx=" + pptx.Length.ToString(CultureInfo.InvariantCulture) +
            "; xlsx=" + xlsx.Length.ToString(CultureInfo.InvariantCulture) +
            "; xmind=" + xmind.Length.ToString(CultureInfo.InvariantCulture) +
            "; csv=" + csv.Length.ToString(CultureInfo.InvariantCulture);
    }

    private static byte[] InvokeByteExporter(
        ApplicationRuntime runtime,
        string typeName,
        string markdown)
    {
        Type exporter = runtime.RequireApplicationType(
            "FilePromptAIWin7." + typeName);
        MethodInfo create = exporter.GetMethod(
            "Create",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new Type[] { typeof(string) },
            null);
        if (create == null)
        {
            throw new AcceptanceFailure(
                "The real exporter method was not found.",
                typeName + ".Create(string)");
        }
        byte[] bytes = InvokeAndUnwrap(
            create,
            null,
            new object[] { markdown }) as byte[];
        if (bytes == null)
        {
            throw new AcceptanceFailure(
                "The real exporter returned no byte payload.",
                typeName);
        }
        return bytes;
    }

    private static void AssertZipSignature(
        string name,
        byte[] bytes,
        int minimumLength)
    {
        if (bytes == null || bytes.Length < minimumLength ||
            bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
        {
            throw new AcceptanceFailure(
                name + " export did not produce a non-empty ZIP package.",
                "length=" + (bytes == null ? 0 : bytes.Length).ToString(
                    CultureInfo.InvariantCulture));
        }
    }

    private static void AssertPdfSignature(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 500 ||
            bytes[0] != (byte)'%' || bytes[1] != (byte)'P' ||
            bytes[2] != (byte)'D' || bytes[3] != (byte)'F')
        {
            throw new AcceptanceFailure(
                "PDF export did not produce a non-empty PDF document.",
                "length=" + (bytes == null ? 0 : bytes.Length).ToString(
                    CultureInfo.InvariantCulture));
        }
    }

    private static string CheckModels(ApplicationRuntime runtime, string nonce)
    {
        LoopbackServer server = new LoopbackServer(
            LoopbackResponse.Models(nonce));
        object client = null;
        try
        {
            server.Start();
            DisableProxyForLoopback();
            client = Activator.CreateInstance(runtime.ModelClientType, true);
            MethodInfo fetch = runtime.ModelClientType.GetMethod(
                "FetchModelsAsync",
                BindingFlags.Instance | BindingFlags.Public);
            if (fetch == null)
            {
                throw new AcceptanceFailure(
                    "The real ModelClient.FetchModelsAsync method was not found.",
                    runtime.ApplicationPath);
            }

            string key = string.Empty;
            string endpoint = server.BaseUrl +
                "v1/chat/completions?acceptance=" + nonce;
            object task = InvokeAndUnwrap(
                fetch,
                client,
                new object[]
                {
                    endpoint,
                    key,
                    Activator.CreateInstance(
                        fetch.GetParameters()[2].ParameterType)
                });
            WaitForTask(task, "model discovery", 15000);
            IList models = GetTaskResult(task) as IList;
            string rawRequest = server.WaitForRequest(5000);

            bool hasExpectedModel = false;
            bool sorted = models != null && models.Count == 2;
            if (models != null)
            {
                for (int index = 0; index < models.Count; index++)
                {
                    if (Convert.ToString(
                        models[index],
                        CultureInfo.InvariantCulture) == "acceptance-" + nonce)
                    {
                        hasExpectedModel = true;
                    }
                }
                sorted = sorted &&
                    Convert.ToString(models[0], CultureInfo.InvariantCulture) ==
                        "acceptance-" + nonce &&
                    Convert.ToString(models[1], CultureInfo.InvariantCulture) ==
                        "z-local-model";
            }

            string expectedRequestLine =
                "GET /v1/models?acceptance=" + nonce + " HTTP/1.1";
            if (rawRequest.IndexOf(
                    expectedRequestLine,
                    StringComparison.Ordinal) < 0 ||
                HasAuthorizationHeader(rawRequest) ||
                !hasExpectedModel ||
                !sorted)
            {
                throw new AcceptanceFailure(
                    "Active model discovery did not use the derived same-origin /models endpoint correctly.",
                    "requestLine=" + FirstLine(rawRequest) +
                        "; authorizationAbsent=" + !HasAuthorizationHeader(rawRequest) +
                        "; count=" + (models == null ? -1 : models.Count).ToString(
                            CultureInfo.InvariantCulture));
            }

            return "realModelClient=true; request=" + expectedRequestLine +
                "; authorizationAbsent=true; modelCount=" + models.Count.ToString(
                    CultureInfo.InvariantCulture) +
                "; sorted=true; loopbackOnly=true";
        }
        finally
        {
            if (client is IDisposable)
            {
                ((IDisposable)client).Dispose();
            }
            server.Dispose();
        }
    }

    private static string CheckChatCompletions(
        ApplicationRuntime runtime,
        string nonce)
    {
        string responseText = "acceptance-stream-" + nonce;
        LoopbackServer server = new LoopbackServer(
            LoopbackResponse.Chat(responseText));
        object client = null;
        try
        {
            server.Start();
            DisableProxyForLoopback();
            client = Activator.CreateInstance(runtime.ModelClientType, true);
            object request = Activator.CreateInstance(
                runtime.ModelRequestType,
                true);
            string key = string.Empty;
            string endpoint = server.BaseUrl +
                "v1/chat/completions?acceptance=" + nonce;
            SetProperty(runtime.ModelRequestType, request, "EndpointUrl", endpoint);
            SetProperty(runtime.ModelRequestType, request, "ApiKey", key);
            SetProperty(
                runtime.ModelRequestType,
                request,
                "ModelName",
                "acceptance-model");
            SetProperty(
                runtime.ModelRequestType,
                request,
                "SystemPrompt",
                "Offline acceptance verification.");
            SetProperty(
                runtime.ModelRequestType,
                request,
                "Prompt",
                "acceptance-prompt-" + nonce);
            SetProperty(
                runtime.ModelRequestType,
                request,
                "Attachments",
                CreateGenericList(runtime.InputItemType));
            SetProperty(
                runtime.ModelRequestType,
                request,
                "ConversationMessages",
                CreateGenericList(runtime.ConversationMessageType));

            apiDelta = new StringBuilder();
            apiStatus = new StringBuilder();
            Action<string> onDelta = new Action<string>(CaptureApiDelta);
            Action<string> onStatus = new Action<string>(CaptureApiStatus);
            MethodInfo generate = runtime.ModelClientType.GetMethod(
                "GenerateAsync",
                BindingFlags.Instance | BindingFlags.Public);
            if (generate == null)
            {
                throw new AcceptanceFailure(
                    "The real ModelClient.GenerateAsync method was not found.",
                    runtime.ApplicationPath);
            }

            object task = InvokeAndUnwrap(
                generate,
                client,
                new object[]
                {
                    request,
                    onDelta,
                    onStatus,
                    Activator.CreateInstance(
                        generate.GetParameters()[3].ParameterType)
                });
            WaitForTask(task, "SSE chat completions", 15000);
            string result = Convert.ToString(
                GetTaskResult(task),
                CultureInfo.InvariantCulture);
            string rawRequest = server.WaitForRequest(5000);
            string capturedDelta;
            lock (ApiCallbackLock)
            {
                capturedDelta = apiDelta.ToString();
            }

            bool exactRequest = rawRequest.IndexOf(
                "POST /v1/chat/completions?acceptance=" + nonce + " HTTP/1.1",
                StringComparison.Ordinal) >= 0;
            bool authorizationAbsent = !HasAuthorizationHeader(rawRequest);
            bool model = rawRequest.IndexOf(
                "\"model\":\"acceptance-model\"",
                StringComparison.Ordinal) >= 0;
            bool prompt = rawRequest.IndexOf(
                "acceptance-prompt-" + nonce,
                StringComparison.Ordinal) >= 0;
            bool streaming = rawRequest.IndexOf(
                "\"stream\":true",
                StringComparison.Ordinal) >= 0;
            if (!exactRequest || !authorizationAbsent || !model || !prompt || !streaming ||
                result != responseText || capturedDelta != responseText)
            {
                throw new AcceptanceFailure(
                    "Streaming Chat Completions acceptance failed.",
                    "request=" + FirstLine(rawRequest) +
                        "; authorizationAbsent=" + authorizationAbsent +
                        "; model=" + model +
                        "; prompt=" + prompt +
                        "; stream=" + streaming +
                        "; resultMatch=" + (result == responseText) +
                        "; deltaMatch=" + (capturedDelta == responseText));
            }

            return "realModelClient=true; endpoint=" + endpoint +
                "; authorizationAbsent=true; stream=true; sseCompletion=true; loopbackOnly=true";
        }
        finally
        {
            if (client is IDisposable)
            {
                ((IDisposable)client).Dispose();
            }
            server.Dispose();
        }
    }

    private static void DisableProxyForLoopback()
    {
        WebRequest.DefaultWebProxy = new WebProxy();
    }

    private static bool HasAuthorizationHeader(string rawRequest)
    {
        if (rawRequest == null)
        {
            return false;
        }
        return rawRequest.IndexOf(
            "\r\nAuthorization:",
            StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void CaptureApiDelta(string value)
    {
        lock (ApiCallbackLock)
        {
            apiDelta.Append(value);
        }
    }

    private static void CaptureApiStatus(string value)
    {
        lock (ApiCallbackLock)
        {
            if (!string.IsNullOrEmpty(value))
            {
                if (apiStatus.Length > 0)
                {
                    apiStatus.Append(" | ");
                }
                apiStatus.Append(value);
            }
        }
    }

    private static object CreateGenericList(Type itemType)
    {
        Type listType = typeof(List<>).MakeGenericType(itemType);
        return Activator.CreateInstance(listType);
    }

    private static void SetProperty(
        Type type,
        object instance,
        string name,
        object value)
    {
        PropertyInfo property = type.GetProperty(name);
        if (property == null)
        {
            throw new MissingMemberException(type.FullName, name);
        }
        property.SetValue(instance, value, null);
    }

    private static object InvokeAndUnwrap(
        MethodInfo method,
        object instance,
        object[] arguments)
    {
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException ?? exception;
        }
    }

    private static void WaitForTask(
        object taskObject,
        string description,
        int timeoutMilliseconds)
    {
        if (taskObject == null)
        {
            throw new InvalidOperationException(
                description + " did not return a Task.");
        }
        Type taskType = taskObject.GetType();
        MethodInfo wait = taskType.GetMethod(
            "Wait",
            new Type[] { typeof(int) });
        if (wait == null)
        {
            throw new MissingMethodException(taskType.FullName, "Wait(Int32)");
        }
        try
        {
            bool completed = Convert.ToBoolean(
                wait.Invoke(taskObject, new object[] { timeoutMilliseconds }),
                CultureInfo.InvariantCulture);
            if (!completed)
            {
                throw new TimeoutException(description + " timed out.");
            }
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException ?? exception;
        }
    }

    private static object GetTaskResult(object taskObject)
    {
        PropertyInfo result = taskObject.GetType().GetProperty("Result");
        if (result == null)
        {
            throw new MissingMemberException(
                taskObject.GetType().FullName,
                "Result");
        }
        try
        {
            return result.GetValue(taskObject, null);
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException ?? exception;
        }
    }

    private static string CheckApplicationLaunch(
        string packageRoot,
        string isolatedDataRoot,
        VerifiedPackageLease verifiedPackage)
    {
        verifiedPackage.AssertIntact();
        string launcherPath = verifiedPackage.GetVerifiedPath(
            @"Start-FilePromptAI.exe");
        string applicationPath = verifiedPackage.GetVerifiedPath(
            @"app\FilePromptAI.exe");

        AssertSafeOutputTarget(isolatedDataRoot, packageRoot, true);
        Directory.CreateDirectory(isolatedDataRoot);
        AssertSafeOutputTarget(isolatedDataRoot, packageRoot, true);
        List<int> existingApplicationProcessIds =
            GetProcessIds(Path.GetFileNameWithoutExtension(applicationPath));
        Process launcher = null;
        Process process = null;
        IntPtr applicationProcessHandle = IntPtr.Zero;
        bool forcedTermination = false;
        try
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = launcherPath;
            start.WorkingDirectory = packageRoot;
            start.UseShellExecute = false;
            start.CreateNoWindow = false;
            start.EnvironmentVariables["FILEPROMPTAI_DATA_ROOT"] =
                isolatedDataRoot;
            verifiedPackage.AssertIntact();
            launcher = Process.Start(start);
            if (launcher == null)
            {
                throw new AcceptanceFailure(
                    "Process.Start returned no launcher process.",
                    launcherPath);
            }

            if (!launcher.WaitForExit(10000))
            {
                throw new AcceptanceFailure(
                    "The root launcher did not exit within 10 seconds.",
                    "launcherPid=" + launcher.Id.ToString(
                        CultureInfo.InvariantCulture));
            }
            if (launcher.ExitCode != 0)
            {
                throw new AcceptanceFailure(
                    "The root launcher returned a failure exit code.",
                    "launcherPid=" + launcher.Id.ToString(
                        CultureInfo.InvariantCulture) +
                        "; exitCode=" + launcher.ExitCode.ToString(
                            CultureInfo.InvariantCulture));
            }

            Stopwatch wait = Stopwatch.StartNew();
            IntPtr window = IntPtr.Zero;
            string title = string.Empty;
            bool responding = false;
            while (wait.ElapsedMilliseconds < 15000)
            {
                if (process == null)
                {
                    process = FindNewApplicationProcess(
                        applicationPath,
                        existingApplicationProcessIds,
                        out applicationProcessHandle);
                    if (process == null)
                    {
                        Thread.Sleep(100);
                        continue;
                    }
                }
                process.Refresh();
                if (process.HasExited)
                {
                    uint earlyExitCode = GetNativeProcessExitCode(
                        applicationProcessHandle,
                        process.Id);
                    throw new AcceptanceFailure(
                        "FilePromptAI exited before opening its main window.",
                        "exitCode=" + earlyExitCode.ToString(
                            CultureInfo.InvariantCulture));
                }
                window = process.MainWindowHandle;
                title = process.MainWindowTitle;
                responding = process.Responding;
                if (window != IntPtr.Zero &&
                    string.Equals(title, WindowTitle, StringComparison.Ordinal) &&
                    responding)
                {
                    break;
                }
                Thread.Sleep(100);
            }

            if (window == IntPtr.Zero ||
                !string.Equals(title, WindowTitle, StringComparison.Ordinal) ||
                !responding)
            {
                throw new AcceptanceFailure(
                    "FilePromptAI did not open a responsive main window within 15 seconds.",
                    "pid=" + process.Id.ToString(CultureInfo.InvariantCulture) +
                        "; handle=" + window.ToInt64().ToString(
                            CultureInfo.InvariantCulture) +
                        "; title=" + title +
                        "; responding=" + responding);
            }

            string launchedImage = GetProcessImagePath(process);
            if (!string.Equals(
                    launchedImage,
                    applicationPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new AcceptanceFailure(
                    "The root launcher opened an unexpected executable.",
                    "expected=" + applicationPath + "; actual=" + launchedImage);
            }
            verifiedPackage.AssertSameIdentity(@"Start-FilePromptAI.exe");
            verifiedPackage.AssertSameIdentity(@"app\FilePromptAI.exe");

            NativeMethods.Rect clientRect;
            if (!NativeMethods.GetClientRect(window, out clientRect) ||
                clientRect.Right <= clientRect.Left ||
                clientRect.Bottom <= clientRect.Top)
            {
                throw new AcceptanceFailure(
                    "The FilePromptAI main window has no valid client area.",
                    "pid=" + process.Id.ToString(CultureInfo.InvariantCulture));
            }

            int width = clientRect.Right - clientRect.Left;
            int height = clientRect.Bottom - clientRect.Top;
            if (!NativeMethods.PostMessage(
                    window,
                    NativeMethods.WmClose,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new AcceptanceFailure(
                    "WM_CLOSE could not be posted to the FilePromptAI window.",
                    "pid=" + process.Id.ToString(CultureInfo.InvariantCulture));
            }
            if (!process.WaitForExit(5000))
            {
                forcedTermination = true;
                process.Kill();
                process.WaitForExit(5000);
            }

            if (forcedTermination)
            {
                throw new AcceptanceFailure(
                    "FilePromptAI did not close normally after WM_CLOSE.",
                    "pid=" + process.Id.ToString(CultureInfo.InvariantCulture) +
                        "; title=" + title + "; forcedTermination=true");
            }
            uint applicationExitCode = GetNativeProcessExitCode(
                applicationProcessHandle,
                process.Id);
            if (applicationExitCode != 0)
            {
                throw new AcceptanceFailure(
                    "FilePromptAI returned a failure exit code after normal close.",
                    "pid=" + process.Id.ToString(CultureInfo.InvariantCulture) +
                        "; exitCode=" + applicationExitCode.ToString(
                            CultureInfo.InvariantCulture));
            }
            verifiedPackage.AssertIntact();

            return "launcherPid=" + launcher.Id.ToString(
                    CultureInfo.InvariantCulture) +
                "; launcherExitCode=" + launcher.ExitCode.ToString(
                    CultureInfo.InvariantCulture) +
                "; applicationPid=" + process.Id.ToString(
                    CultureInfo.InvariantCulture) +
                "; applicationPath=" + launchedImage +
                "; title=" + title +
                "; responsive=true; client=" +
                width.ToString(CultureInfo.InvariantCulture) + "x" +
                height.ToString(CultureInfo.InvariantCulture) +
                "; isolatedDataRoot=" + isolatedDataRoot +
                "; applicationExitCode=" + applicationExitCode.ToString(
                    CultureInfo.InvariantCulture) +
                "; forcedTermination=" + forcedTermination;
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        forcedTermination = true;
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch
                {
                    // The report still records the original launch failure.
                }
                process.Dispose();
            }
            if (launcher != null)
            {
                try
                {
                    if (!launcher.HasExited)
                    {
                        launcher.Kill();
                        launcher.WaitForExit(5000);
                    }
                }
                catch
                {
                    // The report still records the original launcher failure.
                }
                launcher.Dispose();
            }
        }
    }

    private static List<int> GetProcessIds(string processName)
    {
        List<int> processIds = new List<int>();
        Process[] processes = Process.GetProcessesByName(processName);
        for (int index = 0; index < processes.Length; index++)
        {
            try
            {
                processIds.Add(processes[index].Id);
            }
            finally
            {
                processes[index].Dispose();
            }
        }
        return processIds;
    }

    private static Process FindNewApplicationProcess(
        string expectedApplicationPath,
        List<int> existingProcessIds,
        out IntPtr processHandle)
    {
        processHandle = IntPtr.Zero;
        string processName = Path.GetFileNameWithoutExtension(
            expectedApplicationPath);
        Process[] candidates = Process.GetProcessesByName(processName);
        for (int index = 0; index < candidates.Length; index++)
        {
            Process candidate = candidates[index];
            bool keep = false;
            try
            {
                if (existingProcessIds.Contains(candidate.Id) || candidate.HasExited)
                {
                    continue;
                }
                string imagePath = GetProcessImagePath(candidate);
                if (string.Equals(
                        imagePath,
                        expectedApplicationPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    IntPtr candidateHandle = candidate.Handle;
                    if (candidateHandle == IntPtr.Zero)
                    {
                        continue;
                    }
                    processHandle = candidateHandle;
                    keep = true;
                    return candidate;
                }
            }
            catch (InvalidOperationException)
            {
                // A candidate may disappear while the launcher is starting.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Retry while the new process is still initializing.
            }
            finally
            {
                if (!keep)
                {
                    candidate.Dispose();
                }
            }
        }
        return null;
    }

    private static uint GetNativeProcessExitCode(
        IntPtr processHandle,
        int processId)
    {
        uint exitCode;
        if (processHandle == IntPtr.Zero ||
            !NativeMethods.GetExitCodeProcess(processHandle, out exitCode))
        {
            int error = Marshal.GetLastWin32Error();
            throw new AcceptanceFailure(
                "The FilePromptAI process exit code is unavailable.",
                "pid=" + processId.ToString(CultureInfo.InvariantCulture) +
                    "; win32Error=" + error.ToString(
                        CultureInfo.InvariantCulture));
        }
        return exitCode;
    }

    private static string GetProcessImagePath(Process process)
    {
        string imagePath = process.MainModule.FileName;
        if (string.IsNullOrEmpty(imagePath))
        {
            throw new AcceptanceFailure(
                "The launched process image path is unavailable.",
                "pid=" + process.Id.ToString(CultureInfo.InvariantCulture));
        }
        return Path.GetFullPath(imagePath);
    }

    private static string DeleteAcceptanceData(
        string path,
        string packageRoot)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new AcceptanceFailure(
                "The isolated acceptance data path is empty.",
                string.Empty);
        }
        string resolved = Path.GetFullPath(path);
        if (!Path.GetFileName(resolved).StartsWith(
            "FilePromptAI-Acceptance-Data-",
            StringComparison.Ordinal) ||
            Path.GetFileName(resolved).Length !=
                "FilePromptAI-Acceptance-Data-".Length + 32)
        {
            throw new AcceptanceFailure(
                "The isolated acceptance data directory name is unsafe.",
                resolved);
        }
        if (!Directory.Exists(resolved))
        {
            return "path=" + resolved + "; existed=false; removed=true";
        }

        AssertSafeOutputTarget(resolved, packageRoot, true);
        AssertNoReparsePoints(resolved);
        Directory.Delete(resolved, true);
        if (Directory.Exists(resolved) || File.Exists(resolved))
        {
            throw new AcceptanceFailure(
                "The isolated acceptance data directory still exists after cleanup.",
                resolved);
        }
        return "path=" + resolved + "; existed=true; removed=true";
    }

    private static bool AllChecksPassed()
    {
        for (int index = 0; index < Results.Count; index++)
        {
            if (Results[index].Status != "pass")
            {
                return false;
            }
        }
        return Results.Count > 0;
    }

    private static void WriteReport(
        string reportPath,
        string packageRoot,
        string isolatedDataRoot,
        VerifiedArchiveLease verifiedArchive,
        VerifiedPackageLease verifiedPackage,
        bool passed,
        int exitCode)
    {
        AssertSafeOutputTarget(reportPath, packageRoot, false);
        XmlWriterSettings settings = new XmlWriterSettings();
        settings.Encoding = new UTF8Encoding(false);
        settings.Indent = true;
        settings.NewLineChars = "\r\n";
        using (XmlWriter writer = XmlWriter.Create(reportPath, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("filePromptAiAcceptance");
            writer.WriteAttributeString("schemaVersion", "3");
            writer.WriteAttributeString("result", passed ? "pass" : "fail");
            writer.WriteAttributeString(
                "exitCode",
                exitCode.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString(
                "createdUtc",
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            writer.WriteAttributeString(
                "verifierVersion",
                Assembly.GetExecutingAssembly().GetName().Version.ToString());

            WriteElement(writer, "packageRoot", packageRoot);
            WriteElement(writer, "reportPath", reportPath);
            WriteElement(writer, "isolatedDataRoot", isolatedDataRoot);
            WriteElement(writer, "is64BitOperatingSystem", Is64BitOperatingSystem().ToString());
            WriteElement(writer, "clrVersion", Environment.Version.ToString());

            writer.WriteStartElement("packageIdentity");
            if (passed)
            {
                if (verifiedArchive == null ||
                    !verifiedArchive.HasVerifiedIdentity ||
                    verifiedPackage == null ||
                    !verifiedPackage.HasVerifiedManifestIdentity)
                {
                    throw new InvalidOperationException(
                        "A passing report requires verified archive and package identities.");
                }
                writer.WriteAttributeString("status", "verified");
                writer.WriteAttributeString("archiveName", ArchiveName);
                writer.WriteAttributeString(
                    "archiveSha256",
                    verifiedArchive.Sha256);
                writer.WriteAttributeString(
                    "archiveSize",
                    verifiedArchive.Size.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString(
                    "manifestName",
                    "PACKAGE-CHECKSUMS-SHA256.txt");
                writer.WriteAttributeString(
                    "manifestSha256",
                    verifiedPackage.ManifestSha256);
                writer.WriteAttributeString(
                    "manifestEntryCount",
                    verifiedPackage.ManifestEntryCount.ToString(
                        CultureInfo.InvariantCulture));
            }
            else
            {
                // A failed run must not expose an identity that a release gate
                // could mistake for proof of an accepted package.
                writer.WriteAttributeString("status", "unverified");
            }
            writer.WriteEndElement();

            writer.WriteStartElement("checks");
            for (int index = 0; index < Results.Count; index++)
            {
                CheckResult result = Results[index];
                writer.WriteStartElement("check");
                writer.WriteAttributeString("id", result.Identifier);
                writer.WriteAttributeString("status", result.Status);
                writer.WriteAttributeString(
                    "durationMs",
                    result.DurationMilliseconds.ToString(
                        CultureInfo.InvariantCulture));
                WriteElement(writer, "message", result.Message);
                WriteElement(writer, "evidence", result.Evidence);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
    }

    private static void WriteReportChecksum(
        string reportPath,
        string packageRoot)
    {
        string checksumPath = reportPath + ".sha256.txt";
        AssertSafeOutputTarget(checksumPath, packageRoot, false);
        string line = ComputeSha256(reportPath) + " *" +
            Path.GetFileName(reportPath) + "\r\n";
        File.WriteAllText(
            checksumPath,
            line,
            new UTF8Encoding(false));
    }

    private static void WriteElement(
        XmlWriter writer,
        string name,
        string value)
    {
        writer.WriteStartElement(name);
        writer.WriteString(value ?? string.Empty);
        writer.WriteEndElement();
    }

    private static bool Is64BitOperatingSystem()
    {
        if (IntPtr.Size == 8)
        {
            return true;
        }
        bool wow64;
        return NativeMethods.IsWow64Process(
            Process.GetCurrentProcess().Handle,
            out wow64) && wow64;
    }

    private static void PrintResults(
        bool passed,
        int exitCode,
        string reportPath)
    {
        for (int index = 0; index < Results.Count; index++)
        {
            CheckResult result = Results[index];
            Console.WriteLine(
                result.Status.ToUpperInvariant() + " | " +
                result.Identifier + " | " + result.Message);
            if (!string.IsNullOrEmpty(result.Evidence))
            {
                Console.WriteLine("  " + result.Evidence);
            }
        }
        Console.WriteLine("REPORT | " + reportPath);
        Console.WriteLine(
            (passed ? "PASS" : "FAIL") +
            " | win7 acceptance | exitCode=" +
            exitCode.ToString(CultureInfo.InvariantCulture));
    }

    private static string FirstLine(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }
        int end = value.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? value : value.Substring(0, end);
    }

    private static string DescribeException(Exception exception)
    {
        Exception actual = exception;
        while (actual is TargetInvocationException &&
            actual.InnerException != null)
        {
            actual = actual.InnerException;
        }
        return actual.GetType().FullName + ": " + actual.Message;
    }

    private sealed class VerifiedArchiveLease : IDisposable
    {
        private FileStream stream;

        public string FullPath { get; private set; }
        public string Sha256 { get; private set; }
        public long Size { get; private set; }
        public string ManifestSha256 { get; private set; }
        public int ManifestEntryCount { get; private set; }

        public bool HasVerifiedIdentity
        {
            get
            {
                return stream != null &&
                    IsUpperHexSha256(Sha256) &&
                    Size > 0 &&
                    IsUpperHexSha256(ManifestSha256) &&
                    ManifestEntryCount > 0;
            }
        }

        public string Evidence
        {
            get
            {
                return "archive=" + FullPath +
                    "; sha256=" + Sha256 +
                    "; size=" + Size.ToString(CultureInfo.InvariantCulture) +
                    "; locked=true";
            }
        }

        public VerifiedArchiveLease(
            string fullPath,
            string sha256,
            long size,
            string manifestSha256,
            int manifestEntryCount,
            FileStream stream)
        {
            if (string.IsNullOrEmpty(fullPath) ||
                !IsUpperHexSha256(sha256) ||
                size <= 0 ||
                !IsUpperHexSha256(manifestSha256) ||
                manifestEntryCount <= 0 ||
                stream == null)
            {
                throw new ArgumentException(
                    "A verified archive lease requires a canonical identity.");
            }
            FullPath = fullPath;
            Sha256 = sha256;
            Size = size;
            ManifestSha256 = manifestSha256;
            ManifestEntryCount = manifestEntryCount;
            this.stream = stream;
        }

        public void Dispose()
        {
            if (stream != null)
            {
                stream.Dispose();
                stream = null;
            }
        }
    }

    private sealed class ZipManifestIdentity
    {
        public string Sha256 { get; private set; }
        public int EntryCount { get; private set; }

        public ZipManifestIdentity(string sha256, int entryCount)
        {
            Sha256 = sha256;
            EntryCount = entryCount;
        }
    }

    private sealed class ZipEntryLocation
    {
        public int Flags { get; private set; }
        public int Method { get; private set; }
        public uint Crc32 { get; private set; }
        public long CompressedSize { get; private set; }
        public long UncompressedSize { get; private set; }
        public long LocalOffset { get; private set; }
        public byte[] Name { get; private set; }

        public ZipEntryLocation(
            int flags,
            int method,
            uint crc32,
            long compressedSize,
            long uncompressedSize,
            long localOffset,
            byte[] name)
        {
            Flags = flags;
            Method = method;
            Crc32 = crc32;
            CompressedSize = compressedSize;
            UncompressedSize = uncompressedSize;
            LocalOffset = localOffset;
            Name = name;
        }
    }

    private sealed class VerifiedPackageLease : IDisposable
    {
        private readonly string packageRoot;
        private readonly string rootPrefix;
        private readonly Dictionary<string, LockedFile> files;
        private readonly Dictionary<string, LockedDirectory> directories;
        private bool sealedSet;
        private bool disposed;

        public string Evidence { get; set; }

        public bool HasVerifiedManifestIdentity { get; private set; }

        public string ManifestSha256 { get; private set; }

        public int ManifestEntryCount { get; private set; }

        public int LockedFileCount
        {
            get { return files.Count; }
        }

        public VerifiedPackageLease(string packageRoot)
        {
            this.packageRoot = Path.GetFullPath(packageRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            rootPrefix = this.packageRoot + Path.DirectorySeparatorChar;
            files = new Dictionary<string, LockedFile>(StringComparer.Ordinal);
            directories = new Dictionary<string, LockedDirectory>(
                StringComparer.Ordinal);
            Evidence = string.Empty;
            ManifestSha256 = string.Empty;
        }

        public void SetVerifiedManifestIdentity(
            string manifestSha256,
            int manifestEntryCount)
        {
            ThrowIfDisposed();
            if (!sealedSet ||
                !IsUpperHexSha256(manifestSha256) ||
                manifestEntryCount <= 0)
            {
                throw new InvalidOperationException(
                    "The package manifest identity is not verified and canonical.");
            }
            ManifestSha256 = manifestSha256;
            ManifestEntryCount = manifestEntryCount;
            HasVerifiedManifestIdentity = true;
        }

        public void LockDirectories()
        {
            ThrowIfDisposed();
            if (directories.Count != 0)
            {
                throw new InvalidOperationException(
                    "The package directory set is already locked.");
            }

            LockDirectory(string.Empty, packageRoot);
            string[] paths = Directory.GetDirectories(
                packageRoot,
                "*",
                SearchOption.AllDirectories);
            Array.Sort(paths, StringComparer.Ordinal);
            for (int index = 0; index < paths.Length; index++)
            {
                string fullPath = Path.GetFullPath(paths[index]);
                if (!fullPath.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new AcceptanceFailure(
                        "Package directory enumeration escaped the release root.",
                        fullPath);
                }
                string relativePath = fullPath.Substring(rootPrefix.Length);
                if (!IsSafeCanonicalRelativePath(relativePath))
                {
                    throw new AcceptanceFailure(
                        "The package contains a non-canonical directory path.",
                        relativePath);
                }
                LockDirectory(relativePath, fullPath);
            }
        }

        public FileStream LockFile(string relativePath)
        {
            ThrowIfDisposed();
            if (sealedSet)
            {
                throw new InvalidOperationException(
                    "The verified package file set is already sealed.");
            }
            if (!IsSafeCanonicalRelativePath(relativePath) ||
                files.ContainsKey(relativePath))
            {
                throw new AcceptanceFailure(
                    "A package file lock path is unsafe or duplicated.",
                    relativePath);
            }

            string fullPath = ResolveRelativePath(relativePath);
            SafeFileHandle handle = NativeMethods.CreateFile(
                fullPath,
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new AcceptanceFailure(
                    "A package file could not be locked against concurrent modification.",
                    relativePath + "; Win32=" + error.ToString(
                        CultureInfo.InvariantCulture));
            }

            try
            {
                NativeMethods.ByHandleFileInformation information =
                    ReadHandleInformation(handle, relativePath);
                if ((information.FileAttributes &
                    (NativeMethods.FileAttributeDirectory |
                        NativeMethods.FileAttributeReparsePoint)) != 0)
                {
                    throw new AcceptanceFailure(
                        "A package payload path is not a regular file.",
                        relativePath);
                }
                FileStream stream = new FileStream(handle, FileAccess.Read);
                handle = null;
                LockedFile locked = new LockedFile(
                    relativePath,
                    fullPath,
                    FileIdentity.From(information),
                    stream);
                files.Add(relativePath, locked);
                return stream;
            }
            finally
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
            }
        }

        public void Seal(IEnumerable<string> expectedPayloadPaths)
        {
            ThrowIfDisposed();
            if (sealedSet)
            {
                throw new InvalidOperationException(
                    "The verified package file set is already sealed.");
            }
            foreach (string relativePath in expectedPayloadPaths)
            {
                if (!files.ContainsKey(relativePath))
                {
                    throw new AcceptanceFailure(
                        "A verified package payload does not have a persistent lock.",
                        relativePath);
                }
            }
            sealedSet = true;
        }

        public bool Contains(string relativePath)
        {
            ThrowIfDisposed();
            return relativePath != null && files.ContainsKey(relativePath);
        }

        public string GetVerifiedPath(string relativePath)
        {
            AssertSameIdentity(relativePath);
            return files[relativePath].FullPath;
        }

        public string GetRelativePath(string fullPath)
        {
            ThrowIfDisposed();
            string canonical = Path.GetFullPath(fullPath);
            if (!canonical.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            string relativePath = canonical.Substring(rootPrefix.Length);
            return IsSafeCanonicalRelativePath(relativePath)
                ? relativePath
                : string.Empty;
        }

        public string[] GetApplicationLibraryPaths()
        {
            ThrowIfDisposed();
            List<string> paths = new List<string>();
            foreach (KeyValuePair<string, LockedFile> item in files)
            {
                string relativePath = item.Key;
                if (relativePath.StartsWith(
                        @"app\",
                        StringComparison.Ordinal) &&
                    relativePath.EndsWith(
                        ".dll",
                        StringComparison.OrdinalIgnoreCase) &&
                    relativePath.IndexOf(
                        Path.DirectorySeparatorChar,
                        4) < 0)
                {
                    paths.Add(item.Value.FullPath);
                }
            }
            paths.Sort(StringComparer.Ordinal);
            return paths.ToArray();
        }

        public void AssertIntact()
        {
            ThrowIfDisposed();
            if (!sealedSet)
            {
                throw new InvalidOperationException(
                    "The verified package file set is not sealed.");
            }
            AssertDirectorySet();
            AssertFileSet();
            foreach (string relativePath in files.Keys)
            {
                AssertSameIdentity(relativePath);
            }
        }

        public void AssertSameIdentity(string relativePath)
        {
            ThrowIfDisposed();
            LockedFile expected;
            if (relativePath == null ||
                !files.TryGetValue(relativePath, out expected))
            {
                throw new AcceptanceFailure(
                    "An unverified package file was requested.",
                    relativePath ?? string.Empty);
            }

            SafeFileHandle handle = NativeMethods.CreateFile(
                expected.FullPath,
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new AcceptanceFailure(
                    "A verified package file path is no longer accessible.",
                    relativePath + "; Win32=" + error.ToString(
                        CultureInfo.InvariantCulture));
            }
            using (handle)
            {
                NativeMethods.ByHandleFileInformation information =
                    ReadHandleInformation(handle, relativePath);
                if (!expected.Identity.Equals(FileIdentity.From(information)) ||
                    (information.FileAttributes &
                        (NativeMethods.FileAttributeDirectory |
                            NativeMethods.FileAttributeReparsePoint)) != 0)
                {
                    throw new AcceptanceFailure(
                        "A verified package file identity changed after hashing.",
                        relativePath);
                }
            }
        }

        private void AssertDirectorySet()
        {
            Dictionary<string, bool> current = new Dictionary<string, bool>(
                StringComparer.Ordinal);
            current.Add(string.Empty, true);
            string[] paths = Directory.GetDirectories(
                packageRoot,
                "*",
                SearchOption.AllDirectories);
            for (int index = 0; index < paths.Length; index++)
            {
                string fullPath = Path.GetFullPath(paths[index]);
                if (!fullPath.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new AcceptanceFailure(
                        "Package directory enumeration escaped the release root.",
                        fullPath);
                }
                string relativePath = fullPath.Substring(rootPrefix.Length);
                current.Add(relativePath, true);
            }
            if (current.Count != directories.Count)
            {
                throw new AcceptanceFailure(
                    "The package directory set changed after verification.",
                    "expected=" + directories.Count.ToString(
                        CultureInfo.InvariantCulture) +
                        "; actual=" + current.Count.ToString(
                            CultureInfo.InvariantCulture));
            }
            foreach (KeyValuePair<string, LockedDirectory> item in directories)
            {
                if (!current.ContainsKey(item.Key))
                {
                    throw new AcceptanceFailure(
                        "A verified package directory disappeared.",
                        item.Key);
                }
                using (SafeFileHandle handle = OpenDirectoryHandle(
                    item.Value.FullPath,
                    item.Key))
                {
                    FileIdentity actual = FileIdentity.From(
                        ReadHandleInformation(handle, item.Key));
                    if (!item.Value.Identity.Equals(actual))
                    {
                        throw new AcceptanceFailure(
                            "A package directory identity changed after verification.",
                            item.Key);
                    }
                }
            }
        }

        private void AssertFileSet()
        {
            string[] paths = Directory.GetFiles(
                packageRoot,
                "*",
                SearchOption.AllDirectories);
            if (paths.Length != files.Count)
            {
                throw new AcceptanceFailure(
                    "The package file set changed after verification.",
                    "expected=" + files.Count.ToString(
                        CultureInfo.InvariantCulture) +
                        "; actual=" + paths.Length.ToString(
                            CultureInfo.InvariantCulture));
            }
            for (int index = 0; index < paths.Length; index++)
            {
                string relativePath = GetRelativePath(paths[index]);
                if (!files.ContainsKey(relativePath))
                {
                    throw new AcceptanceFailure(
                        "The package contains a file added after verification.",
                        relativePath);
                }
            }
        }

        private void LockDirectory(string relativePath, string fullPath)
        {
            SafeFileHandle handle = OpenDirectoryHandle(fullPath, relativePath);
            try
            {
                NativeMethods.ByHandleFileInformation information =
                    ReadHandleInformation(handle, relativePath);
                if ((information.FileAttributes &
                    NativeMethods.FileAttributeDirectory) == 0 ||
                    (information.FileAttributes &
                    NativeMethods.FileAttributeReparsePoint) != 0)
                {
                    throw new AcceptanceFailure(
                        "A package directory path is invalid or a reparse point.",
                        relativePath);
                }
                directories.Add(
                    relativePath,
                    new LockedDirectory(
                        fullPath,
                        FileIdentity.From(information),
                        handle));
                handle = null;
            }
            finally
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
            }
        }

        private static SafeFileHandle OpenDirectoryHandle(
            string fullPath,
            string evidence)
        {
            SafeFileHandle handle = NativeMethods.CreateFile(
                fullPath,
                NativeMethods.FileReadAttributes,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagBackupSemantics |
                    NativeMethods.FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new AcceptanceFailure(
                    "A package directory could not be locked against replacement.",
                    evidence + "; Win32=" + error.ToString(
                        CultureInfo.InvariantCulture));
            }
            return handle;
        }

        private string ResolveRelativePath(string relativePath)
        {
            string fullPath = Path.GetFullPath(Path.Combine(
                packageRoot,
                relativePath));
            if (!fullPath.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    fullPath.Substring(rootPrefix.Length),
                    relativePath,
                    StringComparison.Ordinal))
            {
                throw new AcceptanceFailure(
                    "A package path does not resolve canonically.",
                    relativePath);
            }
            return fullPath;
        }

        private static NativeMethods.ByHandleFileInformation
            ReadHandleInformation(SafeFileHandle handle, string evidence)
        {
            NativeMethods.ByHandleFileInformation information;
            if (!NativeMethods.GetFileInformationByHandle(
                handle,
                out information))
            {
                throw new AcceptanceFailure(
                    "A package file identity could not be read.",
                    evidence + "; Win32=" + Marshal.GetLastWin32Error().ToString(
                        CultureInfo.InvariantCulture));
            }
            return information;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("VerifiedPackageLease");
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (LockedFile file in files.Values)
            {
                file.Stream.Dispose();
            }
            foreach (LockedDirectory directory in directories.Values)
            {
                directory.Handle.Dispose();
            }
            files.Clear();
            directories.Clear();
        }

        private sealed class LockedFile
        {
            public string RelativePath { get; private set; }
            public string FullPath { get; private set; }
            public FileIdentity Identity { get; private set; }
            public FileStream Stream { get; private set; }

            public LockedFile(
                string relativePath,
                string fullPath,
                FileIdentity identity,
                FileStream stream)
            {
                RelativePath = relativePath;
                FullPath = fullPath;
                Identity = identity;
                Stream = stream;
            }
        }

        private sealed class LockedDirectory
        {
            public string FullPath { get; private set; }
            public FileIdentity Identity { get; private set; }
            public SafeFileHandle Handle { get; private set; }

            public LockedDirectory(
                string fullPath,
                FileIdentity identity,
                SafeFileHandle handle)
            {
                FullPath = fullPath;
                Identity = identity;
                Handle = handle;
            }
        }

        private struct FileIdentity
        {
            private uint volumeSerialNumber;
            private uint fileIndexHigh;
            private uint fileIndexLow;

            public static FileIdentity From(
                NativeMethods.ByHandleFileInformation information)
            {
                FileIdentity identity = new FileIdentity();
                identity.volumeSerialNumber = information.VolumeSerialNumber;
                identity.fileIndexHigh = information.FileIndexHigh;
                identity.fileIndexLow = information.FileIndexLow;
                return identity;
            }

            public override bool Equals(object value)
            {
                if (!(value is FileIdentity))
                {
                    return false;
                }
                FileIdentity other = (FileIdentity)value;
                return volumeSerialNumber == other.volumeSerialNumber &&
                    fileIndexHigh == other.fileIndexHigh &&
                    fileIndexLow == other.fileIndexLow;
            }

            public override int GetHashCode()
            {
                return (int)(volumeSerialNumber ^ fileIndexHigh ^ fileIndexLow);
            }
        }
    }

    private sealed class ApplicationRuntime
    {
        private readonly string applicationDirectory;
        private readonly VerifiedPackageLease verifiedPackage;

        public string ApplicationPath { get; private set; }
        public Assembly ApplicationAssembly { get; private set; }
        public Type ModelClientType { get; private set; }
        public Type ModelRequestType { get; private set; }
        public Type InputItemType { get; private set; }
        public Type ConversationMessageType { get; private set; }
        public Type ExtractorType { get; private set; }

        public ApplicationRuntime(
            string packageRoot,
            VerifiedPackageLease verifiedPackage)
        {
            if (verifiedPackage == null)
            {
                throw new ArgumentNullException("verifiedPackage");
            }

            this.verifiedPackage = verifiedPackage;
            verifiedPackage.AssertIntact();
            ApplicationPath = verifiedPackage.GetVerifiedPath(
                @"app\FilePromptAI.exe");
            applicationDirectory = Path.GetDirectoryName(ApplicationPath);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveApplicationAssembly;
            verifiedPackage.AssertSameIdentity(@"app\FilePromptAI.exe");
            ApplicationAssembly = Assembly.LoadFrom(ApplicationPath);
            PreloadApplicationLibraries();
            ModelClientType = RequireApplicationType("FilePromptAIWin7.ModelClient");
            ModelRequestType = RequireApplicationType("FilePromptAIWin7.ModelRequest");
            InputItemType = RequireApplicationType("FilePromptAIWin7.InputItem");
            ConversationMessageType = RequireApplicationType(
                "FilePromptAIWin7.ConversationMessage");
            ExtractorType = RequireApplicationType(
                "FilePromptAIWin7.FileContentExtractor");
        }

        private void PreloadApplicationLibraries()
        {
            string[] libraries = verifiedPackage.GetApplicationLibraryPaths();
            for (int index = 0; index < libraries.Length; index++)
            {
                try
                {
                    verifiedPackage.AssertSameIdentity(
                        verifiedPackage.GetRelativePath(libraries[index]));
                    Assembly.LoadFrom(libraries[index]);
                }
                catch (AcceptanceFailure)
                {
                    throw;
                }
                catch
                {
                    // Optional assemblies are resolved again if their feature needs them.
                }
            }
        }

        public Type RequireApplicationType(string name)
        {
            Type type = ApplicationAssembly.GetType(name, false);
            if (type == null)
            {
                throw new TypeLoadException(
                    "Required application type was not found: " + name);
            }
            return type;
        }

        private Assembly ResolveApplicationAssembly(
            object sender,
            ResolveEventArgs eventArgs)
        {
            string simpleName = new AssemblyName(eventArgs.Name).Name;
            if (string.IsNullOrEmpty(simpleName) ||
                simpleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }
            string candidate = Path.Combine(
                applicationDirectory,
                simpleName + ".dll");
            string relativePath = verifiedPackage.GetRelativePath(candidate);
            if (!verifiedPackage.Contains(relativePath) ||
                !relativePath.StartsWith(
                    @"app\",
                    StringComparison.Ordinal))
            {
                return null;
            }
            verifiedPackage.AssertSameIdentity(relativePath);
            return Assembly.LoadFrom(candidate);
        }
    }

    private sealed class LoopbackResponse
    {
        public string ContentType { get; private set; }
        public string Body { get; private set; }

        private LoopbackResponse(string contentType, string body)
        {
            ContentType = contentType;
            Body = body;
        }

        public static LoopbackResponse Models(string nonce)
        {
            return new LoopbackResponse(
                "application/json; charset=utf-8",
                "{\"data\":[{\"id\":\"z-local-model\"}," +
                    "{\"id\":\"acceptance-" + nonce + "\"}," +
                    "{\"id\":\"acceptance-" + nonce + "\"}]}");
        }

        public static LoopbackResponse Chat(string responseText)
        {
            string escaped = JsonEscape(responseText);
            return new LoopbackResponse(
                "text/event-stream; charset=utf-8",
                "data: {\"choices\":[{\"delta\":{\"content\":\"" +
                    escaped + "\"}}]}\r\n\r\n" +
                    "data: [DONE]\r\n\r\n");
        }

        private static string JsonEscape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly LoopbackResponse response;
        private readonly ManualResetEvent completed;
        private TcpListener listener;
        private Thread worker;
        private string rawRequest;
        private Exception workerError;

        public string BaseUrl { get; private set; }

        public LoopbackServer(LoopbackResponse responseValue)
        {
            response = responseValue;
            completed = new ManualResetEvent(false);
            rawRequest = string.Empty;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            BaseUrl = "http://127.0.0.1:" +
                port.ToString(CultureInfo.InvariantCulture) + "/";
            worker = new Thread(new ThreadStart(ServeOneRequest));
            worker.IsBackground = true;
            worker.Name = "FilePromptAI acceptance loopback";
            worker.Start();
        }

        public string WaitForRequest(int timeoutMilliseconds)
        {
            if (!completed.WaitOne(timeoutMilliseconds, false))
            {
                throw new TimeoutException(
                    "The local acceptance server did not receive a request.");
            }
            if (workerError != null)
            {
                throw new InvalidOperationException(
                    "The local acceptance server failed.",
                    workerError);
            }
            return rawRequest;
        }

        private void ServeOneRequest()
        {
            TcpClient connection = null;
            try
            {
                connection = listener.AcceptTcpClient();
                connection.ReceiveTimeout = 10000;
                connection.SendTimeout = 10000;
                NetworkStream stream = connection.GetStream();
                rawRequest = ReadHttpRequest(stream);
                byte[] body = Encoding.UTF8.GetBytes(response.Body);
                string headers =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: " + response.ContentType + "\r\n" +
                    "Content-Length: " + body.Length.ToString(
                        CultureInfo.InvariantCulture) + "\r\n" +
                    "Connection: close\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(body, 0, body.Length);
                stream.Flush();
            }
            catch (Exception exception)
            {
                workerError = exception;
            }
            finally
            {
                if (connection != null)
                {
                    connection.Close();
                }
                completed.Set();
            }
        }

        private static string ReadHttpRequest(NetworkStream stream)
        {
            MemoryStream bytes = new MemoryStream();
            byte[] buffer = new byte[4096];
            int headerEnd = -1;
            int contentLength = 0;
            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                bytes.Write(buffer, 0, read);
                if (bytes.Length > 4 * 1024 * 1024)
                {
                    throw new InvalidDataException(
                        "Acceptance request headers exceeded 4 MB.");
                }
                headerEnd = FindHeaderEnd(bytes.GetBuffer(), (int)bytes.Length);
            }

            if (headerEnd >= 0)
            {
                string headerText = Encoding.ASCII.GetString(
                    bytes.GetBuffer(),
                    0,
                    headerEnd + 4);
                string[] lines = headerText.Split(new string[] { "\r\n" },
                    StringSplitOptions.None);
                for (int index = 0; index < lines.Length; index++)
                {
                    if (lines[index].StartsWith(
                        "Content-Length:",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(
                            lines[index].Substring(15).Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out contentLength);
                    }
                }
            }

            int requiredLength = headerEnd < 0
                ? (int)bytes.Length
                : headerEnd + 4 + contentLength;
            if (requiredLength > 32 * 1024 * 1024)
            {
                throw new InvalidDataException(
                    "Acceptance request body exceeded 32 MB.");
            }
            while (bytes.Length < requiredLength)
            {
                int read = stream.Read(buffer, 0, Math.Min(
                    buffer.Length,
                    requiredLength - (int)bytes.Length));
                if (read <= 0)
                {
                    break;
                }
                bytes.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(
                bytes.GetBuffer(),
                0,
                (int)bytes.Length);
        }

        private static int FindHeaderEnd(byte[] bytes, int length)
        {
            for (int index = 0; index <= length - 4; index++)
            {
                if (bytes[index] == 13 && bytes[index + 1] == 10 &&
                    bytes[index + 2] == 13 && bytes[index + 3] == 10)
                {
                    return index;
                }
            }
            return -1;
        }

        public void Dispose()
        {
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                }
            }
            if (worker != null && worker.IsAlive)
            {
                worker.Join(1000);
            }
            completed.Close();
        }
    }

    private sealed class CheckResult
    {
        public string Identifier { get; private set; }
        public string Status { get; private set; }
        public string Message { get; private set; }
        public string Evidence { get; private set; }
        public long DurationMilliseconds { get; private set; }

        public CheckResult(
            string identifier,
            string status,
            string message,
            string evidence,
            long durationMilliseconds)
        {
            Identifier = identifier;
            Status = status;
            Message = message;
            Evidence = evidence;
            DurationMilliseconds = durationMilliseconds;
        }
    }

    private sealed class ExtractionExpectation
    {
        public string Name { get; private set; }
        public string Kind { get; private set; }
        public string TextMarker { get; private set; }
        public bool Binary { get; private set; }

        public ExtractionExpectation(
            string name,
            string kind,
            string textMarker,
            bool binary)
        {
            Name = name;
            Kind = kind;
            TextMarker = textMarker;
            Binary = binary;
        }
    }

    private sealed class AcceptanceFailure : Exception
    {
        public string Evidence { get; private set; }

        public AcceptanceFailure(string message, string evidence)
            : base(message)
        {
            Evidence = evidence ?? string.Empty;
        }
    }

    private sealed class NativeVersion
    {
        public int Major { get; private set; }
        public int Minor { get; private set; }
        public int Build { get; private set; }
        public int ServicePackMajor { get; private set; }
        public int ServicePackMinor { get; private set; }
        public int ProductType { get; private set; }

        public static NativeVersion Read()
        {
            NativeMethods.OsVersionInfoEx info =
                new NativeMethods.OsVersionInfoEx();
            info.Size = Marshal.SizeOf(typeof(NativeMethods.OsVersionInfoEx));
            int status = NativeMethods.RtlGetVersion(ref info);
            if (status != 0)
            {
                throw new AcceptanceFailure(
                    "RtlGetVersion could not read the true Windows kernel version.",
                    "NTSTATUS=" + status.ToString(CultureInfo.InvariantCulture));
            }

            NativeVersion result = new NativeVersion();
            result.Major = info.MajorVersion;
            result.Minor = info.MinorVersion;
            result.Build = info.BuildNumber;
            result.ServicePackMajor = info.ServicePackMajor;
            result.ServicePackMinor = info.ServicePackMinor;
            result.ProductType = info.ProductType;
            return result;
        }
    }

    private sealed class DisplayEvidence
    {
        public bool DpiAware { get; private set; }
        public int MetricsWidth { get; private set; }
        public int MetricsHeight { get; private set; }
        public int ModeWidth { get; private set; }
        public int ModeHeight { get; private set; }
        public int DpiX { get; private set; }
        public int DpiY { get; private set; }

        public static DisplayEvidence Read()
        {
            DisplayEvidence evidence = new DisplayEvidence();
            evidence.DpiAware = NativeMethods.IsProcessDPIAware();
            evidence.MetricsWidth = NativeMethods.GetSystemMetrics(
                NativeMethods.SmCxScreen);
            evidence.MetricsHeight = NativeMethods.GetSystemMetrics(
                NativeMethods.SmCyScreen);

            NativeMethods.DeviceMode mode = new NativeMethods.DeviceMode();
            mode.Size = (short)Marshal.SizeOf(typeof(NativeMethods.DeviceMode));
            if (!NativeMethods.EnumDisplaySettings(
                null,
                NativeMethods.EnumCurrentSettings,
                ref mode))
            {
                throw new AcceptanceFailure(
                    "EnumDisplaySettings could not read the primary display mode.",
                    string.Empty);
            }
            evidence.ModeWidth = mode.PelsWidth;
            evidence.ModeHeight = mode.PelsHeight;

            IntPtr deviceContext = NativeMethods.GetDC(IntPtr.Zero);
            if (deviceContext == IntPtr.Zero)
            {
                throw new AcceptanceFailure(
                    "GetDC could not open the primary display context.",
                    string.Empty);
            }
            try
            {
                evidence.DpiX = NativeMethods.GetDeviceCaps(
                    deviceContext,
                    NativeMethods.LogPixelsX);
                evidence.DpiY = NativeMethods.GetDeviceCaps(
                    deviceContext,
                    NativeMethods.LogPixelsY);
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, deviceContext);
            }
            return evidence;
        }

        public override string ToString()
        {
            return "aware=" + DpiAware +
                "; metrics=" + MetricsWidth.ToString(CultureInfo.InvariantCulture) +
                "x" + MetricsHeight.ToString(CultureInfo.InvariantCulture) +
                "; mode=" + ModeWidth.ToString(CultureInfo.InvariantCulture) +
                "x" + ModeHeight.ToString(CultureInfo.InvariantCulture) +
                "; dpi=" + DpiX.ToString(CultureInfo.InvariantCulture) +
                "x" + DpiY.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint FileReadAttributes = 0x00000080;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint FileShareDelete = 0x00000004;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint FileFlagOpenReparsePoint = 0x00200000;
        internal const uint FileAttributeDirectory = 0x00000010;
        internal const uint FileAttributeReparsePoint = 0x00000400;
        internal const int SmCxScreen = 0;
        internal const int SmCyScreen = 1;
        internal const int LogPixelsX = 88;
        internal const int LogPixelsY = 90;
        internal const int EnumCurrentSettings = -1;
        internal const int WmClose = 0x0010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct OsVersionInfoEx
        {
            public int Size;
            public int MajorVersion;
            public int MinorVersion;
            public int BuildNumber;
            public int PlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string CsdVersion;
            public short ServicePackMajor;
            public short ServicePackMinor;
            public short SuiteMask;
            public byte ProductType;
            public byte Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DeviceMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public short SpecVersion;
            public short DriverVersion;
            public short Size;
            public short DriverExtra;
            public int Fields;
            public int PositionX;
            public int PositionY;
            public int DisplayOrientation;
            public int DisplayFixedOutput;
            public short Color;
            public short Duplex;
            public short YResolution;
            public short TTOption;
            public short Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FormName;
            public short LogPixels;
            public int BitsPerPel;
            public int PelsWidth;
            public int PelsHeight;
            public int DisplayFlags;
            public int DisplayFrequency;
            public int ICMMethod;
            public int ICMIntent;
            public int MediaType;
            public int DitherType;
            public int Reserved1;
            public int Reserved2;
            public int PanningWidth;
            public int PanningHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        internal static extern int RtlGetVersion(ref OsVersionInfoEx info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessDPIAware();

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplaySettings(
            string deviceName,
            int modeNumber,
            ref DeviceMode mode);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int ReleaseDC(IntPtr window, IntPtr context);

        [DllImport("gdi32.dll")]
        internal static extern int GetDeviceCaps(IntPtr context, int index);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            IntPtr window,
            int message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWow64Process(
            IntPtr process,
            out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(
            IntPtr process,
            out uint exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);
    }
}
