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
        private const string RecoveryMarkerFileName =
            ".FilePromptAI-uninstall-recovery";
        private const string DataDirectoryName = "FilePromptAI-Win7";
        private const string DataRootOverrideVariable =
            "FILEPROMPTAI_DATA_ROOT";
        private const int MoveFileDelayUntilReboot = 0x4;
        private const uint DeleteAccess = 0x00010000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeReadOnly = 0x00000001;
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
        private const int ProcessExitWaitMilliseconds = 60000;
        private const uint CreateNoWindow = 0x08000000;

        // Private fault controls are set only by the regression test through
        // reflection. Production executions always keep these defaults.
        private static int commitFailureAfterForTests = -1;
        private static bool rollbackFailureForTests = false;

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

            if ((args.Length == 12 || args.Length == 13) &&
                string.Equals(args[0], "--execute", StringComparison.Ordinal))
            {
                bool silent = args.Length == 13 &&
                    string.Equals(
                        args[12],
                        "--silent",
                        StringComparison.Ordinal);
                if (args.Length == 13 && !silent)
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

            if (args.Length > 0 &&
                string.Equals(
                    args[0],
                    "--check-from-app",
                    StringComparison.Ordinal))
            {
                RunCheckFromApp(args);
                return;
            }

            int applicationProcessId;
            string argumentError;
            if (!TryParseInteractiveArguments(
                args,
                out applicationProcessId,
                out argumentError))
            {
                Environment.ExitCode = 2;
                ShowError(argumentError);
                return;
            }

            RunInteractive(applicationProcessId);
        }

        private static bool TryParseInteractiveArguments(
            string[] args,
            out int applicationProcessId,
            out string error)
        {
            applicationProcessId = 0;
            error = string.Empty;
            if (args == null || args.Length == 0)
            {
                return true;
            }

            if (args.Length == 2 &&
                string.Equals(
                    args[0],
                    "--from-app",
                    StringComparison.Ordinal) &&
                int.TryParse(
                    args[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out applicationProcessId) &&
                applicationProcessId > 0)
            {
                return true;
            }

            applicationProcessId = 0;
            error =
                "卸载启动参数无效，未删除任何文件。\r\n\r\n" +
                "请直接运行 " + UninstallerFileName +
                "，或从 FilePrompt AI 设置中的“卸载程序...”进入。";
            return false;
        }

        private static bool TryParseCheckFromAppArguments(
            string[] args,
            out int applicationProcessId)
        {
            applicationProcessId = 0;
            if (args == null ||
                args.Length != 2 ||
                !string.Equals(
                    args[0],
                    "--check-from-app",
                    StringComparison.Ordinal))
            {
                return false;
            }

            int parsedProcessId;
            if (!int.TryParse(
                    args[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsedProcessId) ||
                parsedProcessId <= 0)
            {
                return false;
            }

            applicationProcessId = parsedProcessId;
            return true;
        }

        private static void RunCheckFromApp(string[] args)
        {
            int applicationProcessId;
            if (!TryParseCheckFromAppArguments(
                args,
                out applicationProcessId))
            {
                Environment.ExitCode = 2;
                return;
            }

            string releaseRoot;
            string rootError;
            if (!TryNormalizeReleaseRoot(
                AppDomain.CurrentDomain.BaseDirectory,
                out releaseRoot,
                out rootError) ||
                !IsExpectedRootUninstaller(releaseRoot))
            {
                Environment.ExitCode = 2;
                return;
            }

            long applicationProcessStartTicks;
            string processError;
            if (!TryValidateApplicationProcessForCheck(
                applicationProcessId,
                releaseRoot,
                out applicationProcessStartTicks,
                out processError))
            {
                Environment.ExitCode = 2;
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
                Environment.ExitCode = 3;
                return;
            }

            if (!IsIdentifiedProcessStillRunning(
                applicationProcessId,
                applicationProcessStartTicks))
            {
                Environment.ExitCode = 2;
                return;
            }

            Environment.ExitCode = 0;
        }

        private static bool IsExpectedRootUninstaller(string releaseRoot)
        {
            try
            {
                string executablePath = Path.GetFullPath(
                    Application.ExecutablePath);
                string expectedPath = Path.GetFullPath(
                    Path.Combine(releaseRoot, UninstallerFileName));
                return File.Exists(executablePath) &&
                    (File.GetAttributes(executablePath) &
                        FileAttributes.ReparsePoint) == 0 &&
                    string.Equals(
                        executablePath,
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryValidateApplicationProcessForCheck(
            int processId,
            string releaseRoot,
            out long startTicks,
            out string error)
        {
            startTicks = 0;
            error = string.Empty;
            try
            {
                string expectedAppDirectory = TrimTrailingSeparators(
                    Path.GetFullPath(Path.Combine(releaseRoot, "app")));
                if (!Directory.Exists(expectedAppDirectory) ||
                    (File.GetAttributes(expectedAppDirectory) &
                        FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "发布目录中的 app 目录身份无效。");
                }

                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            "FilePrompt AI 主程序进程已经退出。");
                    }

                    startTicks = process.StartTime.ToUniversalTime().Ticks;
                    if (startTicks <= 0)
                    {
                        throw new InvalidOperationException(
                            "无法确认 FilePrompt AI 主程序启动时间。");
                    }

                    string modulePath = Path.GetFullPath(
                        process.MainModule.FileName);
                    string moduleDirectory = TrimTrailingSeparators(
                        Path.GetDirectoryName(modulePath));
                    if (!File.Exists(modulePath) ||
                        (File.GetAttributes(modulePath) &
                            FileAttributes.ReparsePoint) != 0 ||
                        !string.Equals(
                            moduleDirectory,
                            expectedAppDirectory,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "请求检查的进程不在当前发布目录的 app 目录中。");
                    }

                    process.Refresh();
                    if (process.HasExited ||
                        process.StartTime.ToUniversalTime().Ticks != startTicks)
                    {
                        throw new InvalidOperationException(
                            "FilePrompt AI 主程序进程身份已变化。");
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                startTicks = 0;
                error = exception.Message;
                return false;
            }
        }

        private static bool IsIdentifiedProcessStillRunning(
            int processId,
            long expectedStartTicks)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return !process.HasExited &&
                        process.StartTime.ToUniversalTime().Ticks ==
                            expectedStartTicks;
                }
            }
            catch
            {
                return false;
            }
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

        private static void RunInteractive(int applicationProcessId)
        {
            string releaseRoot;
            string rootError;
            if (!TryNormalizeReleaseRoot(
                AppDomain.CurrentDomain.BaseDirectory,
                out releaseRoot,
                out rootError))
            {
                Environment.ExitCode = 2;
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
                Environment.ExitCode = 3;
                ShowError(manifestError);
                return;
            }

            long applicationProcessStartTicks;
            string processError;
            if (!TryCaptureProcessStartTicks(
                applicationProcessId,
                out applicationProcessStartTicks,
                out processError))
            {
                Environment.ExitCode = 2;
                ShowError(processError);
                return;
            }
            if (applicationProcessStartTicks == 0)
            {
                applicationProcessId = 0;
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
                string dataDeletionError;
                if (!CanDeleteDefaultUserData(out dataDeletionError))
                {
                    deleteData = false;
                    MessageBox.Show(
                        dataDeletionError,
                        "用户数据将保留",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
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
            if (!StartTemporaryWorker(
                releaseRoot,
                deleteData,
                applicationProcessId,
                applicationProcessStartTicks,
                out startError))
            {
                Environment.ExitCode = 2;
                ShowError(startError);
            }
        }

        private static bool StartTemporaryWorker(
            string releaseRoot,
            bool deleteData,
            int applicationProcessId,
            long applicationProcessStartTicks,
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
                int parentProcessId;
                long parentProcessStartTicks;
                using (Process parentProcess = Process.GetCurrentProcess())
                {
                    parentProcessId = parentProcess.Id;
                    parentProcessStartTicks = parentProcess.StartTime
                        .ToUniversalTime().Ticks;
                }
                startInfo.Arguments =
                    "--execute " + QuoteArgument(releaseRoot) +
                    " --delete-data " + (deleteData ? "true" : "false") +
                    " --parent-pid " + parentProcessId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " --parent-start-ticks " +
                    parentProcessStartTicks.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " --app-pid " + applicationProcessId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " --app-start-ticks " +
                    applicationProcessStartTicks.ToString(
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
                !string.Equals(args[4], "--parent-pid", StringComparison.Ordinal) ||
                !string.Equals(
                    args[6],
                    "--parent-start-ticks",
                    StringComparison.Ordinal) ||
                !string.Equals(args[8], "--app-pid", StringComparison.Ordinal) ||
                !string.Equals(
                    args[10],
                    "--app-start-ticks",
                    StringComparison.Ordinal))
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

            if (deleteData)
            {
                string dataDeletionError;
                if (!CanDeleteDefaultUserData(out dataDeletionError))
                {
                    ShowWorkerError(
                        dataDeletionError,
                        silent,
                        4);
                    return;
                }
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

            long parentProcessStartTicks;
            if (!long.TryParse(
                args[7],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out parentProcessStartTicks) ||
                parentProcessStartTicks <= 0)
            {
                ShowWorkerError(
                    "父进程身份参数无效，未删除任何文件。",
                    silent,
                    2);
                return;
            }

            int applicationProcessId;
            long applicationProcessStartTicks;
            if (!int.TryParse(
                args[9],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out applicationProcessId) ||
                applicationProcessId < 0 ||
                !long.TryParse(
                    args[11],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out applicationProcessStartTicks) ||
                applicationProcessStartTicks < 0 ||
                ((applicationProcessId == 0) !=
                    (applicationProcessStartTicks == 0)))
            {
                ShowWorkerError(
                    "原程序进程身份参数无效，未删除任何文件。",
                    silent,
                    2);
                return;
            }

            string waitError;
            if (!WaitForIdentifiedProcessExit(
                parentProcessId,
                parentProcessStartTicks,
                "启动卸载器",
                out waitError) ||
                !WaitForIdentifiedProcessExit(
                    applicationProcessId,
                    applicationProcessStartTicks,
                    "FilePrompt AI 主程序",
                    out waitError))
            {
                ShowWorkerError(waitError, silent, 4);
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
                ShowWorkerError(manifestError, silent, 3);
                return;
            }

            UninstallResult result = DeleteReleaseFiles(
                releaseRoot,
                entries,
                manifestHash);
            if (deleteData && IsReleaseDeletionSuccessful(result))
            {
                DeleteUserData(result);
            }
            else if (deleteData)
            {
                result.DataDeletionRequested = true;
                result.Warnings.Add(
                    "发布文件未能通过完整卸载检查，用户数据也已保留，未删除任何用户数据。");
            }

            if (silent)
            {
                Environment.ExitCode = IsReleaseDeletionSuccessful(result) &&
                    !result.DataDeletionFailed
                    ? 0
                    : 4;
            }
            else
            {
                Environment.ExitCode = IsReleaseDeletionSuccessful(result) &&
                    !result.DataDeletionFailed
                    ? 0
                    : 4;
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

        private static bool TryCaptureProcessStartTicks(
            int processId,
            out long startTicks,
            out string error)
        {
            startTicks = 0;
            error = string.Empty;
            if (processId == 0)
            {
                return true;
            }

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                    {
                        return true;
                    }

                    startTicks = process.StartTime.ToUniversalTime().Ticks;
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // The application completed before the uninstaller captured it.
                return true;
            }
            catch (InvalidOperationException)
            {
                // The process exited while its identity was being captured.
                return true;
            }
            catch (Exception exception)
            {
                error =
                    "无法确认 FilePrompt AI 主程序进程，未删除任何文件：\r\n\r\n" +
                    exception.Message;
                return false;
            }
        }

        private static bool WaitForIdentifiedProcessExit(
            int processId,
            long expectedStartTicks,
            string description,
            out string error)
        {
            error = string.Empty;
            if (processId == 0 || expectedStartTicks == 0)
            {
                return true;
            }

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                    {
                        return true;
                    }

                    long actualStartTicks = process.StartTime
                        .ToUniversalTime().Ticks;
                    if (actualStartTicks != expectedStartTicks)
                    {
                        // The original process exited and Windows reused its PID.
                        return true;
                    }

                    if (!process.WaitForExit(ProcessExitWaitMilliseconds))
                    {
                        error =
                            "等待" + description + "退出超时，未删除任何文件。\r\n\r\n" +
                            "请关闭 FilePrompt AI 后重新运行卸载器。";
                        return false;
                    }

                    process.WaitForExit();
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Exception exception)
            {
                error =
                    "无法确认" + description + "已经退出，未删除任何文件：\r\n\r\n" +
                    exception.Message;
                return false;
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
                        "没有在以下实际检查目录找到卸载清单：\r\n" +
                        releaseRoot + "\r\n\r\n" +
                        "缺少文件：" + ManifestFileName + "\r\n\r\n" +
                        "请先将完整离线压缩包全部解压到同一个文件夹，" +
                        "再运行该发布根目录内的 " + UninstallerFileName +
                        "。不要只复制或单独运行卸载器文件。为避免误删，卸载已停止。");
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
            List<PreparedFile> payloadFiles = new List<PreparedFile>();
            List<PreparedFile> controlFiles = new List<PreparedFile>();
            List<PreparedFile> preparedFiles = new List<PreparedFile>();
            List<PreparedFile> openedFiles = new List<PreparedFile>();
            List<string> candidateDirectories = new List<string>();
            string canonicalRoot = string.Empty;
            SafeFileHandle rootHandle = null;
            bool filesCommitted = false;
            bool commitAttempted = false;
            bool recoveryMode = false;

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

                string recoveryError;
                if (!TryGetRecoveryMode(
                    releaseRoot,
                    manifestHash,
                    out recoveryMode,
                    out recoveryError))
                {
                    result.FailedFiles++;
                    result.Warnings.Add(recoveryError);
                    return result;
                }

                int index;
                for (index = 0; index < entries.Count; index++)
                {
                    ManifestEntry entry = entries[index];
                    AddParentDirectories(
                        releaseRoot,
                        entry.FullPath,
                        candidateDirectories);

                    PreparedFile prepared;
                    if (TryPrepareVerifiedFile(
                        canonicalRoot,
                        entry,
                        recoveryMode &&
                            !IsUninstallerControlFile(
                                releaseRoot,
                                entry.FullPath),
                        result,
                        out prepared))
                    {
                        if (prepared != null)
                        {
                            openedFiles.Add(prepared);
                            if (IsUninstallerControlFile(
                                releaseRoot,
                                entry.FullPath))
                            {
                                controlFiles.Add(prepared);
                            }
                            else
                            {
                                payloadFiles.Add(prepared);
                            }
                        }
                    }
                }

                ManifestEntry manifestEntry = new ManifestEntry();
                manifestEntry.ExpectedHash = manifestHash;
                manifestEntry.RelativePath = ManifestFileName;
                manifestEntry.FullPath = Path.GetFullPath(
                    Path.Combine(releaseRoot, ManifestFileName));
                PreparedFile preparedManifest;
                if (TryPrepareVerifiedFile(
                    canonicalRoot,
                    manifestEntry,
                    false,
                    result,
                    out preparedManifest))
                {
                    if (preparedManifest != null)
                    {
                        openedFiles.Add(preparedManifest);
                        controlFiles.Add(preparedManifest);
                    }
                }

                if (recoveryMode)
                {
                    ManifestEntry recoveryEntry = CreateRecoveryMarkerEntry(
                        releaseRoot,
                        manifestHash);
                    PreparedFile preparedRecovery;
                    if (TryPrepareVerifiedFile(
                        canonicalRoot,
                        recoveryEntry,
                        false,
                        result,
                        out preparedRecovery))
                    {
                        if (preparedRecovery != null)
                        {
                            openedFiles.Add(preparedRecovery);
                            controlFiles.Add(preparedRecovery);
                        }
                    }
                }

                if (result.MissingFiles != 0 ||
                    result.ModifiedFiles != 0 ||
                    result.FailedFiles != 0 ||
                    payloadFiles.Count + controlFiles.Count +
                        result.AlreadyRemovedFiles !=
                        entries.Count + 1 + (recoveryMode ? 1 : 0))
                {
                    result.Warnings.Add(
                        "卸载前完整安全检查未通过。程序文件、卸载清单和发布目录均保持原样，未删除任何文件。");
                    return result;
                }

                // Recovery controls are committed only after every payload.
                // The root uninstaller is always the final delete operation,
                // so any earlier failure leaves an executable retry entry.
                preparedFiles.AddRange(payloadFiles);
                AddControlFilesForCommit(
                    releaseRoot,
                    controlFiles,
                    preparedFiles);
                commitAttempted = true;
                if (!TryCommitPreparedFiles(preparedFiles, result))
                {
                    result.Warnings.Add(
                        "提交删除时 Windows 返回错误。Windows 可能已经删除部分载荷；根卸载器会尽量保留，并写入恢复标记供再次运行。请勿手工移动剩余文件。"
                    );
                }
                else
                {
                    filesCommitted = true;
                    result.DeletedFiles = preparedFiles.Count;
                    result.ManifestDeleted = true;
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
                if (commitAttempted && !filesCommitted &&
                    result.PartialDeletion)
                {
                    DisposePreparedFiles(openedFiles);
                    openedFiles.Clear();
                    if (rootHandle != null)
                    {
                        rootHandle.Dispose();
                        rootHandle = null;
                    }
                    result.DeletedFiles = CountAbsentReleaseFiles(
                        releaseRoot,
                        entries);
                    EnsureRetryMetadata(
                        releaseRoot,
                        entries,
                        result);
                    if (result.DeletedFiles > 0)
                    {
                        result.PartialDeletion = true;
                        result.Warnings.Add(
                            "本次提交失败后检测到 " +
                            result.DeletedFiles.ToString() +
                            " 个清单载荷已不存在。请再次运行发布根目录中的 " +
                            UninstallerFileName + " 完成清理。"
                        );
                    }
                }
                DisposePreparedFiles(openedFiles);

                if (rootHandle != null)
                {
                    rootHandle.Dispose();
                }
            }

            if (filesCommitted && !string.IsNullOrEmpty(canonicalRoot))
            {
                candidateDirectories.Sort(ComparePathLengthDescending);
                int index;
                for (index = 0; index < candidateDirectories.Count; index++)
                {
                    TryDeleteEmptyDirectory(
                        releaseRoot,
                        canonicalRoot,
                        candidateDirectories[index],
                        result);
                }

                TryDeleteEmptyDirectory(
                    releaseRoot,
                    canonicalRoot,
                    releaseRoot,
                    result);
            }

            return result;
        }

        private static bool IsReleaseDeletionSuccessful(
            UninstallResult result)
        {
            return result != null &&
                result.DeletedFiles > 0 &&
                result.MissingFiles == 0 &&
                result.ModifiedFiles == 0 &&
                result.FailedFiles == 0 &&
                !result.PartialDeletion &&
                result.ManifestDeleted;
        }

        private static bool IsUninstallerControlFile(
            string releaseRoot,
            string fullPath)
        {
            string uninstaller = Path.GetFullPath(
                Path.Combine(releaseRoot, UninstallerFileName));
            return string.Equals(
                    fullPath,
                    uninstaller,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    fullPath,
                    uninstaller + ".config",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void AddControlFilesForCommit(
            string releaseRoot,
            List<PreparedFile> controlFiles,
            List<PreparedFile> destination)
        {
            string uninstaller = Path.GetFullPath(
                Path.Combine(releaseRoot, UninstallerFileName));
            PreparedFile uninstallerFile = null;
            int index;
            for (index = 0; index < controlFiles.Count; index++)
            {
                PreparedFile prepared = controlFiles[index];
                if (string.Equals(
                    prepared.Entry.FullPath,
                    uninstaller,
                    StringComparison.OrdinalIgnoreCase))
                {
                    uninstallerFile = prepared;
                }
                else
                {
                    destination.Add(prepared);
                }
            }

            if (uninstallerFile == null)
            {
                throw new InvalidDataException(
                    "卸载清单缺少可保留到最后的根卸载器。"
                );
            }

            destination.Add(uninstallerFile);
        }

        private static bool TryGetRecoveryMode(
            string releaseRoot,
            string manifestHash,
            out bool recoveryMode,
            out string error)
        {
            recoveryMode = false;
            error = string.Empty;
            string markerPath = Path.GetFullPath(
                Path.Combine(releaseRoot, RecoveryMarkerFileName));
            try
            {
                if (!IsStrictChildPath(releaseRoot, markerPath))
                {
                    throw new InvalidDataException("卸载恢复标记路径越界。");
                }

                if (!File.Exists(markerPath))
                {
                    return true;
                }

                FileAttributes attributes = File.GetAttributes(markerPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) != 0)
                {
                    throw new InvalidDataException(
                        "卸载恢复标记不是普通文件。"
                    );
                }

                byte[] actual = File.ReadAllBytes(markerPath);
                byte[] expected = BuildRecoveryMarkerBytes(manifestHash);
                if (!ByteArraysEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "卸载恢复标记与当前校验清单不匹配。请重新完整解压原 ZIP。"
                    );
                }

                recoveryMode = true;
                return true;
            }
            catch (Exception exception)
            {
                error = "无法验证卸载恢复状态，未删除任何文件：" +
                    exception.Message;
                return false;
            }
        }

        private static ManifestEntry CreateRecoveryMarkerEntry(
            string releaseRoot,
            string manifestHash)
        {
            byte[] bytes = BuildRecoveryMarkerBytes(manifestHash);
            ManifestEntry entry = new ManifestEntry();
            entry.ExpectedHash = ComputeSha256(bytes);
            entry.RelativePath = RecoveryMarkerFileName;
            entry.FullPath = Path.GetFullPath(
                Path.Combine(releaseRoot, RecoveryMarkerFileName));
            return entry;
        }

        private static byte[] BuildRecoveryMarkerBytes(string manifestHash)
        {
            string text =
                "FilePromptAI-Uninstall-Recovery: 1\r\n" +
                "Manifest-SHA256: " + manifestHash + "\r\n";
            return new UTF8Encoding(false).GetBytes(text);
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            int index;
            for (index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static void EnsureRetryMetadata(
            string releaseRoot,
            List<ManifestEntry> entries,
            UninstallResult result)
        {
            try
            {
                string uninstallerPath = Path.GetFullPath(
                    Path.Combine(releaseRoot, UninstallerFileName));
                string workerPath = Path.GetFullPath(Application.ExecutablePath);
                EnsureRecoveryFile(workerPath, uninstallerPath);
                EnsureRecoveryFile(
                    workerPath + ".config",
                    uninstallerPath + ".config");

                string manifestPath = Path.GetFullPath(
                    Path.Combine(releaseRoot, ManifestFileName));
                if (!File.Exists(manifestPath))
                {
                    WriteRecoveryFile(
                        manifestPath,
                        BuildCanonicalManifestBytes(entries));
                }

                string markerPath = Path.GetFullPath(
                    Path.Combine(releaseRoot, RecoveryMarkerFileName));
                byte[] markerBytes = BuildRecoveryMarkerBytes(
                    ComputeSha256(File.ReadAllBytes(manifestPath)));
                if (!File.Exists(markerPath) ||
                    !ByteArraysEqual(File.ReadAllBytes(markerPath), markerBytes))
                {
                    WriteRecoveryFile(markerPath, markerBytes);
                }
            }
            catch (Exception exception)
            {
                result.PartialDeletion = true;
                result.Warnings.Add(
                    "无法完整写入卸载恢复信息：" + exception.Message +
                    "。请重新完整解压原 ZIP 后再次运行卸载器。"
                );
            }
        }

        private static void EnsureRecoveryFile(
            string source,
            string destination)
        {
            if (File.Exists(destination))
            {
                return;
            }

            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    "恢复源文件不存在。",
                    source);
            }

            byte[] bytes = File.ReadAllBytes(source);
            WriteRecoveryFile(destination, bytes);
        }

        private static void WriteRecoveryFile(string path, byte[] bytes)
        {
            string temporary = path + "." +
                Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static byte[] BuildCanonicalManifestBytes(
            List<ManifestEntry> entries)
        {
            StringBuilder manifest = new StringBuilder();
            int index;
            for (index = 0; index < entries.Count; index++)
            {
                manifest.Append(entries[index].ExpectedHash);
                manifest.Append(" *");
                manifest.Append(entries[index].RelativePath);
                manifest.Append("\r\n");
            }

            return new UTF8Encoding(false).GetBytes(manifest.ToString());
        }

        private static int CountAbsentReleaseFiles(
            string releaseRoot,
            List<ManifestEntry> entries)
        {
            int missing = 0;
            int index;
            for (index = 0; index < entries.Count; index++)
            {
                if (!IsUninstallerControlFile(
                        releaseRoot,
                        entries[index].FullPath) &&
                    !File.Exists(entries[index].FullPath))
                {
                    missing++;
                }
            }

            return missing;
        }

        private static bool TryPrepareVerifiedFile(
            string canonicalRoot,
            ManifestEntry entry,
            bool allowMissingFromRecovery,
            UninstallResult result,
            out PreparedFile prepared)
        {
            prepared = null;
            SafeFileHandle handle = null;
            FileStream stream = null;
            bool transferred = false;
            try
            {
                int openError;
                handle = OpenNativeFileForUninstallPreflight(
                    entry.FullPath,
                    out openError);
                if (handle.IsInvalid)
                {
                    if (openError == ErrorFileNotFound ||
                        openError == ErrorPathNotFound)
                    {
                        if (allowMissingFromRecovery)
                        {
                            result.AlreadyRemovedFiles++;
                        }
                        else
                        {
                            result.MissingFiles++;
                            result.Warnings.Add(
                                "清单中的文件不存在，已停止卸载：" +
                                entry.RelativePath);
                        }
                    }
                    else
                    {
                        result.FailedFiles++;
                        result.Warnings.Add(
                            "文件被占用、无删除权限或无法锁定，已停止卸载：" +
                            entry.RelativePath + "；" +
                            CreateNativeException(
                                openError,
                                "Windows 无法取得安全删除句柄").Message);
                    }

                    return false;
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
                    return false;
                }

                if ((information.FileAttributes & FileAttributeReadOnly) != 0)
                {
                    result.FailedFiles++;
                    result.Warnings.Add(
                        "文件是只读文件，Windows 无法安全删除，已停止卸载：" +
                        entry.RelativePath);
                    return false;
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
                    return false;
                }

                stream = new FileStream(
                    handle,
                    FileAccess.Read,
                    128 * 1024,
                    false);
                string actualHash = ComputeSha256(stream);
                if (!string.Equals(
                    actualHash,
                    entry.ExpectedHash,
                    StringComparison.OrdinalIgnoreCase))
                {
                    result.ModifiedFiles++;
                    result.Warnings.Add(
                        "文件内容与发布清单不一致，已停止卸载：" +
                        entry.RelativePath);
                    return false;
                }

                prepared = new PreparedFile();
                prepared.Entry = entry;
                prepared.Handle = handle;
                prepared.Stream = stream;
                transferred = true;
                return true;
            }
            catch (Exception exception)
            {
                result.FailedFiles++;
                result.Warnings.Add(
                    "无法完成卸载前检查 " + entry.RelativePath + "：" +
                    exception.Message);
                return false;
            }
            finally
            {
                if (!transferred)
                {
                    if (stream != null)
                    {
                        stream.Dispose();
                    }
                    else if (handle != null)
                    {
                        handle.Dispose();
                    }
                }
            }
        }

        private static bool TryCommitPreparedFiles(
            List<PreparedFile> preparedFiles,
            UninstallResult result)
        {
            int markedCount = 0;
            try
            {
                for (markedCount = 0;
                    markedCount < preparedFiles.Count;
                    markedCount++)
                {
                    if (commitFailureAfterForTests >= 0 &&
                        markedCount == commitFailureAfterForTests)
                    {
                        throw new InvalidOperationException(
                            "测试注入：提交删除失败。");
                    }

                    SetHandleDeletion(
                        preparedFiles[markedCount].Handle,
                        true);
                    preparedFiles[markedCount].DeleteMarked = true;
                }

                return true;
            }
            catch (Exception exception)
            {
                result.FailedFiles++;
                string relativePath = markedCount < preparedFiles.Count
                    ? preparedFiles[markedCount].Entry.RelativePath
                    : "未知文件";
                result.Warnings.Add(
                    "Windows 无法提交删除 " + relativePath + "：" +
                    exception.Message);

                int rollbackIndex;
                for (rollbackIndex = markedCount - 1;
                    rollbackIndex >= 0;
                    rollbackIndex--)
                {
                    try
                    {
                        if (rollbackFailureForTests && rollbackIndex == 0)
                        {
                            throw new InvalidOperationException(
                                "测试注入：撤销删除标记失败。");
                        }

                        SetHandleDeletion(
                            preparedFiles[rollbackIndex].Handle,
                            false);
                        preparedFiles[rollbackIndex].DeleteMarked = false;
                    }
                    catch (Exception rollbackException)
                    {
                        result.PartialDeletion = true;
                        result.Warnings.Add(
                            "无法撤销文件删除标记 " +
                            preparedFiles[rollbackIndex].Entry.RelativePath +
                            "：" + rollbackException.Message);
                    }
                }

                return false;
            }
        }

        private static void DisposePreparedFiles(
            List<PreparedFile> preparedFiles)
        {
            int index;
            for (index = preparedFiles.Count - 1; index >= 0; index--)
            {
                try
                {
                    if (preparedFiles[index].Stream != null)
                    {
                        preparedFiles[index].Stream.Dispose();
                    }
                    else if (preparedFiles[index].Handle != null)
                    {
                        preparedFiles[index].Handle.Dispose();
                    }
                }
                catch
                {
                    // Windows will close remaining native handles when the worker exits.
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

        private static SafeFileHandle OpenNativeFileForUninstallPreflight(
            string path,
            out int error)
        {
            SafeFileHandle handle = CreateFile(
                path,
                DeleteAccess | FileReadAttributes | GenericRead,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
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

        private static void SetHandleDeletion(
            SafeFileHandle handle,
            bool deleteFile)
        {
            FileDispositionInformation information =
                new FileDispositionInformation();
            information.DeleteFile = deleteFile;
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
                    deleteFile
                        ? "Windows 拒绝句柄级删除"
                        : "Windows 拒绝撤销句柄级删除");
            }
        }

        private static void MarkHandleForDeletion(SafeFileHandle handle)
        {
            SetHandleDeletion(handle, true);
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
            string dataDeletionError;
            if (!CanDeleteDefaultUserData(out dataDeletionError))
            {
                result.DataDeletionFailed = true;
                result.Warnings.Add(dataDeletionError);
                return;
            }

            string dataRoot = GetDefaultDataRoot();
            try
            {
                bool alreadyMissing;
                string deletionError;
                if (!TryDeleteUserDataTree(
                    dataRoot,
                    out alreadyMissing,
                    out deletionError))
                {
                    throw new InvalidOperationException(
                        deletionError);
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

        private static bool CanDeleteDefaultUserData(out string error)
        {
            error = string.Empty;
            try
            {
                string defaultRoot = GetDefaultDataRoot();
                string configured = Environment.GetEnvironmentVariable(
                    DataRootOverrideVariable,
                    EnvironmentVariableTarget.Process);
                if (configured == null || configured.Trim().Length == 0)
                {
                    return true;
                }

                string configuredRoot = TrimTrailingSeparators(
                    Path.GetFullPath(configured.Trim()));
                if (string.Equals(
                    configuredRoot,
                    defaultRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                error =
                    "当前程序使用自定义用户数据目录。为避免误删，卸载器已拒绝删除用户数据；" +
                    "自定义目录和默认目录均会保留。\r\n\r\n" +
                    "当前目录：" + configuredRoot;
                return false;
            }
            catch (Exception exception)
            {
                error =
                    "无法确认当前用户数据目录，卸载器已拒绝删除任何用户数据：" +
                    exception.Message;
                return false;
            }
        }

        private static bool TryDeleteUserDataTree(
            string dataRoot,
            out bool alreadyMissing,
            out string error)
        {
            alreadyMissing = false;
            error = string.Empty;
            PreparedUserDataTree tree = new PreparedUserDataTree();
            try
            {
                tree.LogicalRoot = TrimTrailingSeparators(
                    Path.GetFullPath(dataRoot));
                string pathRoot = TrimTrailingSeparators(
                    Path.GetPathRoot(tree.LogicalRoot));
                if (string.IsNullOrEmpty(tree.LogicalRoot) ||
                    string.Equals(
                        tree.LogicalRoot,
                        pathRoot,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        Path.GetFileName(tree.LogicalRoot),
                        DataDirectoryName,
                        StringComparison.Ordinal))
                {
                    error = "用户数据目录未通过安全边界校验。";
                    return false;
                }

                bool rootMissing;
                if (!TryPrepareUserDataDirectory(
                    tree,
                    tree.LogicalRoot,
                    null,
                    true,
                    out rootMissing,
                    out error))
                {
                    return false;
                }

                if (rootMissing)
                {
                    alreadyMissing = true;
                    return true;
                }

                tree.CanonicalRoot = tree.Directories[0].FinalPath;
                if (!string.Equals(
                    tree.CanonicalRoot,
                    tree.LogicalRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        "用户数据根目录最终身份与默认目录不一致，删除已停止。";
                    return false;
                }

                if (!ValidatePreparedUserDataTree(tree, out error))
                {
                    return false;
                }

                return CommitPreparedUserDataTree(tree, out error);
            }
            catch (Exception exception)
            {
                error = "用户数据删除准备失败：" + exception.Message;
                return false;
            }
            finally
            {
                DisposePreparedUserDataTree(tree);
            }
        }

        private static bool TryPrepareUserDataDirectory(
            PreparedUserDataTree tree,
            string directory,
            string expectedFinalPath,
            bool isRoot,
            out bool rootMissing,
            out string error)
        {
            rootMissing = false;
            error = string.Empty;
            SafeFileHandle handle = null;
            bool transferred = false;
            try
            {
                int openError;
                handle = OpenNativeUserDataDirectory(
                    directory,
                    out openError);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    handle = null;
                    if (isRoot &&
                        (openError == ErrorFileNotFound ||
                            openError == ErrorPathNotFound))
                    {
                        rootMissing = true;
                        return true;
                    }

                    error = CreateNativeException(
                        openError,
                        "无法独占验证用户数据目录 " + directory).Message;
                    return false;
                }

                ByHandleFileInformation information =
                    GetNativeInformation(handle);
                string finalPath = GetFinalHandlePath(handle);
                if ((information.FileAttributes & FileAttributeDirectory) == 0 ||
                    (information.FileAttributes & FileAttributeReparsePoint) != 0 ||
                    (information.FileAttributes & FileAttributeReadOnly) != 0)
                {
                    error =
                        "用户数据目录是只读目录、重解析点或身份无效，删除已停止：" +
                        directory;
                    return false;
                }

                if (isRoot)
                {
                    expectedFinalPath = finalPath;
                }
                else if (!IsStrictChildPath(tree.CanonicalRoot, finalPath) ||
                    !string.Equals(
                        finalPath,
                        expectedFinalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        "用户数据子目录句柄身份不一致，删除已停止：" +
                        directory;
                    return false;
                }

                PreparedUserDataDirectory prepared =
                    new PreparedUserDataDirectory();
                prepared.LogicalPath = directory;
                prepared.FinalPath = finalPath;
                prepared.Identity = information;
                prepared.Handle = handle;
                tree.Directories.Add(prepared);
                if (isRoot)
                {
                    tree.CanonicalRoot = finalPath;
                }
                transferred = true;

                string[] entries = Directory.GetFileSystemEntries(directory);
                Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
                int index;
                for (index = 0; index < entries.Length; index++)
                {
                    string entry = Path.GetFullPath(entries[index]);
                    if (!IsStrictChildPath(tree.LogicalRoot, entry))
                    {
                        error =
                            "用户数据条目路径越界，删除已停止：" + entry;
                        return false;
                    }

                    string name = Path.GetFileName(entry);
                    prepared.EntryNames.Add(name);
                    string expectedChildFinalPath = Path.GetFullPath(
                        Path.Combine(finalPath, name));
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        bool childMissing;
                        if (!TryPrepareUserDataDirectory(
                            tree,
                            entry,
                            expectedChildFinalPath,
                            false,
                            out childMissing,
                            out error) ||
                            childMissing)
                        {
                            if (string.IsNullOrEmpty(error))
                            {
                                error =
                                    "用户数据子目录在预检期间消失，删除已停止：" +
                                    entry;
                            }
                            return false;
                        }
                    }
                    else if (!TryPrepareUserDataFile(
                        tree,
                        entry,
                        expectedChildFinalPath,
                        out error))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error =
                    "无法完成用户数据目录预检 " + directory + "：" +
                    exception.Message;
                return false;
            }
            finally
            {
                if (!transferred && handle != null)
                {
                    handle.Dispose();
                }
            }
        }

        private static bool TryPrepareUserDataFile(
            PreparedUserDataTree tree,
            string file,
            string expectedFinalPath,
            out string error)
        {
            error = string.Empty;
            SafeFileHandle handle = null;
            bool transferred = false;
            try
            {
                int openError;
                handle = OpenNativeUserDataFile(file, out openError);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    handle = null;
                    error = CreateNativeException(
                        openError,
                        "用户数据文件被占用或无法独占验证 " + file).Message;
                    return false;
                }

                ByHandleFileInformation information =
                    GetNativeInformation(handle);
                string finalPath = GetFinalHandlePath(handle);
                if ((information.FileAttributes & FileAttributeDirectory) != 0 ||
                    (information.FileAttributes & FileAttributeReparsePoint) != 0 ||
                    (information.FileAttributes & FileAttributeReadOnly) != 0 ||
                    !IsStrictChildPath(tree.CanonicalRoot, finalPath) ||
                    !string.Equals(
                        finalPath,
                        expectedFinalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error =
                        "用户数据文件是只读文件、重解析点或身份不一致，删除已停止：" +
                        file;
                    return false;
                }

                PreparedUserDataFile prepared = new PreparedUserDataFile();
                prepared.LogicalPath = file;
                prepared.FinalPath = finalPath;
                prepared.Identity = information;
                prepared.Handle = handle;
                tree.Files.Add(prepared);
                transferred = true;
                return true;
            }
            catch (Exception exception)
            {
                error =
                    "无法完成用户数据文件预检 " + file + "：" +
                    exception.Message;
                return false;
            }
            finally
            {
                if (!transferred && handle != null)
                {
                    handle.Dispose();
                }
            }
        }

        private static bool ValidatePreparedUserDataTree(
            PreparedUserDataTree tree,
            out string error)
        {
            error = string.Empty;
            try
            {
                int index;
                for (index = 0; index < tree.Directories.Count; index++)
                {
                    PreparedUserDataDirectory directory =
                        tree.Directories[index];
                    ByHandleFileInformation current =
                        GetNativeInformation(directory.Handle);
                    if (!HasSameNativeIdentity(directory.Identity, current) ||
                        (current.FileAttributes & FileAttributeDirectory) == 0 ||
                        (current.FileAttributes & FileAttributeReparsePoint) != 0 ||
                        !string.Equals(
                            GetFinalHandlePath(directory.Handle),
                            directory.FinalPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        error =
                            "用户数据目录身份在提交前发生变化，未删除任何用户数据：" +
                            directory.LogicalPath;
                        return false;
                    }

                    string[] entries = Directory.GetFileSystemEntries(
                        directory.LogicalPath);
                    Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
                    if (entries.Length != directory.EntryNames.Count)
                    {
                        error =
                            "用户数据目录内容在提交前发生变化，未删除任何用户数据：" +
                            directory.LogicalPath;
                        return false;
                    }

                    int entryIndex;
                    for (entryIndex = 0;
                        entryIndex < entries.Length;
                        entryIndex++)
                    {
                        if (!string.Equals(
                            Path.GetFileName(entries[entryIndex]),
                            directory.EntryNames[entryIndex],
                            StringComparison.OrdinalIgnoreCase))
                        {
                            error =
                                "用户数据目录内容在提交前发生变化，未删除任何用户数据：" +
                                directory.LogicalPath;
                            return false;
                        }
                    }
                }

                for (index = 0; index < tree.Files.Count; index++)
                {
                    PreparedUserDataFile file = tree.Files[index];
                    ByHandleFileInformation current =
                        GetNativeInformation(file.Handle);
                    if (!HasSameNativeIdentity(file.Identity, current) ||
                        (current.FileAttributes & FileAttributeDirectory) != 0 ||
                        (current.FileAttributes & FileAttributeReparsePoint) != 0 ||
                        !string.Equals(
                            GetFinalHandlePath(file.Handle),
                            file.FinalPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        error =
                            "用户数据文件身份在提交前发生变化，未删除任何用户数据：" +
                            file.LogicalPath;
                        return false;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error =
                    "用户数据完整预检失败，未删除任何用户数据：" +
                    exception.Message;
                return false;
            }
        }

        private static bool CommitPreparedUserDataTree(
            PreparedUserDataTree tree,
            out string error)
        {
            error = string.Empty;
            int markedFiles = 0;
            try
            {
                for (markedFiles = 0;
                    markedFiles < tree.Files.Count;
                    markedFiles++)
                {
                    SetHandleDeletion(
                        tree.Files[markedFiles].Handle,
                        true);
                    tree.Files[markedFiles].DeleteMarked = true;
                }
            }
            catch (Exception exception)
            {
                bool rollbackSucceeded = true;
                int rollbackIndex;
                for (rollbackIndex = markedFiles - 1;
                    rollbackIndex >= 0;
                    rollbackIndex--)
                {
                    try
                    {
                        SetHandleDeletion(
                            tree.Files[rollbackIndex].Handle,
                            false);
                        tree.Files[rollbackIndex].DeleteMarked = false;
                    }
                    catch
                    {
                        rollbackSucceeded = false;
                    }
                }

                error = rollbackSucceeded
                    ? "用户数据提交删除失败；删除标记已全部撤销，未删除任何用户数据：" +
                        exception.Message
                    : "用户数据提交删除失败，且 Windows 未能撤销全部删除标记：" +
                        exception.Message;
                return false;
            }

            int index;
            for (index = 0; index < tree.Files.Count; index++)
            {
                tree.Files[index].Handle.Dispose();
                tree.Files[index].Handle = null;
            }

            try
            {
                for (index = tree.Directories.Count - 1;
                    index >= 0;
                    index--)
                {
                    PreparedUserDataDirectory directory =
                        tree.Directories[index];
                    ByHandleFileInformation current =
                        GetNativeInformation(directory.Handle);
                    if (!HasSameNativeIdentity(directory.Identity, current) ||
                        !string.Equals(
                            GetFinalHandlePath(directory.Handle),
                            directory.FinalPath,
                            StringComparison.OrdinalIgnoreCase) ||
                        Directory.GetFileSystemEntries(
                            directory.LogicalPath).Length != 0)
                    {
                        throw new InvalidOperationException(
                            "目录内容或身份在提交阶段发生变化：" +
                            directory.LogicalPath);
                    }

                    MarkHandleForDeletion(directory.Handle);
                    directory.DeleteMarked = true;
                    directory.Handle.Dispose();
                    directory.Handle = null;
                }

                if (Directory.Exists(tree.LogicalRoot))
                {
                    throw new IOException(
                        "Windows 未能移除已清空的用户数据根目录。");
                }

                return true;
            }
            catch (Exception exception)
            {
                error =
                    "用户数据文件已通过完整预检，但目录提交删除未能完成：" +
                    exception.Message;
                return false;
            }
        }

        private static SafeFileHandle OpenNativeUserDataDirectory(
            string path,
            out int error)
        {
            SafeFileHandle handle = CreateFile(
                path,
                DeleteAccess | FileReadAttributes,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);
            error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            return handle;
        }

        private static SafeFileHandle OpenNativeUserDataFile(
            string path,
            out int error)
        {
            SafeFileHandle handle = CreateFile(
                path,
                DeleteAccess | FileReadAttributes,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);
            error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            return handle;
        }

        private static bool HasSameNativeIdentity(
            ByHandleFileInformation expected,
            ByHandleFileInformation actual)
        {
            return expected.VolumeSerialNumber == actual.VolumeSerialNumber &&
                expected.FileIndexHigh == actual.FileIndexHigh &&
                expected.FileIndexLow == actual.FileIndexLow;
        }

        private static void DisposePreparedUserDataTree(
            PreparedUserDataTree tree)
        {
            int index;
            for (index = tree.Files.Count - 1; index >= 0; index--)
            {
                if (tree.Files[index].Handle != null)
                {
                    tree.Files[index].Handle.Dispose();
                    tree.Files[index].Handle = null;
                }
            }

            for (index = tree.Directories.Count - 1; index >= 0; index--)
            {
                if (tree.Directories[index].Handle != null)
                {
                    tree.Directories[index].Handle.Dispose();
                    tree.Directories[index].Handle = null;
                }
            }
        }

        private static string GetDefaultDataRoot()
        {
            string local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (local == null || local.Trim().Length == 0)
            {
                throw new InvalidOperationException(
                    "Windows 未返回当前用户的 LocalAppData 目录。");
            }

            string localRoot = TrimTrailingSeparators(
                Path.GetFullPath(local));
            string dataRoot = TrimTrailingSeparators(Path.GetFullPath(Path.Combine(
                localRoot,
                DataDirectoryName)));
            if (!IsStrictChildPath(localRoot, dataRoot) ||
                !string.Equals(
                    Path.GetFileName(dataRoot),
                    DataDirectoryName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Windows 返回的默认用户数据目录未通过安全边界校验。");
            }

            return dataRoot;
        }

        private static string GetExpectedDataRoot()
        {
            return GetDefaultDataRoot();
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

        private sealed class PreparedFile
        {
            public ManifestEntry Entry;
            public SafeFileHandle Handle;
            public FileStream Stream;
            public bool DeleteMarked;
        }

        private sealed class PreparedUserDataTree
        {
            public string LogicalRoot;
            public string CanonicalRoot;
            public readonly List<PreparedUserDataDirectory> Directories =
                new List<PreparedUserDataDirectory>();
            public readonly List<PreparedUserDataFile> Files =
                new List<PreparedUserDataFile>();
        }

        private sealed class PreparedUserDataDirectory
        {
            public string LogicalPath;
            public string FinalPath;
            public ByHandleFileInformation Identity;
            public SafeFileHandle Handle;
            public readonly List<string> EntryNames = new List<string>();
            public bool DeleteMarked;
        }

        private sealed class PreparedUserDataFile
        {
            public string LogicalPath;
            public string FinalPath;
            public ByHandleFileInformation Identity;
            public SafeFileHandle Handle;
            public bool DeleteMarked;
        }

        private sealed class UninstallResult
        {
            public readonly List<string> Warnings = new List<string>();
            public int DeletedFiles;
            public int MissingFiles;
            public int ModifiedFiles;
            public int FailedFiles;
            public int AlreadyRemovedFiles;
            public int DeletedDirectories;
            public bool ManifestDeleted;
            public bool PartialDeletion;
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
                string dataDeletionError;
                if (!CanDeleteDefaultUserData(out dataDeletionError))
                {
                    deleteDataCheckBox.Enabled = false;
                    deleteDataCheckBox.AutoSize = false;
                    deleteDataCheckBox.Size = new Size(466, 44);
                    deleteDataCheckBox.Text =
                        "当前使用自定义数据目录；为避免误删，用户数据将保留。";
                }
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
