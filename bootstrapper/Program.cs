using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Forms;

namespace FilePromptAIBootstrapper
{
    internal static class Program
    {
        private const int Net48Release = 528040;
        private const string RuntimeFileName = "NDP48-x86-x64-AllOS-ENU.exe";
        private const long RuntimeFileLength = 121346568;
        private const string RuntimeSha256 =
            "0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo : IDisposable
        {
            public uint StructSize = (uint)Marshal.SizeOf(
                typeof(WinTrustFileInfo));
            public IntPtr FilePath;
            public IntPtr FileHandle = IntPtr.Zero;
            public IntPtr KnownSubject = IntPtr.Zero;

            public WinTrustFileInfo(string path)
            {
                FilePath = Marshal.StringToCoTaskMemUni(path);
            }

            public void Dispose()
            {
                if (FilePath != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(FilePath);
                    FilePath = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustData : IDisposable
        {
            public uint StructSize = (uint)Marshal.SizeOf(
                typeof(WinTrustData));
            public IntPtr PolicyCallbackData = IntPtr.Zero;
            public IntPtr SipClientData = IntPtr.Zero;
            public uint UiChoice = 2;
            public uint RevocationChecks = 0;
            public uint UnionChoice = 1;
            public IntPtr FileInfo;
            public uint StateAction = 0;
            public IntPtr StateData = IntPtr.Zero;
            public IntPtr UrlReference = IntPtr.Zero;
            public uint ProviderFlags = 0x00001010;
            public uint UiContext = 0;

            public WinTrustData(WinTrustFileInfo file)
            {
                FileInfo = Marshal.AllocCoTaskMem(
                    Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(file, FileInfo, false);
            }

            public void Dispose()
            {
                if (FileInfo != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(FileInfo);
                    FileInfo = IntPtr.Zero;
                }
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true,
            SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(
            IntPtr window,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            WinTrustData data);

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string root = AppDomain.CurrentDomain.BaseDirectory;
            string applicationPath = Path.Combine(root, "app\\FilePromptAI.exe");
            string[] arguments = Environment.GetCommandLineArgs();
            bool checkOnly = arguments.Length > 1 &&
                string.Equals(arguments[1], "--check", StringComparison.OrdinalIgnoreCase);
            bool verifyRuntimeOnly = arguments.Length > 1 &&
                string.Equals(
                    arguments[1],
                    "--verify-runtime",
                    StringComparison.OrdinalIgnoreCase);
            if (verifyRuntimeOnly)
            {
                string runtimePath = Path.Combine(
                    root,
                    "runtime\\" + RuntimeFileName);
                Environment.ExitCode = File.Exists(runtimePath) &&
                    IsApprovedOfflineRuntime(runtimePath)
                    ? 0
                    : 4;
                return;
            }

            if (!File.Exists(applicationPath))
            {
                if (checkOnly)
                {
                    Environment.ExitCode = 3;
                    return;
                }

                ShowError(
                    "没有找到主程序：\r\n\r\n" + applicationPath +
                    "\r\n\r\n请先完整解压安装包，不要只复制启动器。");
                return;
            }

            if (IsNet48Installed())
            {
                if (checkOnly)
                {
                    Environment.ExitCode = 0;
                    return;
                }

                StartApplication(applicationPath);
                return;
            }

            if (checkOnly)
            {
                Environment.ExitCode = 2;
                return;
            }

            if (IsWindows7WithoutServicePack1())
            {
                ShowError(
                    "当前系统是 Windows 7，但没有检测到 Service Pack 1。\r\n\r\n" +
                    ".NET Framework 4.8 只能安装在 Windows 7 SP1 上。" +
                    "请先安装 Windows 7 Service Pack 1 和系统更新，再运行此启动器。");
                return;
            }

            DialogResult answer = MessageBox.Show(
                "FilePrompt AI 需要 Microsoft .NET Framework 4.8。\r\n\r\n" +
                "当前电脑尚未安装。是否现在使用安装包内的微软官方离线程序安装？\r\n\r\n" +
                "安装过程需要管理员权限。",
                "安装运行环境",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            string installerPath = Path.Combine(root, "runtime\\" + RuntimeFileName);
            if (!File.Exists(installerPath))
            {
                ShowError(
                    "安装包中缺少微软运行环境安装程序：\r\n\r\n" + installerPath +
                    "\r\n\r\n请重新下载完整离线版安装包。");
                return;
            }

            if (!IsApprovedOfflineRuntime(installerPath))
            {
                ShowError(
                    "随包运行环境未通过完整性校验，因此没有启动安装。\r\n\r\n" +
                    "请删除当前文件，重新取得完整的 FilePrompt AI 离线包。\r\n\r\n" +
                    "校验目标：Microsoft .NET Framework 4.8 官方离线安装程序。");
                return;
            }

            InstallRuntime(installerPath, applicationPath);
        }

        private static bool IsApprovedOfflineRuntime(string installerPath)
        {
            try
            {
                FileInfo installer = new FileInfo(installerPath);
                if (installer.Length != RuntimeFileLength)
                {
                    return false;
                }

                byte[] digest;
                using (FileStream stream = new FileStream(
                    installerPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                using (SHA256 algorithm = new SHA256CryptoServiceProvider())
                {
                    digest = algorithm.ComputeHash(stream);
                }

                string actualHash = BitConverter.ToString(digest).Replace("-", string.Empty);
                if (!string.Equals(
                    actualHash,
                    RuntimeSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                X509Certificate certificate =
                    X509Certificate.CreateFromSignedFile(installerPath);
                if (certificate == null || certificate.Subject == null ||
                    certificate.Subject.IndexOf(
                        "O=Microsoft Corporation",
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }

                Guid action = new Guid(
                    "00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                using (WinTrustFileInfo file =
                    new WinTrustFileInfo(installerPath))
                using (WinTrustData trust = new WinTrustData(file))
                {
                    return WinVerifyTrust(
                        new IntPtr(-1),
                        action,
                        trust) == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNet48Installed()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    object releaseValue = key.GetValue("Release");
                    if (releaseValue == null)
                    {
                        return false;
                    }

                    return Convert.ToInt32(releaseValue) >= Net48Release;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsWindows7WithoutServicePack1()
        {
            Version version = Environment.OSVersion.Version;
            if (version.Major != 6 || version.Minor != 1)
            {
                return false;
            }

            string servicePack = Environment.OSVersion.ServicePack;
            return string.IsNullOrEmpty(servicePack) ||
                servicePack.IndexOf("1", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static void InstallRuntime(string installerPath, string applicationPath)
        {
            Process process = null;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = installerPath;
                startInfo.Arguments = "/passive /norestart";
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                process = Process.Start(startInfo);
                if (process == null)
                {
                    ShowError("无法启动 .NET Framework 4.8 安装程序。");
                    return;
                }

                process.WaitForExit();
                int exitCode = process.ExitCode;
                if (exitCode == 0 && IsNet48Installed())
                {
                    MessageBox.Show(
                        ".NET Framework 4.8 安装完成。现在启动 FilePrompt AI。",
                        "安装完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    StartApplication(applicationPath);
                    return;
                }

                if (exitCode == 3010 || exitCode == 1641 ||
                    (exitCode == 0 && !IsNet48Installed()))
                {
                    MessageBox.Show(
                        "运行环境已经安装，但 Windows 需要重新启动。\r\n\r\n" +
                        "重启电脑后，再双击“Start-FilePromptAI.exe”。",
                        "需要重新启动",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                ShowError(
                    ".NET Framework 4.8 安装未成功。\r\n\r\n" +
                    "安装程序返回代码：" + exitCode + "\r\n\r\n" +
                    "Windows 7 请确认已安装 SP1、SHA-2 支持和最新的系统根证书更新。");
            }
            catch (Win32Exception exception)
            {
                if (exception.NativeErrorCode == 1223)
                {
                    MessageBox.Show(
                        "安装已取消。FilePrompt AI 尚不能启动。",
                        "安装运行环境",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                ShowError("启动安装程序失败：\r\n\r\n" + exception.Message);
            }
            catch (Exception exception)
            {
                ShowError("安装运行环境时发生错误：\r\n\r\n" + exception.Message);
            }
            finally
            {
                if (process != null)
                {
                    process.Dispose();
                }
            }
        }

        private static void StartApplication(string applicationPath)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = applicationPath;
                startInfo.WorkingDirectory = Path.GetDirectoryName(applicationPath);
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                ShowError(
                    "主程序启动失败：\r\n\r\n" + exception.Message +
                    "\r\n\r\n请确认已完整解压安装包，并保留 app 目录中的所有 DLL 文件。");
            }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(
                message,
                "FilePrompt AI 无法启动",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
