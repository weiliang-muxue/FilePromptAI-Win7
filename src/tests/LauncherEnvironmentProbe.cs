using System;
using System.IO;
using System.Text;

internal static class LauncherEnvironmentProbe
{
    private const string DataRootVariable = "FILEPROMPTAI_DATA_ROOT";
    private const string ReportPrefix = "launcher-environment-";

    private static int Main()
    {
        string directory = Path.GetFullPath(
            AppDomain.CurrentDomain.BaseDirectory);
        string nonce = Guid.NewGuid().ToString("N");
        string reportPath = Path.Combine(
            directory,
            ReportPrefix + nonce + ".txt");
        string temporaryPath = reportPath + ".tmp";

        try
        {
            string dataRoot = Environment.GetEnvironmentVariable(
                DataRootVariable) ?? string.Empty;
            File.WriteAllText(
                temporaryPath,
                dataRoot,
                new UTF8Encoding(false));
            File.Move(temporaryPath, reportPath);
            return 0;
        }
        catch
        {
            return 1;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
        }
    }
}
