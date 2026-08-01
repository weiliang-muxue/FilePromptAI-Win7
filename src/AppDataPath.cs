using System;
using System.IO;

namespace FilePromptWin7
{
    internal static class AppDataPath
    {
        private const string OverrideVariable = "FILEPROMPT_DATA_ROOT";

        public static string Root
        {
            get
            {
                string overridden = Environment.GetEnvironmentVariable(
                    OverrideVariable);
                if (!string.IsNullOrWhiteSpace(overridden))
                {
                    return Path.GetFullPath(overridden.Trim());
                }

                string local = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "FilePromptWin7");
            }
        }
    }
}
