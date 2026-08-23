using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FilePromptAIWin7
{
    // A named connection preset. The API key is only held in memory here;
    // ModelProfileStore protects it before writing the profile file.
    internal sealed class ModelProfile
    {
        public string Name { get; set; }
        public string EndpointUrl { get; set; }
        public string ApiKey { get; set; }
        public string ModelName { get; set; }
        public string SystemPrompt { get; set; }
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public int? MaxOutputTokens { get; set; }

        public ModelProfile Clone()
        {
            return new ModelProfile
            {
                Name = Name ?? string.Empty,
                EndpointUrl = EndpointUrl ?? string.Empty,
                ApiKey = ApiKey ?? string.Empty,
                ModelName = ModelName ?? string.Empty,
                SystemPrompt = SystemPrompt ?? string.Empty,
                Temperature = Temperature,
                TopP = TopP,
                MaxOutputTokens = MaxOutputTokens
            };
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name)
                ? "未命名配置"
                : Name;
        }
    }

    internal sealed class ModelProfileStore
    {
        private const string RootName = "FilePromptAIModelProfiles";
        private const string CurrentVersion = "1";
        private const int MaximumProfiles = 64;
        private const int MaximumFieldCharacters = 4096;
        private const int MaximumSystemPromptCharacters = 16000;
        private const int MaximumSettingsBytes = 1024 * 1024;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "FilePromptAIWin7.ModelProfiles.v1");

        private readonly string path;

        public string LoadWarning { get; private set; }

        public string SettingsPath
        {
            get { return path; }
        }

        public ModelProfileStore()
            : this(Path.Combine(AppDataPath.Root, "model-profiles.xml"))
        {
        }

        public ModelProfileStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Profile path is required.", "path");
            }

            this.path = path;
        }

        public IList<ModelProfile> Load()
        {
            LoadWarning = string.Empty;
            List<ModelProfile> result = new List<ModelProfile>();
            if (!File.Exists(path))
            {
                return result;
            }

            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length > MaximumSettingsBytes)
                {
                    throw new InvalidDataException(
                        "模型配置文件超过 1 MB 安全限制。");
                }

                XmlReaderSettings readerSettings = new XmlReaderSettings();
                readerSettings.DtdProcessing = DtdProcessing.Prohibit;
                readerSettings.XmlResolver = null;
                readerSettings.MaxCharactersInDocument = MaximumSettingsBytes;
                readerSettings.MaxCharactersFromEntities = 0;
                XDocument document;
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                using (XmlReader reader = XmlReader.Create(
                    stream,
                    readerSettings))
                {
                    document = XDocument.Load(reader, LoadOptions.None);
                }
                XElement root = document.Root;
                if (root == null || root.Name != RootName ||
                    (string)root.Attribute("version") != CurrentVersion)
                {
                    throw new InvalidDataException("模型配置文件版本不受支持。");
                }

                foreach (XElement element in root.Elements("Profile"))
                {
                    if (result.Count >= MaximumProfiles)
                    {
                        break;
                    }

                    string protectedKey = GetValue(element, "ProtectedApiKey");
                    string apiKey = string.Empty;
                    bool keyDecryptionFailed = false;
                    if (!string.IsNullOrWhiteSpace(protectedKey))
                    {
                        try
                        {
                            apiKey = Unprotect(protectedKey);
                        }
                        catch
                        {
                            // DPAPI is scoped to the Windows user. Entries
                            // copied from another account are skipped while
                            // the original file remains untouched.
                            keyDecryptionFailed = true;
                        }
                    }

                    if (keyDecryptionFailed)
                    {
                        continue;
                    }

                    ModelProfile profile = new ModelProfile
                    {
                        Name = Limit(GetValue(element, "Name")),
                        EndpointUrl = Limit(GetValue(element, "EndpointUrl")),
                        ApiKey = Limit(apiKey),
                        ModelName = Limit(GetValue(element, "ModelName")),
                        SystemPrompt = LimitSystemPrompt(
                            GetValue(element, "SystemPrompt")),
                        Temperature = ParseOptionalDouble(
                            GetValue(element, "Temperature"),
                            0d,
                            2d),
                        TopP = ParseOptionalDouble(
                            GetValue(element, "TopP"),
                            0d,
                            1d),
                        MaxOutputTokens = ParseOptionalInt32(
                            GetValue(element, "MaxOutputTokens"),
                            1,
                            1048576)
                    };
                    try
                    {
                        Validate(profile);
                    }
                    catch
                    {
                        // A single incomplete or cross-user DPAPI entry must
                        // not block the remaining saved profiles.
                        continue;
                    }

                    if (result.Any(existing => string.Equals(
                        existing.Name,
                        profile.Name,
                        StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    result.Add(profile);
                }
            }
            catch (Exception exception)
            {
                LoadWarning = "模型配置未能读取，已忽略损坏文件：" + exception.Message;
                TryMoveCorruptFile();
                result.Clear();
            }

            return result;
        }

        public void Save(IEnumerable<ModelProfile> profiles)
        {
            if (profiles == null)
            {
                throw new ArgumentNullException("profiles");
            }

            List<ModelProfile> normalized = new List<ModelProfile>();
            foreach (ModelProfile source in profiles)
            {
                if (source == null)
                {
                    continue;
                }

                ModelProfile profile = source.Clone();
                Validate(profile);
                if (normalized.Any(existing => string.Equals(
                    existing.Name,
                    profile.Name,
                    StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        "模型配置名称不能重复：" + profile.Name);
                }

                normalized.Add(profile);
                if (normalized.Count > MaximumProfiles)
                {
                    throw new InvalidOperationException(
                        "模型配置最多保存 " + MaximumProfiles + " 个。");
                }
            }

            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            XElement root = new XElement(
                RootName,
                new XAttribute("version", CurrentVersion));
            foreach (ModelProfile profile in normalized)
            {
                root.Add(new XElement(
                    "Profile",
                    new XElement("Name", profile.Name),
                    new XElement("EndpointUrl", profile.EndpointUrl),
                    new XElement("ModelName", profile.ModelName),
                    new XElement("SystemPrompt", profile.SystemPrompt),
                    new XElement(
                        "Temperature",
                        FormatOptionalDouble(profile.Temperature)),
                    new XElement("TopP", FormatOptionalDouble(profile.TopP)),
                    new XElement(
                        "MaxOutputTokens",
                        profile.MaxOutputTokens.HasValue
                            ? profile.MaxOutputTokens.Value.ToString(
                                CultureInfo.InvariantCulture)
                            : string.Empty),
                    new XElement("ProtectedApiKey", Protect(profile.ApiKey))));
            }

            AtomicFile.WriteAllText(
                path,
                new XDocument(root).ToString(),
                new UTF8Encoding(true));
        }

        public static void Validate(ModelProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            profile.Name = (profile.Name ?? string.Empty).Trim();
            profile.EndpointUrl = (profile.EndpointUrl ?? string.Empty).Trim();
            profile.ApiKey = (profile.ApiKey ?? string.Empty).Trim();
            profile.ModelName = (profile.ModelName ?? string.Empty).Trim();
            profile.SystemPrompt = (profile.SystemPrompt ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                throw new InvalidOperationException("模型配置名称不能为空。");
            }

            if (profile.Name.Length > MaximumFieldCharacters ||
                profile.EndpointUrl.Length > MaximumFieldCharacters ||
                profile.ApiKey.Length > MaximumFieldCharacters ||
                profile.ModelName.Length > MaximumFieldCharacters)
            {
                throw new InvalidOperationException(
                    "模型配置字段长度不能超过 " + MaximumFieldCharacters + " 个字符。");
            }

            if (profile.SystemPrompt.Length > MaximumSystemPromptCharacters)
            {
                throw new InvalidOperationException(
                    "系统提示词不能超过 " +
                    MaximumSystemPromptCharacters + " 个字符。");
            }

            Uri endpoint;
            if (!Uri.TryCreate(
                profile.EndpointUrl,
                UriKind.Absolute,
                out endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp &&
                 endpoint.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(endpoint.UserInfo))
            {
                throw new InvalidOperationException(
                    "请求 URL 必须是完整且不含用户名密码的 http:// 或 https:// 地址。");
            }

            if (string.IsNullOrWhiteSpace(profile.ModelName))
            {
                throw new InvalidOperationException("模型名称不能为空。");
            }

            if (profile.Temperature.HasValue &&
                (double.IsNaN(profile.Temperature.Value) ||
                 double.IsInfinity(profile.Temperature.Value) ||
                 profile.Temperature.Value < 0d ||
                 profile.Temperature.Value > 2d))
            {
                throw new InvalidOperationException(
                    "Temperature 必须在 0 到 2 之间。");
            }

            if (profile.TopP.HasValue &&
                (double.IsNaN(profile.TopP.Value) ||
                 double.IsInfinity(profile.TopP.Value) ||
                 profile.TopP.Value < 0d ||
                 profile.TopP.Value > 1d))
            {
                throw new InvalidOperationException("Top P 必须在 0 到 1 之间。");
            }

            if (profile.MaxOutputTokens.HasValue &&
                (profile.MaxOutputTokens.Value < 1 ||
                 profile.MaxOutputTokens.Value > 1048576))
            {
                throw new InvalidOperationException(
                    "最大输出 Token 必须在 1 到 1,048,576 之间。");
            }
        }


        private static string GetValue(XElement parent, string name)
        {
            XElement value = parent.Element(name);
            return value == null ? string.Empty : value.Value;
        }

        private static string Limit(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return value.Length <= MaximumFieldCharacters
                ? value
                : value.Substring(0, MaximumFieldCharacters);
        }

        private static string LimitSystemPrompt(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return value.Length <= MaximumSystemPromptCharacters
                ? value
                : value.Substring(0, MaximumSystemPromptCharacters);
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
            return double.TryParse(
                       value,
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out parsed) &&
                   !double.IsNaN(parsed) &&
                   !double.IsInfinity(parsed) &&
                   parsed >= minimum && parsed <= maximum
                ? (double?)parsed
                : null;
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
            return int.TryParse(
                       value,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out parsed) &&
                   parsed >= minimum && parsed <= maximum
                ? (int?)parsed
                : null;
        }

        private static string FormatOptionalDouble(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.##", CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            byte[] clear = Encoding.UTF8.GetBytes(value);
            byte[] encrypted = ProtectedData.Protect(
                clear,
                Entropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private static string Unprotect(string value)
        {
            byte[] encrypted = Convert.FromBase64String(value);
            byte[] clear = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }

        private void TryMoveCorruptFile()
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                string backup = path + ".corrupt-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".xml";
                File.Move(path, backup);
            }
            catch
            {
                // Keep startup resilient if the profile file is locked.
            }
        }
    }
}
