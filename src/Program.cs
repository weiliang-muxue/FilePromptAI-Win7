using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace FilePromptWin7
{
    internal static class Program
    {
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
            Application.Run(new MainForm());
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
                "FilePrompt",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
