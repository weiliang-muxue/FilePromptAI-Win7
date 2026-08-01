using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace FilePromptWin7
{
    internal sealed class ExtensionSettings
    {
        public IList<SkillDefinition> Skills { get; set; }
        public IList<McpServerDefinition> McpServers { get; set; }

        public ExtensionSettings()
        {
            Skills = new List<SkillDefinition>();
            McpServers = new List<McpServerDefinition>();
        }

        public ExtensionSettings Clone()
        {
            ExtensionSettings clone = new ExtensionSettings();
            foreach (SkillDefinition skill in Skills ?? new List<SkillDefinition>())
            {
                if (skill != null)
                {
                    clone.Skills.Add(skill.Clone());
                }
            }

            foreach (McpServerDefinition server in
                McpServers ?? new List<McpServerDefinition>())
            {
                if (server != null)
                {
                    clone.McpServers.Add(server.Clone());
                }
            }

            return clone;
        }

        public string BuildSystemPrompt()
        {
            IList<SkillDefinition> enabled = (Skills ??
                new List<SkillDefinition>())
                .Where(skill => skill != null && skill.Enabled &&
                    !string.IsNullOrWhiteSpace(skill.Instructions))
                .ToList();
            if (enabled.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder prompt = new StringBuilder();
            prompt.AppendLine(
                "以下内容是用户在 FilePrompt 中明确安装并启用的本地技能指令。" +
                "请在不违反用户当前要求的前提下遵循这些指令：");
            foreach (SkillDefinition skill in enabled)
            {
                prompt.AppendLine();
                prompt.Append("## 技能：");
                prompt.AppendLine(skill.Name ?? "未命名技能");
                if (!string.IsNullOrWhiteSpace(skill.Description))
                {
                    prompt.AppendLine(skill.Description.Trim());
                }

                prompt.AppendLine(skill.Instructions.Trim());
            }

            return prompt.ToString().Trim();
        }
    }

    internal sealed class SkillDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Instructions { get; set; }
        public bool Enabled { get; set; }

        public SkillDefinition()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "新技能";
            Description = string.Empty;
            Instructions = string.Empty;
            Enabled = true;
        }

        public SkillDefinition Clone()
        {
            return new SkillDefinition
            {
                Id = Id,
                Name = Name,
                Description = Description,
                Instructions = Instructions,
                Enabled = Enabled
            };
        }
    }

    internal sealed class McpServerDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Transport { get; set; }
        public string Command { get; set; }
        public IList<string> Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public IDictionary<string, string> Environment { get; set; }
        public string Url { get; set; }
        public IDictionary<string, string> Headers { get; set; }
        public bool Enabled { get; set; }
        public bool RequireConfirmation { get; set; }

        public McpServerDefinition()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "新 MCP";
            Transport = "stdio";
            Command = string.Empty;
            Arguments = new List<string>();
            WorkingDirectory = string.Empty;
            Environment = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            Url = string.Empty;
            Headers = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            Enabled = false;
            RequireConfirmation = true;
        }

        public McpServerDefinition Clone()
        {
            return new McpServerDefinition
            {
                Id = Id,
                Name = Name,
                Transport = Transport,
                Command = Command,
                Arguments = new List<string>(Arguments ?? new List<string>()),
                WorkingDirectory = WorkingDirectory,
                Environment = new Dictionary<string, string>(
                    Environment ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase),
                Url = Url,
                Headers = new Dictionary<string, string>(
                    Headers ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase),
                Enabled = Enabled,
                RequireConfirmation = RequireConfirmation
            };
        }
    }

    internal sealed class McpToolDefinition
    {
        public string PublicName { get; set; }
        public string ServerId { get; set; }
        public string ServerName { get; set; }
        public string ToolName { get; set; }
        public string Description { get; set; }
        public object InputSchema { get; set; }
        public bool RequireConfirmation { get; set; }
    }

    internal sealed class ModelToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ArgumentsJson { get; set; }
    }

    internal sealed class McpToolResult
    {
        public string Content { get; set; }
        public bool IsError { get; set; }

        public McpToolResult()
        {
            Content = string.Empty;
        }
    }

    internal sealed class McpException : Exception
    {
        public McpException(string message)
            : base(message)
        {
        }

        public McpException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal static class ExtensionImport
    {
        private const int MaximumServers = 20;

        public static IList<McpServerDefinition> ParseMcpServers(
            string configuration)
        {
            if (string.IsNullOrWhiteSpace(configuration))
            {
                throw new InvalidOperationException("剪贴板中没有 MCP JSON 配置。");
            }

            JavaScriptSerializer json = new JavaScriptSerializer();
            json.MaxJsonLength = 2 * 1024 * 1024;
            json.RecursionLimit = 64;
            IDictionary<string, object> root = json.DeserializeObject(
                configuration) as IDictionary<string, object>;
            if (root == null)
            {
                throw new InvalidOperationException("MCP 配置必须是 JSON 对象。");
            }

            object serversValue;
            IDictionary<string, object> servers;
            if (root.TryGetValue("mcpServers", out serversValue))
            {
                servers = serversValue as IDictionary<string, object>;
            }
            else
            {
                servers = root;
            }

            if (servers == null || servers.Count == 0)
            {
                throw new InvalidOperationException(
                    "配置中没有找到 mcpServers。");
            }

            List<McpServerDefinition> result =
                new List<McpServerDefinition>();
            foreach (KeyValuePair<string, object> pair in servers)
            {
                if (result.Count >= MaximumServers)
                {
                    throw new InvalidOperationException(
                        "一次最多导入 20 个 MCP 服务。");
                }

                IDictionary<string, object> value =
                    pair.Value as IDictionary<string, object>;
                if (value == null)
                {
                    throw new InvalidOperationException(
                        "MCP 服务“" + pair.Key + "”必须是 JSON 对象。");
                }

                McpServerDefinition server = new McpServerDefinition();
                server.Name = string.IsNullOrWhiteSpace(pair.Key)
                    ? "未命名 MCP"
                    : pair.Key.Trim();
                server.Command = GetString(value, "command");
                server.Url = GetString(value, "url");
                if (string.IsNullOrEmpty(server.Url))
                {
                    server.Url = GetString(value, "httpUrl");
                }

                server.Arguments = GetStringList(value, "args");
                server.WorkingDirectory = GetString(value, "cwd");
                server.Environment = GetStringDictionary(value, "env");
                server.Headers = GetStringDictionary(value, "headers");
                server.RequireConfirmation = GetBoolean(
                    value,
                    "requireConfirmation",
                    true);
                // Clipboard configuration never grants execution authority.
                server.Enabled = false;

                bool hasCommand = !string.IsNullOrWhiteSpace(server.Command);
                bool hasUrl = !string.IsNullOrWhiteSpace(server.Url);
                if (hasCommand == hasUrl)
                {
                    throw new InvalidOperationException(
                        "MCP 服务“" + server.Name +
                        "”必须且只能填写 command 或 url 其中一项。");
                }

                server.Transport = hasCommand ? "stdio" : "http";
                result.Add(server);
            }

            return result;
        }

        public static SkillDefinition ParseSkill(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("剪贴板中没有技能内容。");
            }

            string trimmed = content.Trim();
            SkillDefinition markdownSkill;
            if (TryParseSkillMarkdown(trimmed, out markdownSkill))
            {
                return markdownSkill;
            }

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    JavaScriptSerializer json = new JavaScriptSerializer();
                    IDictionary<string, object> value = json.DeserializeObject(
                        trimmed) as IDictionary<string, object>;
                    if (value != null)
                    {
                        string instructions = GetString(value, "instructions");
                        if (string.IsNullOrWhiteSpace(instructions))
                        {
                            instructions = GetString(value, "prompt");
                        }

                        if (!string.IsNullOrWhiteSpace(instructions))
                        {
                            SkillDefinition parsed = new SkillDefinition();
                            parsed.Name = GetString(value, "name");
                            if (string.IsNullOrWhiteSpace(parsed.Name))
                            {
                                parsed.Name = "粘贴技能";
                            }

                            parsed.Description = GetString(
                                value,
                                "description");
                            parsed.Instructions = instructions;
                            parsed.Enabled = GetBoolean(value, "enabled", true);
                            return parsed;
                        }
                    }
                }
                catch
                {
                    // Plain-text skills may legitimately start with a brace.
                }
            }

            SkillDefinition skill = new SkillDefinition();
            string firstLine = trimmed
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
            skill.Name = BuildSkillName(firstLine);
            skill.Instructions = trimmed;
            return skill;
        }

        private static bool TryParseSkillMarkdown(
            string content,
            out SkillDefinition skill)
        {
            skill = null;
            string normalized = (content ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            if (lines.Length < 3 ||
                !string.Equals(
                    lines[0].Trim(),
                    "---",
                    StringComparison.Ordinal))
            {
                return false;
            }

            int closingLine = -1;
            for (int index = 1; index < lines.Length && index <= 100; index++)
            {
                if (string.Equals(
                    lines[index].Trim(),
                    "---",
                    StringComparison.Ordinal))
                {
                    closingLine = index;
                    break;
                }
            }

            if (closingLine < 0)
            {
                return false;
            }

            Dictionary<string, string> metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < closingLine; index++)
            {
                string line = lines[index];
                if (string.IsNullOrWhiteSpace(line) ||
                    char.IsWhiteSpace(line[0]))
                {
                    continue;
                }

                int separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (key.Length > 0)
                {
                    metadata[key] = UnquoteYamlScalar(value);
                }
            }

            string instructions = string.Join(
                "\n",
                lines.Skip(closingLine + 1).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(instructions))
            {
                return false;
            }

            string name;
            metadata.TryGetValue("name", out name);
            if (string.IsNullOrWhiteSpace(name))
            {
                string firstLine = instructions
                    .Split(new[] { '\n' }, StringSplitOptions.None)
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
                name = BuildSkillName(firstLine);
            }

            string description;
            metadata.TryGetValue("description", out description);
            string enabledValue;
            bool enabled = true;
            if (metadata.TryGetValue("enabled", out enabledValue))
            {
                bool parsed;
                if (bool.TryParse(enabledValue, out parsed))
                {
                    enabled = parsed;
                }
            }

            skill = new SkillDefinition
            {
                Name = name.Trim(),
                Description = (description ?? string.Empty).Trim(),
                Instructions = instructions,
                Enabled = enabled
            };
            return true;
        }

        private static string UnquoteYamlScalar(string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length >= 2 &&
                ((text[0] == '"' && text[text.Length - 1] == '"') ||
                    (text[0] == '\'' && text[text.Length - 1] == '\'')))
            {
                text = text.Substring(1, text.Length - 2);
            }

            return text;
        }

        private static string BuildSkillName(string firstLine)
        {
            string value = (firstLine ?? string.Empty).Trim();
            while (value.StartsWith("#", StringComparison.Ordinal))
            {
                value = value.Substring(1).TrimStart();
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return "粘贴技能";
            }

            return value.Length <= 40 ? value : value.Substring(0, 40);
        }

        private static string GetString(
            IDictionary<string, object> value,
            string key)
        {
            object field;
            return value != null && value.TryGetValue(key, out field) &&
                field != null
                ? Convert.ToString(field, CultureInfo.InvariantCulture).Trim()
                : string.Empty;
        }

        private static bool GetBoolean(
            IDictionary<string, object> value,
            string key,
            bool fallback)
        {
            object field;
            if (value == null || !value.TryGetValue(key, out field) ||
                field == null)
            {
                return fallback;
            }

            bool parsed;
            return bool.TryParse(
                Convert.ToString(field, CultureInfo.InvariantCulture),
                out parsed)
                ? parsed
                : fallback;
        }

        private static IList<string> GetStringList(
            IDictionary<string, object> value,
            string key)
        {
            object field;
            IList list;
            List<string> result = new List<string>();
            if (value == null || !value.TryGetValue(key, out field) ||
                (list = field as IList) == null)
            {
                return result;
            }

            foreach (object item in list)
            {
                if (item != null)
                {
                    result.Add(Convert.ToString(
                        item,
                        CultureInfo.InvariantCulture));
                }
            }

            return result;
        }

        private static IDictionary<string, string> GetStringDictionary(
            IDictionary<string, object> value,
            string key)
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            object field;
            IDictionary<string, object> dictionary;
            if (value == null || !value.TryGetValue(key, out field) ||
                (dictionary = field as IDictionary<string, object>) == null)
            {
                return result;
            }

            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                result[pair.Key] = pair.Value == null
                    ? string.Empty
                    : Convert.ToString(
                        pair.Value,
                        CultureInfo.InvariantCulture);
            }

            return result;
        }
    }
}
