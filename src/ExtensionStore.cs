using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace FilePromptWin7
{
    internal sealed class ExtensionStore
    {
        private const int MaximumSkills = 50;
        private const int MaximumServers = 20;
        private const int MaximumSkillCharacters = 200000;
        private const int MaximumSettingsBytes = 8 * 1024 * 1024;
        private const int MaximumSettingsCharacters = 8 * 1024 * 1024;
        private static readonly UTF8Encoding SettingsEncoding =
            new UTF8Encoding(true);
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "FilePromptWin7.ExtensionSettings.v1");

        private readonly JavaScriptSerializer json;

        public string SettingsPath { get; private set; }
        public string LoadWarning { get; private set; }

        public ExtensionStore()
            : this(Path.Combine(AppDataPath.Root, "extensions.xml"))
        {
        }

        internal ExtensionStore(string settingsPath)
        {
            SettingsPath = Path.GetFullPath(settingsPath);
            json = new JavaScriptSerializer();
            json.MaxJsonLength = MaximumSettingsCharacters;
            json.RecursionLimit = 64;
        }

        public ExtensionSettings Load()
        {
            LoadWarning = string.Empty;
            if (!File.Exists(SettingsPath))
            {
                return new ExtensionSettings();
            }

            try
            {
                FileInfo info = new FileInfo(SettingsPath);
                if (info.Length > MaximumSettingsBytes)
                {
                    throw new InvalidDataException("扩展配置超过 8 MB 安全限制。");
                }

                XDocument document;
                XmlReaderSettings readerSettings = new XmlReaderSettings();
                readerSettings.DtdProcessing = DtdProcessing.Prohibit;
                readerSettings.XmlResolver = null;
                readerSettings.MaxCharactersInDocument =
                    MaximumSettingsCharacters;
                readerSettings.MaxCharactersFromEntities = 0;
                using (FileStream stream = new FileStream(
                    SettingsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                using (XmlReader reader = XmlReader.Create(
                    stream,
                    readerSettings))
                {
                    document = XDocument.Load(reader, LoadOptions.None);
                }

                return ReadSettings(document);
            }
            catch (Exception exception)
            {
                string backup = PreserveDamagedSettings();
                LoadWarning = "扩展配置损坏，已停用全部技能和 MCP。" +
                    (string.IsNullOrEmpty(backup)
                        ? string.Empty
                        : " 原文件已保留为 " + Path.GetFileName(backup) + "。") +
                    "（" + exception.Message + "）";
                return new ExtensionSettings();
            }
        }

        public void Save(ExtensionSettings settings)
        {
            Validate(settings);
            string directory = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            XElement skills = new XElement("Skills");
            foreach (SkillDefinition skill in settings.Skills)
            {
                skills.Add(
                    new XElement(
                        "Skill",
                        new XAttribute("id", skill.Id),
                        new XAttribute("enabled", skill.Enabled),
                        new XElement("Name", skill.Name),
                        new XElement(
                            "Description",
                            skill.Description ?? string.Empty),
                        new XElement(
                            "Instructions",
                            skill.Instructions ?? string.Empty)));
            }

            XElement servers = new XElement("McpServers");
            foreach (McpServerDefinition server in settings.McpServers)
            {
                string serialized = json.Serialize(server);
                servers.Add(
                    new XElement(
                        "McpServer",
                        new XAttribute("id", server.Id),
                        new XAttribute("name", server.Name),
                        new XAttribute("enabled", server.Enabled),
                        new XElement(
                            "ProtectedConfiguration",
                            Protect(serialized))));
            }

            XDocument document = new XDocument(
                new XElement(
                    "FilePromptExtensions",
                    new XAttribute("version", "1"),
                    skills,
                    servers));
            string content = document.ToString();
            long encodedLength = SettingsEncoding.GetPreamble().Length +
                (long)SettingsEncoding.GetByteCount(content);
            if (encodedLength > MaximumSettingsBytes)
            {
                throw new InvalidOperationException(
                    "扩展配置超过 8 MB，无法保存。");
            }

            AtomicFile.WriteAllText(
                SettingsPath,
                content,
                SettingsEncoding);
        }

        public static void Validate(ExtensionSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            IList<SkillDefinition> skills = settings.Skills ??
                new List<SkillDefinition>();
            IList<McpServerDefinition> servers = settings.McpServers ??
                new List<McpServerDefinition>();
            if (skills.Count > MaximumSkills)
            {
                throw new InvalidOperationException("最多保存 50 个技能。");
            }

            if (servers.Count > MaximumServers)
            {
                throw new InvalidOperationException("最多保存 20 个 MCP 服务。");
            }

            HashSet<string> ids = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (SkillDefinition skill in skills)
            {
                if (skill == null)
                {
                    throw new InvalidOperationException("技能配置不能为空。");
                }

                NormalizeSkill(skill);
                if (!ids.Add(skill.Id))
                {
                    throw new InvalidOperationException("技能 ID 重复。");
                }
            }

            ids.Clear();
            foreach (McpServerDefinition server in servers)
            {
                if (server == null)
                {
                    throw new InvalidOperationException("MCP 配置不能为空。");
                }

                NormalizeServer(server);
                if (!ids.Add(server.Id))
                {
                    throw new InvalidOperationException("MCP 服务 ID 重复。");
                }
            }
        }

        private ExtensionSettings ReadSettings(XDocument document)
        {
            XElement root = document.Root;
            if (root == null || root.Name != "FilePromptExtensions" ||
                (string)root.Attribute("version") != "1")
            {
                throw new InvalidDataException("扩展配置根节点或版本无效。");
            }

            ExtensionSettings settings = new ExtensionSettings();
            XElement skills = root.Element("Skills");
            if (skills != null)
            {
                foreach (XElement element in skills.Elements("Skill"))
                {
                    SkillDefinition skill = new SkillDefinition();
                    skill.Id = GetAttribute(element, "id");
                    bool enabled;
                    skill.Enabled = bool.TryParse(
                        GetAttribute(element, "enabled"),
                        out enabled) && enabled;
                    skill.Name = GetValue(element, "Name");
                    skill.Description = GetValue(element, "Description");
                    skill.Instructions = GetValue(element, "Instructions");
                    settings.Skills.Add(skill);
                }
            }

            XElement servers = root.Element("McpServers");
            if (servers != null)
            {
                foreach (XElement element in servers.Elements("McpServer"))
                {
                    string protectedConfiguration = GetValue(
                        element,
                        "ProtectedConfiguration");
                    McpServerDefinition server =
                        json.Deserialize<McpServerDefinition>(
                            Unprotect(protectedConfiguration));
                    if (server == null)
                    {
                        throw new InvalidDataException("MCP 配置无法解析。");
                    }

                    settings.McpServers.Add(server);
                }
            }

            Validate(settings);
            return settings;
        }

        private static void NormalizeSkill(SkillDefinition skill)
        {
            skill.Id = NormalizeId(skill.Id);
            skill.Name = (skill.Name ?? string.Empty).Trim();
            skill.Description = (skill.Description ?? string.Empty).Trim();
            skill.Instructions = (skill.Instructions ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(skill.Name))
            {
                throw new InvalidOperationException("技能名称不能为空。");
            }

            if (skill.Name.Length > 80 || skill.Description.Length > 500 ||
                skill.Instructions.Length > MaximumSkillCharacters)
            {
                throw new InvalidOperationException(
                    "技能名称、说明或指令超过长度限制。");
            }

            EnsureXmlText(skill.Name);
            EnsureXmlText(skill.Description);
            EnsureXmlText(skill.Instructions);
        }

        private static void NormalizeServer(McpServerDefinition server)
        {
            server.Id = NormalizeId(server.Id);
            server.Name = (server.Name ?? string.Empty).Trim();
            server.Transport = (server.Transport ?? string.Empty)
                .Trim()
                .ToLowerInvariant();
            server.Command = (server.Command ?? string.Empty).Trim();
            server.Url = (server.Url ?? string.Empty).Trim();
            server.WorkingDirectory = (server.WorkingDirectory ?? string.Empty)
                .Trim();
            server.Arguments = new List<string>(
                server.Arguments ?? new List<string>());
            server.Environment = new Dictionary<string, string>(
                server.Environment ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            server.Headers = new Dictionary<string, string>(
                server.Headers ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(server.Name) ||
                server.Name.Length > 80)
            {
                throw new InvalidOperationException(
                    "MCP 服务名称不能为空且最多 80 个字符。");
            }

            if (server.Transport != "stdio" && server.Transport != "http")
            {
                throw new InvalidOperationException(
                    "MCP 传输方式只能是 stdio 或 http。");
            }

            if (server.Transport == "stdio")
            {
                if (string.IsNullOrWhiteSpace(server.Command))
                {
                    throw new InvalidOperationException(
                        "stdio MCP 必须填写启动命令。");
                }

                if (server.Command.IndexOf('\0') >= 0 ||
                    server.Command.IndexOf('\r') >= 0 ||
                    server.Command.IndexOf('\n') >= 0 ||
                    server.WorkingDirectory.IndexOf('\0') >= 0)
                {
                    throw new InvalidOperationException(
                        "stdio MCP 命令或工作目录包含无效控制字符。");
                }

                server.Url = string.Empty;
            }
            else
            {
                Uri uri;
                if (!Uri.TryCreate(server.Url, UriKind.Absolute, out uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp &&
                        uri.Scheme != Uri.UriSchemeHttps) ||
                    !string.IsNullOrEmpty(uri.UserInfo))
                {
                    throw new InvalidOperationException(
                        "HTTP MCP 必须填写不含用户名密码的完整 http/https URL。");
                }

                server.Command = string.Empty;
                server.Arguments.Clear();
                server.WorkingDirectory = string.Empty;
                server.Environment.Clear();
            }

            if (server.Arguments.Count > 100 ||
                server.Environment.Count > 100 ||
                server.Headers.Count > 100)
            {
                throw new InvalidOperationException(
                    "MCP 参数、环境变量或请求头数量超过限制。");
            }

            ValidateStringCollection(server.Arguments, 4000, "MCP 参数");
            ValidateDictionary(server.Environment, "MCP 环境变量");
            ValidateDictionary(server.Headers, "MCP 请求头");
            foreach (string headerName in server.Headers.Keys)
            {
                if (IsManagedHeader(headerName))
                {
                    throw new InvalidOperationException(
                        "MCP 请求头“" + headerName +
                        "”由程序管理，不能覆盖。");
                }
            }

            EnsureXmlText(server.Name);
        }

        private static bool IsManagedHeader(string name)
        {
            string value = (name ?? string.Empty).Trim();
            string[] managed =
            {
                "Host",
                "Content-Length",
                "Content-Type",
                "Transfer-Encoding",
                "Connection",
                "Accept",
                "Mcp-Session-Id",
                "MCP-Protocol-Version"
            };
            return managed.Any(header => string.Equals(
                header,
                value,
                StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeId(string value)
        {
            string id = (value ?? string.Empty).Trim();
            Guid parsed;
            return id.Length == 32 && Guid.TryParseExact(id, "N", out parsed)
                ? id.ToLowerInvariant()
                : Guid.NewGuid().ToString("N");
        }

        private static void ValidateStringCollection(
            IEnumerable<string> values,
            int maximumLength,
            string description)
        {
            foreach (string value in values)
            {
                if ((value ?? string.Empty).Length > maximumLength ||
                    (value ?? string.Empty).IndexOf('\0') >= 0)
                {
                    throw new InvalidOperationException(
                        description + "包含无效或过长内容。");
                }
            }
        }

        private static void ValidateDictionary(
            IDictionary<string, string> values,
            string description)
        {
            foreach (KeyValuePair<string, string> pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) ||
                    pair.Key.Length > 200 ||
                    (pair.Value ?? string.Empty).Length > 16000 ||
                    pair.Key.IndexOf('\r') >= 0 ||
                    pair.Key.IndexOf('\n') >= 0 ||
                    pair.Key.IndexOf('\0') >= 0 ||
                    pair.Key.IndexOf('=') >= 0 ||
                    (pair.Value ?? string.Empty).IndexOf('\0') >= 0 ||
                    (pair.Value ?? string.Empty).IndexOf('\r') >= 0 ||
                    (pair.Value ?? string.Empty).IndexOf('\n') >= 0)
                {
                    throw new InvalidOperationException(
                        description + "包含无效或过长内容。");
                }
            }
        }

        private static void EnsureXmlText(string value)
        {
            string text = value ?? string.Empty;
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (char.IsHighSurrogate(character) &&
                    index + 1 < text.Length &&
                    char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                    continue;
                }

                if (!XmlConvert.IsXmlChar(character))
                {
                    throw new InvalidOperationException(
                        "扩展配置包含 XML 无法保存的控制字符。");
                }
            }
        }

        private string PreserveDamagedSettings()
        {
            try
            {
                string backup = SettingsPath + ".corrupt-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".xml";
                int suffix = 1;
                while (File.Exists(backup))
                {
                    backup = SettingsPath + ".corrupt-" +
                        DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" +
                        suffix.ToString() + ".xml";
                    suffix++;
                }

                File.Move(SettingsPath, backup);
                return backup;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAttribute(XElement element, string name)
        {
            XAttribute attribute = element.Attribute(name);
            return attribute == null ? string.Empty : attribute.Value;
        }

        private static string GetValue(XElement element, string name)
        {
            XElement child = element.Element(name);
            return child == null ? string.Empty : child.Value;
        }

        private static string Protect(string value)
        {
            byte[] clear = Encoding.UTF8.GetBytes(value ?? string.Empty);
            byte[] encrypted = ProtectedData.Protect(
                clear,
                Entropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("MCP 加密配置为空。");
            }

            byte[] encrypted = Convert.FromBase64String(value);
            byte[] clear = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
    }
}
