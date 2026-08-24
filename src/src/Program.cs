using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FilePromptAIWin7
{
    internal static class Program
    {
        private const string InstanceMutexName =
            @"Local\FilePromptAIWin7.Singleton.77e99c24-2d55-4fa0-9a90-2b498733335a";

        [STAThread]
        private static void Main()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.Expect100Continue = false;
            }
            catch
            {
                // Windows 7 machines without the required updates will receive a clearer
                // TLS error from the request layer.
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            bool createdNew;
            using (Mutex instanceMutex = new Mutex(
                true,
                InstanceMutexName,
                out createdNew))
            {
                if (!createdNew)
                {
                    IntPtr existingWindow = FindWindow(
                        null,
                        MainForm.WindowTitle);
                    if (existingWindow != IntPtr.Zero)
                    {
                        ShowWindow(existingWindow, 9);
                        SetForegroundWindow(existingWindow);
                    }
                    else
                    {
                        MessageBox.Show(
                            "FilePrompt AI 已经在运行。请切换到现有窗口继续使用。",
                            "FilePrompt AI",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }

                    return;
                }

                Application.Run(new MainForm());
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ShowFatalError(e.Exception);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ShowFatalError(e.ExceptionObject as Exception);
        }

        private static void ShowFatalError(Exception exception)
        {
            string message = exception == null ? "发生未知错误。" : exception.Message;
            MessageBox.Show(
                "程序遇到错误：\r\n\r\n" + message,
                "FilePrompt AI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(
            string className,
            string windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);
    }
}
