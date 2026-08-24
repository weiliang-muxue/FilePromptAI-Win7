using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security;
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
        private static readonly object ProtectionSync = new object();
        private static readonly Dictionary<string, string> WriteBlocks =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly string path;
        private string loadWarning;

        public string LoadWarning
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(loadWarning))
                {
                    return loadWarning;
                }

                return GetWriteBlockReason(path);
            }
        }

        public bool IsWriteProtected
        {
            get { return !string.IsNullOrEmpty(GetWriteBlockReason(path)); }
        }

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

            this.path = Path.GetFullPath(path);
            loadWarning = GetWriteBlockReason(this.path);
        }

        public IList<ModelProfile> Load()
        {
            loadWarning = GetWriteBlockReason(path);
            List<ModelProfile> result = new List<ModelProfile>();

            try
            {
                XDocument document = ReadDocument();
                if (document == null)
                {
                    return result;
                }

                XElement root = document.Root;
                if (root == null || root.Name != RootName ||
                    (string)root.Attribute("version") != CurrentVersion)
                {
                    throw new InvalidDataException("模型配置文件版本不受支持。");
                }

                if (root.Elements().Any(element => element.Name != "Profile"))
                {
                    throw new InvalidDataException(
                        "模型配置文件包含未知节点。");
                }

                foreach (XElement element in root.Elements("Profile"))
                {
                    if (result.Count >= MaximumProfiles)
                    {
                        MarkPartialLoad(
                            "模型配置超过 " + MaximumProfiles +
                            " 条，未加载的条目已原样保留。");
                        break;
                    }

                    try
                    {
                        ValidateProfileStructure(element);
                        string protectedKey = GetValue(
                            element,
                            "ProtectedApiKey");
                        string apiKey = string.Empty;
                        if (!string.IsNullOrWhiteSpace(protectedKey))
                        {
                            apiKey = Unprotect(protectedKey);
                        }

                        string temperatureText = GetValue(
                            element,
                            "Temperature");
                        string topPText = GetValue(element, "TopP");
                        string maxTokensText = GetValue(
                            element,
                            "MaxOutputTokens");
                        bool invalidOptionalValue =
                            IsInvalidOptionalDouble(
                                temperatureText,
                                0d,
                                2d) ||
                            IsInvalidOptionalDouble(topPText, 0d, 1d) ||
                            IsInvalidOptionalInt32(
                                maxTokensText,
                                1,
                                1048576);
                        ModelProfile profile = new ModelProfile
                        {
                            Name = GetValue(element, "Name"),
                            EndpointUrl = GetValue(
                                element,
                                "EndpointUrl"),
                            ApiKey = apiKey,
                            ModelName = GetValue(element, "ModelName"),
                            SystemPrompt = GetValue(element, "SystemPrompt"),
                            Temperature = ParseOptionalDouble(
                                temperatureText,
                                0d,
                                2d),
                            TopP = ParseOptionalDouble(
                                topPText,
                                0d,
                                1d),
                            MaxOutputTokens = ParseOptionalInt32(
                                maxTokensText,
                                1,
                                1048576)
                        };
                        Validate(profile);

                        if (result.Any(existing => string.Equals(
                            existing.Name,
                            profile.Name,
                            StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new InvalidDataException(
                                "模型配置名称重复：" + profile.Name);
                        }

                        result.Add(profile);
                        if (invalidOptionalValue)
                        {
                            MarkPartialLoad(
                                "模型配置包含无法解析的生成参数，已按默认值加载；" +
                                "原文件已保留且本次运行禁止保存。");
                        }
                    }
                    catch (Exception exception)
                    {
                        MarkPartialLoad(
                            "部分模型配置无法安全读取，原文件已保留且本次运行禁止保存：" +
                            exception.Message);
                        continue;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                loadWarning = GetWriteBlockReason(path);
            }
            catch (DirectoryNotFoundException)
            {
                loadWarning = GetWriteBlockReason(path);
            }
            catch (InvalidDataException exception)
            {
                HandleCorruptFile(exception);
                result.Clear();
            }
            catch (XmlException exception)
            {
                HandleCorruptFile(exception);
                result.Clear();
            }
            catch (IOException exception)
            {
                HandleUnavailableFile(exception);
                result.Clear();
            }
            catch (UnauthorizedAccessException exception)
            {
                HandleUnavailableFile(exception);
                result.Clear();
            }
            catch (SecurityException exception)
            {
                HandleUnavailableFile(exception);
                result.Clear();
            }
            catch (Exception exception)
            {
                // Unexpected runtime failures must not be treated as proof
                // that the persisted bytes are corrupt.
                HandleUnavailableFile(exception);
                result.Clear();
            }

            return result;
        }

        public void Save(IEnumerable<ModelProfile> profiles)
        {
            string blockedReason = GetWriteBlockReason(path);
            if (!string.IsNullOrEmpty(blockedReason))
            {
                throw new InvalidOperationException(
                    "模型配置文件在本次运行中处于只读保护状态。" +
                    blockedReason);
            }

            try
            {
                SaveCore(profiles);
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

        private void SaveCore(IEnumerable<ModelProfile> profiles)
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

        private XDocument ReadDocument()
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
                        "模型配置文件大小无效或超过 1 MB 安全限制。");
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

        private void MarkPartialLoad(string warning)
        {
            MarkWriteBlocked(path, warning);
            loadWarning = GetWriteBlockReason(path);
        }

        private static void ValidateProfileStructure(XElement element)
        {
            if (element == null || element.HasAttributes)
            {
                throw new InvalidDataException("模型配置条目结构无效。");
            }

            string[] allowed =
            {
                "Name",
                "EndpointUrl",
                "ModelName",
                "SystemPrompt",
                "Temperature",
                "TopP",
                "MaxOutputTokens",
                "ProtectedApiKey"
            };
            foreach (XElement child in element.Elements())
            {
                if (!allowed.Contains(
                    child.Name.LocalName,
                    StringComparer.Ordinal) ||
                    child.HasElements || child.HasAttributes)
                {
                    throw new InvalidDataException(
                        "模型配置条目包含未知或无效字段。");
                }
            }

            foreach (string name in allowed)
            {
                if (element.Elements(name).Skip(1).Any())
                {
                    throw new InvalidDataException(
                        "模型配置条目包含重复字段：" + name);
                }
            }

            if (element.Nodes().OfType<XText>().Any(
                text => !string.IsNullOrWhiteSpace(text.Value)))
            {
                throw new InvalidDataException(
                    "模型配置条目包含无法识别的文本。");
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

        private static bool IsInvalidOptionalDouble(
            string value,
            double minimum,
            double maximum)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            double parsed;
            return !double.TryParse(
                       value,
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out parsed) ||
                double.IsNaN(parsed) ||
                double.IsInfinity(parsed) ||
                parsed < minimum || parsed > maximum;
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

        private static bool IsInvalidOptionalInt32(
            string value,
            int minimum,
            int maximum)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            int parsed;
            return !int.TryParse(
                       value,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out parsed) ||
                parsed < minimum || parsed > maximum;
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

        private void HandleCorruptFile(Exception exception)
        {
            string blockedReason = GetWriteBlockReason(path);
            if (!string.IsNullOrEmpty(blockedReason))
            {
                loadWarning = blockedReason;
                return;
            }

            bool moved = TryMoveCorruptFile();
            if (moved)
            {
                loadWarning =
                    "模型配置未能读取，已备份损坏文件：" + exception.Message;
                return;
            }

            string warning = "模型配置内容损坏，但无法创建安全备份；" +
                "原文件保持不变，本次运行已进入只读保护：" +
                exception.Message;
            MarkWriteBlocked(path, warning);
            loadWarning = GetWriteBlockReason(path);
        }

        private void HandleUnavailableFile(Exception exception)
        {
            string warning = "模型配置文件当前无法访问，原文件保持不变；" +
                "本次运行已进入只读保护：" + exception.Message;
            MarkWriteBlocked(path, warning);
            loadWarning = GetWriteBlockReason(path);
        }

        private bool TryMoveCorruptFile()
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                string backup = path + ".corrupt-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".xml";
                File.Move(path, backup);
                return true;
            }
            catch
            {
                // Keep startup resilient if the profile file is locked.
                return false;
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
    }
}
