using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FilePromptAIWin7
{
    internal sealed class AppSettings
    {
        private const int MaximumSettingsBytes = 1024 * 1024;
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("FilePromptAIWin7.Settings.v1");
        private static readonly object ProtectionSync = new object();
        private static readonly Dictionary<string, string> WriteBlocks =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly string settingsPath;
        private string loadWarning;

        public string EndpointUrl { get; set; }
        public string ApiKey { get; set; }
        public string ModelName { get; set; }
        public string SendShortcut { get; set; }
        public string SystemPrompt { get; set; }
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public int? MaxOutputTokens { get; set; }

        public string LoadWarning
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(loadWarning))
                {
                    return loadWarning;
                }

                return GetWriteBlockReason(settingsPath);
            }
        }

        public bool IsWriteBlocked
        {
            get { return !string.IsNullOrEmpty(GetWriteBlockReason(settingsPath)); }
        }

        public static string SettingsPath
        {
            get
            {
                return Path.Combine(AppDataPath.Root, "settings.xml");
            }
        }

        public AppSettings()
        {
            settingsPath = Path.GetFullPath(SettingsPath);
            EndpointUrl = string.Empty;
            ApiKey = string.Empty;
            ModelName = string.Empty;
            SendShortcut = "Both";
            SystemPrompt = string.Empty;
            Temperature = null;
            TopP = null;
            MaxOutputTokens = null;
            loadWarning = string.Empty;
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            try
            {
                XDocument document = ReadDocument(settings.settingsPath);
                if (document == null)
                {
                    settings.loadWarning = GetWriteBlockReason(
                        settings.settingsPath);
                    return settings;
                }

                XElement root = document.Root;
                if (root == null ||
                    root.Name != "FilePromptAISettings" ||
                    (string)root.Attribute("version") != "1")
                {
                    throw new InvalidDataException(
                        "设置文件根节点或版本无效。");
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

                settings.loadWarning = GetWriteBlockReason(
                    settings.settingsPath);
            }
            catch (InvalidDataException exception)
            {
                settings.HandleCorruptFile(exception);
            }
            catch (XmlException exception)
            {
                settings.HandleCorruptFile(exception);
            }
            catch (FormatException exception)
            {
                settings.HandleCorruptFile(exception);
            }
            catch (CryptographicException exception)
            {
                settings.HandleCorruptFile(exception);
            }
            catch (FileNotFoundException)
            {
                settings.loadWarning = GetWriteBlockReason(
                    settings.settingsPath);
            }
            catch (DirectoryNotFoundException)
            {
                settings.loadWarning = GetWriteBlockReason(
                    settings.settingsPath);
            }
            catch (IOException exception)
            {
                settings.HandleUnavailableFile(exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                settings.HandleUnavailableFile(exception);
            }
            catch (SecurityException exception)
            {
                settings.HandleUnavailableFile(exception);
            }
            catch (Exception exception)
            {
                settings.HandleUnavailableFile(exception);
            }

            return settings;
        }

        public void Save()
        {
            string blockedReason = GetWriteBlockReason(settingsPath);
            if (!string.IsNullOrEmpty(blockedReason))
            {
                throw new InvalidOperationException(
                    "设置文件处于只读保护，本次运行不能保存。" +
                    blockedReason);
            }

            try
            {
                SaveCore();
            }
            catch (IOException exception)
            {
                HandleUnavailableFile(exception);
                throw;
            }
            catch (UnauthorizedAccessException exception)
            {
                HandleUnavailableFile(exception);
                throw;
            }
            catch (SecurityException exception)
            {
                HandleUnavailableFile(exception);
                throw;
            }
        }

        private void SaveCore()
        {
            string directory = Path.GetDirectoryName(settingsPath);
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
                settingsPath,
                document.ToString(),
                new UTF8Encoding(true));
        }

        private static XDocument ReadDocument(string path)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            using (stream)
            {
                if (stream.Length == 0 || stream.Length > MaximumSettingsBytes)
                {
                    throw new InvalidDataException(
                        "设置文件大小无效或超过 1 MB 安全限制。");
                }

                XmlReaderSettings readerSettings = new XmlReaderSettings();
                readerSettings.DtdProcessing = DtdProcessing.Prohibit;
                readerSettings.XmlResolver = null;
                readerSettings.MaxCharactersInDocument = MaximumSettingsBytes;
                readerSettings.MaxCharactersFromEntities = 0;
                using (XmlReader reader = XmlReader.Create(
                    stream,
                    readerSettings))
                {
                    return XDocument.Load(reader, LoadOptions.None);
                }
            }
        }

        private void HandleCorruptFile(Exception exception)
        {
            string blockedReason = GetWriteBlockReason(settingsPath);
            if (!string.IsNullOrEmpty(blockedReason))
            {
                loadWarning = blockedReason;
                return;
            }

            string backup = TryPreserveDamagedSettings(settingsPath);
            if (!string.IsNullOrEmpty(backup))
            {
                loadWarning = "设置文件损坏，原文件已安全保留为 " +
                    Path.GetFileName(backup) + "。（" + exception.Message + "）";
                return;
            }

            string warning = "设置文件内容损坏，但无法创建安全备份；" +
                "原文件保持不变，本次运行已进入只读保护。请先手工备份并解除占用：" +
                settingsPath + "（" + exception.Message + "）";
            MarkWriteBlocked(settingsPath, warning);
            loadWarning = warning;
        }

        private void HandleUnavailableFile(Exception exception)
        {
            string warning = "设置文件当前无法安全读取，原文件保持不变；" +
                "本次运行已进入只读保护，无法保存设置。请检查文件占用或权限：" +
                settingsPath + "（" + exception.Message + "）";
            MarkWriteBlocked(settingsPath, warning);
            loadWarning = GetWriteBlockReason(settingsPath);
        }

        private static string TryPreserveDamagedSettings(string path)
        {
            try
            {
                string backup = path + ".corrupt-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".xml";
                int suffix = 1;
                while (File.Exists(backup))
                {
                    backup = path + ".corrupt-" +
                        DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" +
                        suffix.ToString(CultureInfo.InvariantCulture) + ".xml";
                    suffix++;
                }

                File.Move(path, backup);
                return backup;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void MarkWriteBlocked(string path, string reason)
        {
            lock (ProtectionSync)
            {
                if (!WriteBlocks.ContainsKey(path))
                {
                    WriteBlocks.Add(path, reason ?? string.Empty);
                }
            }
        }

        private static string GetWriteBlockReason(string path)
        {
            lock (ProtectionSync)
            {
                string reason;
                return WriteBlocks.TryGetValue(path, out reason)
                    ? reason ?? string.Empty
                    : string.Empty;
            }
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
