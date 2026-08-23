using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace FilePromptAIWin7
{
    internal sealed class AppSettings
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("FilePromptAIWin7.Settings.v1");

        public string EndpointUrl { get; set; }
        public string ApiKey { get; set; }
        public string ModelName { get; set; }
        public string SendShortcut { get; set; }
        public string SystemPrompt { get; set; }
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public int? MaxOutputTokens { get; set; }

        public static string SettingsPath
        {
            get
            {
                return Path.Combine(AppDataPath.Root, "settings.xml");
            }
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            settings.EndpointUrl = string.Empty;
            settings.ApiKey = string.Empty;
            settings.ModelName = string.Empty;
            settings.SendShortcut = "Both";
            settings.SystemPrompt = string.Empty;
            settings.Temperature = null;
            settings.TopP = null;
            settings.MaxOutputTokens = null;

            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return settings;
                }

                XDocument document = XDocument.Load(SettingsPath);
                XElement root = document.Root;
                if (root == null ||
                    root.Name != "FilePromptAISettings" ||
                    (string)root.Attribute("version") != "1")
                {
                    return settings;
                }

                settings.EndpointUrl = GetValue(root, "EndpointUrl");
                settings.ModelName = GetValue(root, "ModelName");
                settings.SendShortcut = GetValue(root, "SendShortcut");
                settings.SystemPrompt = GetValue(root, "SystemPrompt");
                settings.Temperature = ParseOptionalDouble(
                    GetValue(root, "Temperature"),
                    0d,
                    2d);
                settings.TopP = ParseOptionalDouble(
                    GetValue(root, "TopP"),
                    0d,
                    1d);
                settings.MaxOutputTokens = ParseOptionalInt32(
                    GetValue(root, "MaxOutputTokens"),
                    1,
                    1048576);
                string protectedKey = GetValue(root, "ProtectedApiKey");
                if (!string.IsNullOrWhiteSpace(protectedKey))
                {
                    settings.ApiKey = Unprotect(protectedKey);
                }
            }
            catch
            {
                // A damaged or machine-bound settings file should not prevent startup.
            }

            return settings;
        }

        public void Save()
        {
            string directory = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            XDocument document = new XDocument(
                new XElement(
                    "FilePromptAISettings",
                    new XAttribute("version", "1"),
                    new XElement("EndpointUrl", EndpointUrl ?? string.Empty),
                    new XElement("ModelName", ModelName ?? string.Empty),
                    new XElement("SendShortcut", SendShortcut ?? "Both"),
                    new XElement("SystemPrompt", SystemPrompt ?? string.Empty),
                    new XElement(
                        "Temperature",
                        FormatOptionalDouble(Temperature, 0d, 2d)),
                    new XElement(
                        "TopP",
                        FormatOptionalDouble(TopP, 0d, 1d)),
                    new XElement(
                        "MaxOutputTokens",
                        FormatOptionalInt32(
                            MaxOutputTokens,
                            1,
                            1048576)),
                    new XElement("ProtectedApiKey", Protect(ApiKey ?? string.Empty))));

            AtomicFile.WriteAllText(
                SettingsPath,
                document.ToString(),
                new UTF8Encoding(true));
        }

        private static string GetValue(XElement root, string name)
        {
            XElement element = root.Element(name);
            return element == null ? string.Empty : element.Value;
        }

        private static double? ParseOptionalDouble(
            string value,
            double minimum,
            double maximum)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            double parsed;
            if (!double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed) ||
                double.IsNaN(parsed) ||
                double.IsInfinity(parsed) ||
                parsed < minimum ||
                parsed > maximum)
            {
                return null;
            }

            return parsed;
        }

        private static int? ParseOptionalInt32(
            string value,
            int minimum,
            int maximum)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            int parsed;
            if (!int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed) ||
                parsed < minimum ||
                parsed > maximum)
            {
                return null;
            }

            return parsed;
        }

        private static string FormatOptionalDouble(
            double? value,
            double minimum,
            double maximum)
        {
            if (!value.HasValue)
            {
                return string.Empty;
            }

            double actual = value.Value;
            if (double.IsNaN(actual) || double.IsInfinity(actual) ||
                actual < minimum || actual > maximum)
            {
                throw new InvalidOperationException("生成参数超出允许范围。");
            }

            return actual.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string FormatOptionalInt32(
            int? value,
            int minimum,
            int maximum)
        {
            if (!value.HasValue)
            {
                return string.Empty;
            }

            if (value.Value < minimum || value.Value > maximum)
            {
                throw new InvalidOperationException("生成参数超出允许范围。");
            }

            return value.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            byte[] clear = Encoding.UTF8.GetBytes(value);
            byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private static string Unprotect(string value)
        {
            byte[] encrypted = Convert.FromBase64String(value);
            byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
    }
}
