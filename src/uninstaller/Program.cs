using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FilePromptAIUninstaller
{
    internal static class Program
    {
        private const string ProductName = "FilePrompt AI";
        private const string ManifestFileName = "PACKAGE-CHECKSUMS-SHA256.txt";
        private const string UninstallerFileName = "Uninstall-FilePromptAI.exe";
        private const string LauncherFileName = "Start-FilePromptAI.exe";
        private const string ApplicationRelativePath = "app\\FilePromptAI.exe";
        private const string DataDirectoryName = "FilePromptAI-Win7";
        private const int MoveFileDelayUntilReboot = 0x4;
        private const uint DeleteAccess = 0x00010000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const int FileDispositionInfoClass = 4;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorDirectoryNotEmpty = 145;
        private const int MaximumManifestBytes = 4 * 1024 * 1024;
        private const uint CreateNoWindow = 0x08000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int informationClass,
            ref FileDispositionInformation information,
            uint bufferSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInformation startupInformation,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public NativeFileTime CreationTime;
            public NativeFileTime LastAccessTime;
            public NativeFileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool DeleteFile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInformation
        {
            public int Size;
            public string Reserved;
            public string Desktop;
            public string Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public short ShowWindow;
            public short Reserved2Size;
            public IntPtr Reserved2;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if ((args.Length == 6 || args.Length == 7) &&
                string.Equals(args[0], "--execute", StringComparison.Ordinal))
            {
                bool silent = args.Length == 7 &&
                    string.Equals(
                        args[6],
                        "--silent",
                        StringComparison.Ordinal);
                if (args.Length == 7 && !silent)
                {
                    Environment.ExitCode = 2;
                    return;
                }

                RunWorker(args, silent);
                return;
            }

            if (args.Length == 1 &&
                string.Equals(args[0], "--check", StringComparison.Ordinal))
            {
                RunCheck();
                return;
            }

            RunInteractive();
        }

        private static void RunCheck()
        {
            string releaseRoot;
            string rootError;
            if (!TryNormalizeReleaseRoot(
                AppDomain.CurrentDomain.BaseDirectory,
                out releaseRoot,
                out rootError))
            {
                Environment.ExitCode = 2;
                return;
            }

            List<ManifestEntry> entries;
            string manifestError;
            string manifestHash;
            Environment.ExitCode = TryReadManifest(
                releaseRoot,
                out entries,
                out manifestHash,
                out manifestError)
                ? 0
                : 3;
        }

        private static void RunInteractive()
        {
            string releaseRoot;
            string rootError;
            if (!TryNormalizeReleaseRoot(
                AppDomain.CurrentDomain.BaseDirectory,
                out releaseRoot,
                out rootError))
            {
                ShowError(rootError);
                return;
            }

            List<ManifestEntry> entries;
            string manifestError;
            string manifestHash;
            if (!TryReadManifest(
                releaseRoot,
                out entries,
                out manifestHash,
                out manifestError))
            {
                ShowError(manifestError);
                return;
            }

            bool deleteData;
            using (UninstallForm form = new UninstallForm(releaseRoot))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                deleteData = form.DeleteData;
            }

            if (deleteData)
            {
                DialogResult dataAnswer = MessageBox.Show(
                    "确定同时删除当前 Windows 用户的本地配置、会话、技能和 MCP 配置吗？\r\n\r\n" +
                    "数据目录：\r\n" + GetExpectedDataRoot() + "\r\n\r\n" +
                    "此操作无法撤销。选择“否”将继续卸载程序，但保留用户数据。",
                    "再次确认删除用户数据",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                deleteData = dataAnswer == DialogResult.Yes;
            }

            string startError;
            if (!StartTemporaryWorker(releaseRoot, deleteData, out startError))
            {
                ShowError(startError);
            }
        }

        private static bool StartTemporaryWorker(
            string releaseRoot,
            bool deleteData,
            out string error)
        {
            error = string.Empty;
            string temporaryRoot = string.Empty;
            try
            {
                temporaryRoot = Path.Combine(
                    Path.GetTempPath(),
                    "FilePromptAI-Uninstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryRoot);

                string sourceExe = Path.GetFullPath(Application.ExecutablePath);
                string temporaryExe = Path.Combine(
                    temporaryRoot,
                    UninstallerFileName);
                File.Copy(sourceExe, temporaryExe, false);

                string sourceConfig = sourceExe + ".config";
                if (File.Exists(sourceConfig))
                {
                    File.Copy(
                        sourceConfig,
                        temporaryExe + ".config",
                        false);
                }

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = temporaryExe;
                startInfo.WorkingDirectory = temporaryRoot;
                startInfo.UseShellExecute = false;
                startInfo.Arguments =
                    "--execute " + QuoteArgument(releaseRoot) +
                    " --delete-data " + (deleteData ? "true" : "false") +
                    " --parent-pid " +
                    Process.GetCurrentProcess().Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                Process process = Process.Start(startInfo);
                if (process == null)
                {
                    throw new InvalidOperationException("无法启动临时卸载进程。");
                }

                process.Dispose();
                return true;
            }
            catch (Exception exception)
            {
                TryDeleteUnstartedTemporaryCopy(temporaryRoot);
                error = "无法准备卸载程序：\r\n\r\n" + exception.Message;
                return false;
            }
        }

        private static void RunWorker(string[] args, bool silent)
        {
            if (!IsValidTemporaryWorkerLocation())
            {
                ShowWorkerError(
                    "内部卸载参数只能由确认后的临时卸载进程使用，未删除任何文件。",
                    silent,
                    2);
                return;
            }

            try
            {
                RunWorkerCore(args, silent);
            }
            finally
            {
                ScheduleTemporaryCleanup();
            }
        }

        private static void RunWorkerCore(string[] args, bool silent)
        {
            string releaseRoot;
            string rootError;
            if (!TryNormalizeReleaseRoot(args[1], out releaseRoot, out rootError))
            {
                ShowWorkerError(rootError, silent, 2);
                return;
            }

            if (!string.Equals(args[2], "--delete-data", StringComparison.Ordinal) ||
                !string.Equals(args[4], "--parent-pid", StringComparison.Ordinal))
            {
                ShowWorkerError(
                    "卸载参数无效，未删除任何文件。",
                    silent,
                    2);
                return;
            }

            bool deleteData;
            if (!bool.TryParse(args[3], out deleteData))
            {
                ShowWorkerError(
                    "用户数据选项无效，未删除任何文件。",
                    silent,
                    2);
                return;
            }

            int parentProcessId;
            if (!int.TryParse(
                args[5],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out parentProcessId) ||
                parentProcessId <= 0)
            {
                ShowWorkerError(
                    "父进程参数无效，未删除任何文件。",
                    silent,
                    2);
                return;
            }

            WaitForParent(parentProcessId);

            List<ManifestEntry> entries;
            string manifestError;
            string manifestHash;
            if (!TryReadManifest(
                releaseRoot,
                out entries,
                out manifestHash,
                out manifestError))
            {
                ShowWorkerError(manifestError, silent, 3);
                return;
            }

            UninstallResult result = DeleteReleaseFiles(
                releaseRoot,
                entries,
                manifestHash);
            if (deleteData)
            {
                DeleteUserData(result);
            }

            if (silent)
            {
                Environment.ExitCode = result.FailedFiles == 0 &&
                    result.ModifiedFiles == 0 &&
                    !result.DataDeletionFailed
                    ? 0
                    : 4;
            }
            else
            {
                ShowResult(result, releaseRoot);
            }
        }

        private static void TryDeleteUnstartedTemporaryCopy(string directory)
        {
            if (!IsOwnedTemporaryDirectory(
                directory,
                "FilePromptAI-Uninstall-"))
            {
                return;
            }

            try
            {
                string executable = Path.Combine(
                    directory,
                    UninstallerFileName);
                string configuration = executable + ".config";
                if (File.Exists(configuration))
                {
                    File.Delete(configuration);
                }

                if (File.Exists(executable))
                {
                    File.Delete(executable);
                }

                Directory.Delete(directory, false);
            }
            catch
            {
                // The cleanup scheduler will handle copies that actually started.
            }
        }

        private static void ShowWorkerError(
            string message,
            bool silent,
            int exitCode)
        {
            Environment.ExitCode = exitCode;
            if (!silent)
            {
                ShowError(message);
            }
        }

        private static void WaitForParent(int processId)
        {
            try
            {
                using (Process parent = Process.GetProcessById(processId))
                {
                    parent.WaitForExit(30000);
                }
            }
            catch
            {
                // The original process may already have exited.
            }
        }

        private static bool IsValidTemporaryWorkerLocation()
        {
            try
            {
                const string prefix = "FilePromptAI-Uninstall-";
                string workerDirectory = TrimTrailingSeparators(
                    Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory));
                if (!IsOwnedTemporaryDirectory(workerDirectory, prefix) ||
                    (File.GetAttributes(workerDirectory) &
                        FileAttributes.ReparsePoint) != 0 ||
                    !string.Equals(
                        Path.GetFileName(Application.ExecutablePath),
                        UninstallerFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsOwnedTemporaryDirectory(
            string directory,
            string prefix)
        {
            try
            {
                if (string.IsNullOrEmpty(directory) ||
                    string.IsNullOrEmpty(prefix))
                {
                    return false;
                }

                string temporaryBase = TrimTrailingSeparators(
                    Path.GetFullPath(Path.GetTempPath()));
                string fullDirectory = TrimTrailingSeparators(
                    Path.GetFullPath(directory));
                if (!IsStrictChildPath(temporaryBase, fullDirectory))
                {
                    return false;
                }

                string directoryName = Path.GetFileName(fullDirectory);
                if (directoryName == null ||
                    !directoryName.StartsWith(prefix, StringComparison.Ordinal) ||
                    directoryName.Length != prefix.Length + 32)
                {
                    return false;
                }

                int index;
                for (index = prefix.Length; index < directoryName.Length; index++)
                {
                    char value = directoryName[index];
                    bool isHex =
                        (value >= '0' && value <= '9') ||
                        (value >= 'a' && value <= 'f') ||
                        (value >= 'A' && value <= 'F');
                    if (!isHex)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryNormalizeReleaseRoot(
            string value,
            out string releaseRoot,
            out string error)
        {
            releaseRoot = string.Empty;
            error = string.Empty;
            try
            {
                if (string.IsNullOrEmpty(value))
                {
                    throw new InvalidOperationException("发布目录不能为空。");
                }

                string fullPath = Path.GetFullPath(value);
                fullPath = TrimTrailingSeparators(fullPath);
                string pathRoot = TrimTrailingSeparators(
                    Path.GetPathRoot(fullPath));
                if (string.Equals(
                    fullPath,
                    pathRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "为安全起见，不能从磁盘或共享根目录执行卸载。");
                }

                if (!Directory.Exists(fullPath))
                {
                    throw new DirectoryNotFoundException(
                        "发布目录不存在：" + fullPath);
                }

                if ((File.GetAttributes(fullPath) &
                    FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "发布根目录是重解析点，为避免路径跳转，卸载已停止。");
                }

                string manifestPath = Path.GetFullPath(
                    Path.Combine(fullPath, ManifestFileName));
                if (!IsStrictChildPath(fullPath, manifestPath))
                {
                    throw new InvalidOperationException("校验清单路径越界。");
                }

                releaseRoot = fullPath;
                return true;
            }
            catch (Exception exception)
            {
                error = "无法确认安全的发布目录：\r\n\r\n" + exception.Message;
                return false;
            }
        }

        private static bool TryReadManifest(
            string releaseRoot,
            out List<ManifestEntry> entries,
            out string manifestHash,
            out string error)
        {
            entries = new List<ManifestEntry>();
            manifestHash = string.Empty;
            error = string.Empty;
            try
            {
                string manifestPath = Path.GetFullPath(
                    Path.Combine(releaseRoot, ManifestFileName));
                if (!IsStrictChildPath(releaseRoot, manifestPath) ||
                    !File.Exists(manifestPath))
                {
                    throw new FileNotFoundException(
                        "发布目录中缺少 " + ManifestFileName +
                        "，为避免误删，卸载已停止。");
                }

                byte[] manifestBytes = File.ReadAllBytes(manifestPath);
                if (manifestBytes.Length == 0 ||
                    manifestBytes.Length > MaximumManifestBytes)
                {
                    throw new InvalidDataException(
                        "校验清单为空或超过安全大小限制。");
                }

                manifestHash = ComputeSha256(manifestBytes);
                string manifestText;
                using (MemoryStream memory = new MemoryStream(
                    manifestBytes,
                    false))
                using (StreamReader reader = new StreamReader(
                    memory,
                    new UTF8Encoding(false, true),
                    true))
                {
                    manifestText = reader.ReadToEnd();
                }

                string[] lines = manifestText
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Split(new char[] { '\n' });
                Dictionary<string, bool> uniquePaths =
                    new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                int lineNumber;
                for (lineNumber = 0; lineNumber < lines.Length; lineNumber++)
                {
                    string line = lines[lineNumber];
                    if (line == null || line.Trim().Length == 0)
                    {
                        continue;
                    }

                    ManifestEntry entry = ParseManifestLine(
                        releaseRoot,
                        line,
                        lineNumber + 1);
                    if (uniquePaths.ContainsKey(entry.FullPath))
                    {
                        throw new InvalidDataException(
                            "校验清单第 " + (lineNumber + 1).ToString() +
                            " 行包含重复路径。");
                    }

                    uniquePaths.Add(entry.FullPath, true);
                    entries.Add(entry);
                }

                if (entries.Count == 0)
                {
                    throw new InvalidDataException("校验清单中没有可验证的文件记录。");
                }

                RequireManifestEntry(
                    releaseRoot,
                    entries,
                    ApplicationRelativePath);
                RequireManifestEntry(
                    releaseRoot,
                    entries,
                    LauncherFileName);
                RequireManifestEntry(
                    releaseRoot,
                    entries,
                    UninstallerFileName);

                return true;
            }
            catch (Exception exception)
            {
                entries.Clear();
                manifestHash = string.Empty;
                error = "无法验证卸载清单，未删除任何文件：\r\n\r\n" +
                    exception.Message;
                return false;
            }
        }

        private static void RequireManifestEntry(
            string releaseRoot,
            List<ManifestEntry> entries,
            string relativePath)
        {
            string expected = Path.GetFullPath(
                Path.Combine(releaseRoot, relativePath));
            int index;
            for (index = 0; index < entries.Count; index++)
            {
                if (string.Equals(
                    entries[index].FullPath,
                    expected,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            throw new InvalidDataException(
                "校验清单缺少产品身份文件：" + relativePath);
        }

        private static ManifestEntry ParseManifestLine(
            string releaseRoot,
            string line,
            int lineNumber)
        {
            if (line.Length < 66)
            {
                throw InvalidManifestLine(lineNumber);
            }

            string hash = line.Substring(0, 64);
            int index;
            for (index = 0; index < hash.Length; index++)
            {
                char value = hash[index];
                bool isHex =
                    (value >= '0' && value <= '9') ||
                    (value >= 'a' && value <= 'f') ||
                    (value >= 'A' && value <= 'F');
                if (!isHex)
                {
                    throw InvalidManifestLine(lineNumber);
                }
            }

            index = 64;
            if (index >= line.Length ||
                (line[index] != ' ' && line[index] != '\t'))
            {
                throw InvalidManifestLine(lineNumber);
            }

            while (index < line.Length &&
                (line[index] == ' ' || line[index] == '\t'))
            {
                index++;
            }

            if (index < line.Length && line[index] == '*')
            {
                index++;
            }

            if (index >= line.Length)
            {
                throw InvalidManifestLine(lineNumber);
            }

            string relativePath = line.Substring(index);
            if (relativePath.Length == 0 ||
                relativePath != relativePath.Trim() ||
                Path.IsPathRooted(relativePath) ||
                relativePath.IndexOf(':') >= 0 ||
                relativePath.IndexOf('\0') >= 0)
            {
                throw InvalidManifestLine(lineNumber);
            }

            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(
                Path.Combine(releaseRoot, relativePath));
            if (!IsStrictChildPath(releaseRoot, fullPath) ||
                string.Equals(
                    fullPath,
                    Path.Combine(releaseRoot, ManifestFileName),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "校验清单第 " + lineNumber.ToString() +
                    " 行的路径超出发布目录或指向清单自身。");
            }

            ManifestEntry entry = new ManifestEntry();
            entry.ExpectedHash = hash.ToUpperInvariant();
            entry.RelativePath = relativePath;
            entry.FullPath = fullPath;
            return entry;
        }

        private static InvalidDataException InvalidManifestLine(int lineNumber)
        {
            return new InvalidDataException(
                "校验清单第 " + lineNumber.ToString() + " 行格式无效。");
        }

        private static UninstallResult DeleteReleaseFiles(
            string releaseRoot,
            List<ManifestEntry> entries,
            string manifestHash)
        {
            UninstallResult result = new UninstallResult();
            List<ManifestEntry> controlEntries = new List<ManifestEntry>();
            List<string> candidateDirectories = new List<string>();
            string canonicalRoot = string.Empty;
            SafeFileHandle rootHandle = null;

            try
            {
                int rootError;
                rootHandle = OpenNativePath(
                    releaseRoot,
                    true,
                    false,
                    out rootError);
                if (rootHandle.IsInvalid)
                {
                    throw CreateNativeException(
                        rootError,
                        "无法锁定发布根目录");
                }

                ByHandleFileInformation rootInformation =
                    GetNativeInformation(rootHandle);
                if ((rootInformation.FileAttributes & FileAttributeDirectory) == 0 ||
                    (rootInformation.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "发布根目录身份无效或是重解析点。");
                }

                canonicalRoot = GetFinalHandlePath(rootHandle);

                int index;
                for (index = 0; index < entries.Count; index++)
                {
                    ManifestEntry entry = entries[index];
                    AddParentDirectories(
                        releaseRoot,
                        entry.FullPath,
                        candidateDirectories);

                    if (IsUninstallerControlFile(releaseRoot, entry.FullPath))
                    {
                        controlEntries.Add(entry);
                        continue;
                    }

                    DeleteVerifiedFile(canonicalRoot, entry, result);
                }

                if (result.ModifiedFiles == 0 && result.FailedFiles == 0)
                {
                    for (index = 0; index < controlEntries.Count; index++)
                    {
                        DeleteVerifiedFile(
                            canonicalRoot,
                            controlEntries[index],
                            result);
                    }

                    if (result.ModifiedFiles == 0 && result.FailedFiles == 0)
                    {
                        TryDeleteManifest(
                            releaseRoot,
                            canonicalRoot,
                            manifestHash,
                            result);
                    }
                }
                else if (controlEntries.Count > 0)
                {
                    result.Warnings.Add(
                        "检测到修改过或无法删除的发布文件，已保留卸载器和校验清单，便于关闭占用程序后重试。");
                }

                candidateDirectories.Sort(ComparePathLengthDescending);
                for (index = 0; index < candidateDirectories.Count; index++)
                {
                    TryDeleteEmptyDirectory(
                        releaseRoot,
                        canonicalRoot,
                        candidateDirectories[index],
                        result);
                }
            }
            catch (Exception exception)
            {
                result.FailedFiles++;
                result.Warnings.Add(
                    "无法建立安全的发布目录删除上下文：" +
                    exception.Message);
            }
            finally
            {
                if (rootHandle != null)
                {
                    rootHandle.Dispose();
                }
            }

            if (!string.IsNullOrEmpty(canonicalRoot))
            {
                TryDeleteEmptyDirectory(
                    releaseRoot,
                    canonicalRoot,
                    releaseRoot,
                    result);
            }

            return result;
        }

        private static void DeleteVerifiedFile(
            string canonicalRoot,
            ManifestEntry entry,
            UninstallResult result)
        {
            SafeFileHandle handle = null;
            try
            {
                int openError;
                handle = OpenNativePath(
                    entry.FullPath,
                    false,
                    true,
                    out openError);
                if (handle.IsInvalid)
                {
                    if (openError == ErrorFileNotFound ||
                        openError == ErrorPathNotFound)
                    {
                        result.MissingFiles++;
                    }
                    else
                    {
                        throw CreateNativeException(
                            openError,
                            "无法锁定文件");
                    }

                    return;
                }

                ByHandleFileInformation information =
                    GetNativeInformation(handle);
                if ((information.FileAttributes & FileAttributeDirectory) != 0 ||
                    (information.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    result.FailedFiles++;
                    result.Warnings.Add(
                        "文件身份是目录或重解析点，已保留：" +
                        entry.RelativePath);
                    return;
                }

                string expectedFinalPath = Path.GetFullPath(
                    Path.Combine(canonicalRoot, entry.RelativePath));
                string actualFinalPath = GetFinalHandlePath(handle);
                if (!IsStrictChildPath(canonicalRoot, expectedFinalPath) ||
                    !string.Equals(
                        actualFinalPath,
                        expectedFinalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.FailedFiles++;
                    result.Warnings.Add(
                        "文件最终路径与发布目录身份不一致，已保留：" +
                        entry.RelativePath);
                    return;
                }

                using (FileStream stream = new FileStream(
                    handle,
                    FileAccess.Read,
                    128 * 1024,
                    false))
                {
                    string actualHash = ComputeSha256(stream);
                    if (!string.Equals(
                        actualHash,
                        entry.ExpectedHash,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        result.ModifiedFiles++;
                        result.Warnings.Add(
                            "文件内容与发布清单不一致，已保留：" +
                            entry.RelativePath);
                        return;
                    }

                    MarkHandleForDeletion(handle);
                    result.DeletedFiles++;
                }
            }
            catch (Exception exception)
            {
                result.FailedFiles++;
                result.Warnings.Add(
                    "无法删除 " + entry.RelativePath + "：" + exception.Message);
            }
            finally
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
            }
        }

        private static string ComputeSha256(Stream stream)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                int index;
                for (index = 0; index < hash.Length; index++)
                {
                    builder.Append(hash[index].ToString("X2"));
                }

                return builder.ToString();
            }
        }

        private static string ComputeSha256(byte[] value)
        {
            using (MemoryStream stream = new MemoryStream(value, false))
            {
                return ComputeSha256(stream);
            }
        }

        private static void TryDeleteManifest(
            string releaseRoot,
            string canonicalRoot,
            string manifestHash,
            UninstallResult result)
        {
            ManifestEntry manifest = new ManifestEntry();
            manifest.ExpectedHash = manifestHash;
            manifest.RelativePath = ManifestFileName;
            manifest.FullPath = Path.GetFullPath(
                Path.Combine(releaseRoot, ManifestFileName));
            int deletedBefore = result.DeletedFiles;
            DeleteVerifiedFile(canonicalRoot, manifest, result);
            result.ManifestDeleted = result.DeletedFiles > deletedBefore;
        }

        private static bool IsUninstallerControlFile(
            string releaseRoot,
            string fullPath)
        {
            string uninstaller = Path.GetFullPath(
                Path.Combine(releaseRoot, UninstallerFileName));
            string configuration = uninstaller + ".config";
            return string.Equals(
                fullPath,
                uninstaller,
                StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    fullPath,
                    configuration,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void AddParentDirectories(
            string releaseRoot,
            string filePath,
            List<string> directories)
        {
            string current = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(current) &&
                IsStrictChildPath(releaseRoot, current))
            {
                if (!ContainsPath(directories, current))
                {
                    directories.Add(current);
                }

                current = Path.GetDirectoryName(current);
            }
        }

        private static bool ContainsPath(List<string> paths, string candidate)
        {
            int index;
            for (index = 0; index < paths.Count; index++)
            {
                if (string.Equals(
                    paths[index],
                    candidate,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int ComparePathLengthDescending(string left, string right)
        {
            return right.Length.CompareTo(left.Length);
        }

        private static SafeFileHandle OpenNativePath(
            string path,
            bool directory,
            bool readContent,
            out int error)
        {
            uint desiredAccess = DeleteAccess | FileReadAttributes;
            if (readContent)
            {
                desiredAccess |= GenericRead;
            }

            uint flags = FileFlagOpenReparsePoint;
            flags |= directory
                ? FileFlagBackupSemantics
                : FileFlagSequentialScan;
            SafeFileHandle handle = CreateFile(
                path,
                desiredAccess,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            return handle;
        }

        private static ByHandleFileInformation GetNativeInformation(
            SafeFileHandle handle)
        {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                throw CreateNativeException(
                    Marshal.GetLastWin32Error(),
                    "无法读取文件句柄身份");
            }

            return information;
        }

        private static string GetFinalHandlePath(SafeFileHandle handle)
        {
            uint capacity = 1024;
            StringBuilder path = new StringBuilder((int)capacity);
            uint length = GetFinalPathNameByHandle(
                handle,
                path,
                capacity,
                0);
            if (length == 0)
            {
                throw CreateNativeException(
                    Marshal.GetLastWin32Error(),
                    "无法解析文件句柄最终路径");
            }

            if (length >= capacity)
            {
                capacity = length + 1;
                path = new StringBuilder((int)capacity);
                length = GetFinalPathNameByHandle(
                    handle,
                    path,
                    capacity,
                    0);
                if (length == 0 || length >= capacity)
                {
                    throw CreateNativeException(
                        Marshal.GetLastWin32Error(),
                        "无法解析完整的文件句柄最终路径");
                }
            }

            string value = path.ToString();
            if (value.StartsWith(
                "\\\\?\\UNC\\",
                StringComparison.OrdinalIgnoreCase))
            {
                value = "\\\\" + value.Substring(8);
            }
            else if (value.StartsWith(
                "\\\\?\\",
                StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(4);
            }

            return TrimTrailingSeparators(Path.GetFullPath(value));
        }

        private static void MarkHandleForDeletion(SafeFileHandle handle)
        {
            FileDispositionInformation information =
                new FileDispositionInformation();
            information.DeleteFile = true;
            uint size = (uint)Marshal.SizeOf(
                typeof(FileDispositionInformation));
            if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref information,
                size))
            {
                throw CreateNativeException(
                    Marshal.GetLastWin32Error(),
                    "Windows 拒绝句柄级删除");
            }
        }

        private static Win32Exception CreateNativeException(
            int error,
            string action)
        {
            return new Win32Exception(
                error,
                action + "（Windows 错误 " + error.ToString() + "）");
        }

        private static void TryDeleteEmptyDirectory(
            string releaseRoot,
            string canonicalRoot,
            string directory,
            UninstallResult result)
        {
            SafeFileHandle handle = null;
            try
            {
                string fullPath = Path.GetFullPath(directory);
                bool isReleaseRoot = string.Equals(
                    fullPath,
                    releaseRoot,
                    StringComparison.OrdinalIgnoreCase);
                if (!isReleaseRoot && !IsStrictChildPath(releaseRoot, fullPath))
                {
                    return;
                }

                string relativePath = isReleaseRoot
                    ? string.Empty
                    : fullPath.Substring(releaseRoot.Length).TrimStart(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                string expectedFinalPath = string.IsNullOrEmpty(relativePath)
                    ? canonicalRoot
                    : Path.GetFullPath(Path.Combine(
                        canonicalRoot,
                        relativePath));

                int openError;
                handle = OpenNativePath(
                    fullPath,
                    true,
                    false,
                    out openError);
                if (handle.IsInvalid)
                {
                    if (openError != ErrorFileNotFound &&
                        openError != ErrorPathNotFound)
                    {
                        throw CreateNativeException(
                            openError,
                            "无法锁定候选空目录");
                    }

                    return;
                }

                ByHandleFileInformation information =
                    GetNativeInformation(handle);
                if ((information.FileAttributes & FileAttributeDirectory) == 0 ||
                    (information.FileAttributes & FileAttributeReparsePoint) != 0 ||
                    !string.Equals(
                        GetFinalHandlePath(handle),
                        expectedFinalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add(
                        "候选目录身份发生变化，已保留：" + fullPath);
                    return;
                }

                MarkHandleForDeletion(handle);
                result.DeletedDirectories++;
            }
            catch (Win32Exception exception)
            {
                if (exception.NativeErrorCode != ErrorDirectoryNotEmpty)
                {
                    result.Warnings.Add(
                        "无法删除空目录 " + directory + "：" +
                        exception.Message);
                }
            }
            catch (Exception exception)
            {
                result.Warnings.Add(
                    "无法确认空目录 " + directory + "：" +
                    exception.Message);
            }
            finally
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
            }
        }

        private static void DeleteUserData(UninstallResult result)
        {
            result.DataDeletionRequested = true;
            string dataRoot = GetExpectedDataRoot();
            try
            {
                string localRoot = TrimTrailingSeparators(Path.GetFullPath(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData)));
                dataRoot = Path.GetFullPath(dataRoot);
                if (!IsStrictChildPath(localRoot, dataRoot) ||
                    !string.Equals(
                        Path.GetFileName(dataRoot),
                        DataDirectoryName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "用户数据目录未通过边界校验。");
                }

                int openError;
                SafeFileHandle rootHandle = OpenNativePath(
                    dataRoot,
                    true,
                    false,
                    out openError);
                if (rootHandle.IsInvalid)
                {
                    rootHandle.Dispose();
                    if (openError == ErrorFileNotFound ||
                        openError == ErrorPathNotFound)
                    {
                        result.DataDeleted = true;
                        return;
                    }

                    throw CreateNativeException(
                        openError,
                        "无法锁定用户数据根目录");
                }

                using (rootHandle)
                {
                    ByHandleFileInformation rootInformation =
                        GetNativeInformation(rootHandle);
                    if ((rootInformation.FileAttributes &
                            FileAttributeDirectory) == 0 ||
                        (rootInformation.FileAttributes &
                            FileAttributeReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            "用户数据根目录身份无效或是重解析点，已拒绝递归访问。");
                    }

                    string canonicalDataRoot =
                        GetFinalHandlePath(rootHandle);
                    DeleteOpenedUserDirectory(
                        dataRoot,
                        canonicalDataRoot,
                        dataRoot,
                        canonicalDataRoot,
                        rootHandle,
                        true);
                }

                result.DataDeleted = true;
            }
            catch (Exception exception)
            {
                result.DataDeletionFailed = true;
                result.Warnings.Add(
                    "无法完整删除用户数据目录：" + exception.Message);
            }
        }

        private static void DeleteOpenedUserDirectory(
            string dataRoot,
            string canonicalDataRoot,
            string directory,
            string expectedFinalPath,
            SafeFileHandle directoryHandle,
            bool isRoot)
        {
            ByHandleFileInformation directoryInformation =
                GetNativeInformation(directoryHandle);
            if ((directoryInformation.FileAttributes &
                    FileAttributeDirectory) == 0 ||
                !string.Equals(
                    GetFinalHandlePath(directoryHandle),
                    expectedFinalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "用户数据目录句柄身份不一致，删除已停止。");
            }

            if ((directoryInformation.FileAttributes &
                FileAttributeReparsePoint) != 0)
            {
                if (isRoot)
                {
                    throw new InvalidOperationException(
                        "用户数据根目录是重解析点，删除已停止。");
                }

                MarkHandleForDeletion(directoryHandle);
                return;
            }

            string[] files = Directory.GetFiles(directory);
            int index;
            for (index = 0; index < files.Length; index++)
            {
                string file = Path.GetFullPath(files[index]);
                if (!IsStrictChildPath(dataRoot, file))
                {
                    throw new InvalidOperationException(
                        "用户数据文件路径越界，删除已停止。");
                }

                string expectedFilePath = Path.GetFullPath(Path.Combine(
                    expectedFinalPath,
                    Path.GetFileName(file)));
                DeleteUserFileByHandle(
                    canonicalDataRoot,
                    file,
                    expectedFilePath);
            }

            string[] directories = Directory.GetDirectories(directory);
            for (index = 0; index < directories.Length; index++)
            {
                string child = Path.GetFullPath(directories[index]);
                if (!IsStrictChildPath(dataRoot, child))
                {
                    throw new InvalidOperationException(
                        "用户数据子目录路径越界，删除已停止。");
                }

                string expectedChildPath = Path.GetFullPath(Path.Combine(
                    expectedFinalPath,
                    Path.GetFileName(child)));
                if (!IsStrictChildPath(
                    canonicalDataRoot,
                    expectedChildPath))
                {
                    throw new InvalidOperationException(
                        "用户数据子目录最终路径越界，删除已停止。");
                }

                int childError;
                SafeFileHandle childHandle = OpenNativePath(
                    child,
                    true,
                    false,
                    out childError);
                if (childHandle.IsInvalid)
                {
                    childHandle.Dispose();
                    if (childError == ErrorFileNotFound ||
                        childError == ErrorPathNotFound)
                    {
                        continue;
                    }

                    throw CreateNativeException(
                        childError,
                        "无法锁定用户数据子目录");
                }

                using (childHandle)
                {
                    DeleteOpenedUserDirectory(
                        dataRoot,
                        canonicalDataRoot,
                        child,
                        expectedChildPath,
                        childHandle,
                        false);
                }
            }

            MarkHandleForDeletion(directoryHandle);
        }

        private static void DeleteUserFileByHandle(
            string canonicalDataRoot,
            string file,
            string expectedFinalPath)
        {
            if (!IsStrictChildPath(canonicalDataRoot, expectedFinalPath))
            {
                throw new InvalidOperationException(
                    "用户数据文件最终路径越界，删除已停止。");
            }

            int openError;
            SafeFileHandle handle = OpenNativePath(
                file,
                false,
                false,
                out openError);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                if (openError == ErrorFileNotFound ||
                    openError == ErrorPathNotFound)
                {
                    return;
                }

                throw CreateNativeException(
                    openError,
                    "无法锁定用户数据文件");
            }

            using (handle)
            {
                ByHandleFileInformation information =
                    GetNativeInformation(handle);
                if ((information.FileAttributes & FileAttributeDirectory) != 0 ||
                    !string.Equals(
                        GetFinalHandlePath(handle),
                        expectedFinalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "用户数据文件句柄身份不一致，删除已停止。");
                }

                MarkHandleForDeletion(handle);
            }
        }

        private static string GetExpectedDataRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                DataDirectoryName);
        }

        private static bool IsStrictChildPath(string parent, string candidate)
        {
            string normalizedParent = TrimTrailingSeparators(
                Path.GetFullPath(parent));
            string normalizedCandidate = Path.GetFullPath(candidate);
            string prefix = normalizedParent + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            string root = Path.GetPathRoot(path);
            while (path.Length > root.Length &&
                (path[path.Length - 1] == Path.DirectorySeparatorChar ||
                    path[path.Length - 1] == Path.AltDirectorySeparatorChar))
            {
                path = path.Substring(0, path.Length - 1);
            }

            return path;
        }

        private static string QuoteArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            StringBuilder builder = new StringBuilder();
            builder.Append('"');
            int slashCount = 0;
            int index;
            for (index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '\\')
                {
                    slashCount++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', slashCount * 2 + 1);
                    builder.Append('"');
                    slashCount = 0;
                    continue;
                }

                if (slashCount > 0)
                {
                    builder.Append('\\', slashCount);
                    slashCount = 0;
                }

                builder.Append(character);
            }

            if (slashCount > 0)
            {
                builder.Append('\\', slashCount * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static void ShowResult(UninstallResult result, string releaseRoot)
        {
            StringBuilder message = new StringBuilder();
            message.AppendLine("发布文件处理完成。");
            message.AppendLine();
            message.AppendLine("已删除文件：" + result.DeletedFiles.ToString());
            message.AppendLine("原本已不存在：" + result.MissingFiles.ToString());
            message.AppendLine("因内容变更而保留：" + result.ModifiedFiles.ToString());
            message.AppendLine("删除失败：" + result.FailedFiles.ToString());
            message.AppendLine();
            message.AppendLine(
                "发布目录仅在完全为空时删除；其中的额外文件不会被递归删除。");
            if (Directory.Exists(releaseRoot))
            {
                message.AppendLine("保留的发布目录：" + releaseRoot);
            }

            if (result.DataDeletionRequested)
            {
                message.AppendLine();
                message.AppendLine(result.DataDeleted
                    ? "用户数据：已删除。"
                    : "用户数据：未能完整删除。");
            }

            if (result.Warnings.Count > 0)
            {
                message.AppendLine();
                message.AppendLine("详细信息：");
                int maximum = Math.Min(result.Warnings.Count, 8);
                int index;
                for (index = 0; index < maximum; index++)
                {
                    message.AppendLine("- " + result.Warnings[index]);
                }

                if (result.Warnings.Count > maximum)
                {
                    message.AppendLine(
                        "- 另有 " +
                        (result.Warnings.Count - maximum).ToString() +
                        " 条信息未显示。");
                }
            }

            MessageBox.Show(
                message.ToString(),
                ProductName + " 卸载",
                MessageBoxButtons.OK,
                result.FailedFiles > 0 ||
                    result.ModifiedFiles > 0 ||
                    result.DataDeletionFailed
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        }

        private static void ScheduleTemporaryCleanup()
        {
            string executable = string.Empty;
            string configuration = string.Empty;
            string directory = string.Empty;
            const string configVariable = "FILEPROMPTAI_TEMP_CONFIG";
            const string executableVariable = "FILEPROMPTAI_TEMP_EXE";
            const string directoryVariable = "FILEPROMPTAI_TEMP_DIR";
            string previousConfig = null;
            string previousExecutable = null;
            string previousDirectory = null;
            try
            {
                executable = Path.GetFullPath(Application.ExecutablePath);
                configuration = executable + ".config";
                directory = Path.GetDirectoryName(executable);
                MoveFileEx(
                    executable,
                    null,
                    MoveFileDelayUntilReboot);
                MoveFileEx(
                    directory,
                    null,
                    MoveFileDelayUntilReboot);

                string commandInterpreter = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.System),
                    "cmd.exe");
                StringBuilder commandLine = new StringBuilder();
                commandLine.Append(QuoteArgument(commandInterpreter));
                commandLine.Append(
                    " /d /q /c for /l %I in (1,1,120) do @(" +
                    "del /f /q \"%" + configVariable + "%\" >nul 2>&1 & " +
                    "del /f /q \"%" + executableVariable + "%\" >nul 2>&1 & " +
                    "rmdir /q \"%" + directoryVariable + "%\" >nul 2>&1 & " +
                    "if not exist \"%" + directoryVariable +
                    "%\" exit /b 0 & " +
                    "ping.exe 127.0.0.1 -n 2 >nul)");

                previousConfig = Environment.GetEnvironmentVariable(
                    configVariable,
                    EnvironmentVariableTarget.Process);
                previousExecutable = Environment.GetEnvironmentVariable(
                    executableVariable,
                    EnvironmentVariableTarget.Process);
                previousDirectory = Environment.GetEnvironmentVariable(
                    directoryVariable,
                    EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable(
                    configVariable,
                    configuration,
                    EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable(
                    executableVariable,
                    executable,
                    EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable(
                    directoryVariable,
                    directory,
                    EnvironmentVariableTarget.Process);

                StartupInformation startup = new StartupInformation();
                startup.Size = Marshal.SizeOf(typeof(StartupInformation));
                ProcessInformation cleanup;
                if (!CreateProcess(
                    commandInterpreter,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateNoWindow,
                    IntPtr.Zero,
                    Path.GetTempPath(),
                    ref startup,
                    out cleanup))
                {
                    throw CreateNativeException(
                        Marshal.GetLastWin32Error(),
                        "无法启动临时文件清理进程");
                }

                CloseHandle(cleanup.Thread);
                CloseHandle(cleanup.Process);
                return;
            }
            catch
            {
                try
                {
                    if (!string.IsNullOrEmpty(configuration) &&
                        File.Exists(configuration))
                    {
                        File.Delete(configuration);
                    }

                    if (!string.IsNullOrEmpty(executable))
                    {
                        MoveFileEx(
                            executable,
                            null,
                            MoveFileDelayUntilReboot);
                    }

                    if (!string.IsNullOrEmpty(directory))
                    {
                        MoveFileEx(
                            directory,
                            null,
                            MoveFileDelayUntilReboot);
                    }
                }
                catch
                {
                    // A harmless temporary copy may remain if Windows refuses cleanup.
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    configVariable,
                    previousConfig,
                    EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable(
                    executableVariable,
                    previousExecutable,
                    EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable(
                    directoryVariable,
                    previousDirectory,
                    EnvironmentVariableTarget.Process);
            }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(
                message,
                ProductName + " 无法卸载",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private sealed class ManifestEntry
        {
            public string ExpectedHash;
            public string RelativePath;
            public string FullPath;
        }

        private sealed class UninstallResult
        {
            public readonly List<string> Warnings = new List<string>();
            public int DeletedFiles;
            public int MissingFiles;
            public int ModifiedFiles;
            public int FailedFiles;
            public int DeletedDirectories;
            public bool ManifestDeleted;
            public bool DataDeletionRequested;
            public bool DataDeleted;
            public bool DataDeletionFailed;
        }

        private sealed class UninstallForm : Form
        {
            private readonly CheckBox deleteDataCheckBox;

            public UninstallForm(string releaseRoot)
            {
                Text = "卸载 " + ProductName;
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = true;
                ClientSize = new Size(520, 238);
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
                BackColor = Color.FromArgb(245, 247, 250);

                Label title = new Label();
                title.AutoSize = false;
                title.Location = new Point(24, 22);
                title.Size = new Size(472, 30);
                title.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
                title.ForeColor = Color.FromArgb(32, 38, 46);
                title.Text = "卸载 " + ProductName;
                Controls.Add(title);

                Label detail = new Label();
                detail.AutoSize = false;
                detail.Location = new Point(26, 60);
                detail.Size = new Size(468, 55);
                detail.ForeColor = Color.FromArgb(78, 91, 105);
                detail.Text =
                    "请先关闭正在运行的程序。卸载器只会删除发布清单中校验一致的文件。\r\n" +
                    "发布目录：" + releaseRoot;
                Controls.Add(detail);

                deleteDataCheckBox = new CheckBox();
                deleteDataCheckBox.AutoSize = true;
                deleteDataCheckBox.Location = new Point(28, 130);
                deleteDataCheckBox.Text = "同时删除当前用户的本地配置和会话数据";
                deleteDataCheckBox.Checked = false;
                Controls.Add(deleteDataCheckBox);

                Button uninstallButton = new Button();
                uninstallButton.Location = new Point(318, 184);
                uninstallButton.Size = new Size(86, 32);
                uninstallButton.Text = "卸载";
                uninstallButton.BackColor = Color.FromArgb(37, 112, 205);
                uninstallButton.ForeColor = Color.White;
                uninstallButton.FlatStyle = FlatStyle.Flat;
                uninstallButton.FlatAppearance.BorderSize = 0;
                uninstallButton.DialogResult = DialogResult.OK;
                Controls.Add(uninstallButton);

                Button cancelButton = new Button();
                cancelButton.Location = new Point(414, 184);
                cancelButton.Size = new Size(82, 32);
                cancelButton.Text = "取消";
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.TabIndex = 0;
                Controls.Add(cancelButton);

                uninstallButton.TabIndex = 1;
                AcceptButton = cancelButton;
                CancelButton = cancelButton;
            }

            public bool DeleteData
            {
                get { return deleteDataCheckBox.Checked; }
            }
        }
    }
}
