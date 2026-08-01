using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FilePromptWin7
{
    internal static class ExtensionSettingsSmokeTest
    {
        private static int Main()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "FilePromptExtensions-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                TestSkillParsing();
                TestMcpImport();
                TestMcpValidation();
                TestEncryptedRoundTrip(root);
                TestUtf8SizeBoundary(root);
                TestDamagedSettings(root);
                Console.WriteLine("PASS | extension settings");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL | extension settings");
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch
                {
                    // The next test run can clean a locked temporary folder.
                }
            }
        }

        private static void TestSkillParsing()
        {
            SkillDefinition plain = ExtensionImport.ParseSkill(
                "# 表格审核\r\n请检查每一列。");
            Assert(plain.Name == "表格审核", "plain skill name");
            Assert(
                plain.Instructions.IndexOf("每一列", StringComparison.Ordinal) >= 0,
                "plain skill instructions");
            SkillDefinition braceText = ExtensionImport.ParseSkill(
                "{这是一条普通文本技能，不是 JSON");
            Assert(
                braceText.Instructions.StartsWith(
                    "{这是一条",
                    StringComparison.Ordinal),
                "brace-prefixed plain skill");

            SkillDefinition markdown = ExtensionImport.ParseSkill(
                "---\r\n" +
                "name: table-review\r\n" +
                "description: \"内网表格复核\"\r\n" +
                "enabled: true\r\n" +
                "---\r\n" +
                "# 工作流程\r\n先核对列名，再输出风险。");
            Assert(markdown.Name == "table-review", "SKILL.md name");
            Assert(
                markdown.Description == "内网表格复核",
                "SKILL.md description");
            Assert(
                markdown.Instructions.StartsWith(
                    "# 工作流程",
                    StringComparison.Ordinal) &&
                markdown.Instructions.IndexOf(
                    "name:",
                    StringComparison.Ordinal) < 0,
                "SKILL.md body instructions");

            SkillDefinition json = ExtensionImport.ParseSkill(
                "{\"name\":\"合同审阅\",\"description\":\"内网模板\"," +
                "\"instructions\":\"列出风险\",\"enabled\":false}");
            Assert(json.Name == "合同审阅", "JSON skill name");
            Assert(!json.Enabled, "JSON skill enabled flag");
        }

        private static void TestMcpImport()
        {
            Assert(
                !new McpServerDefinition().Enabled,
                "new MCP defaults disabled");
            string configuration =
                "{\"mcpServers\":{" +
                "\"local\":{\"command\":\"tool.exe\"," +
                "\"args\":[\"--stdio\"],\"env\":{\"TOKEN\":\"x\"}," +
                "\"enabled\":true}," +
                "\"remote\":{\"url\":\"http://127.0.0.1:19000/mcp\"," +
                "\"headers\":{\"Authorization\":\"Bearer y\"}," +
                "\"enabled\":true}}}";
            IList<McpServerDefinition> servers =
                ExtensionImport.ParseMcpServers(configuration);
            Assert(servers.Count == 2, "MCP import count");
            Assert(servers[0].Transport == "stdio", "stdio import");
            Assert(servers[1].Transport == "http", "HTTP import");
            Assert(
                servers.All(server => server.RequireConfirmation),
                "MCP confirmation defaults on");
            Assert(
                servers.All(server => !server.Enabled),
                "MCP import cannot auto-enable servers");
        }

        private static void TestEncryptedRoundTrip(string root)
        {
            string path = Path.Combine(root, "extensions.xml");
            ExtensionStore store = new ExtensionStore(path);
            ExtensionSettings settings = new ExtensionSettings();
            settings.Skills.Add(new SkillDefinition
            {
                Name = "离线总结",
                Description = "固定输出结构",
                Instructions = "先列结论，再列依据。😀",
                Enabled = true
            });
            settings.Skills.Add(new SkillDefinition
            {
                Name = "已停用技能",
                Instructions = "不应进入提示词",
                Enabled = false
            });
            settings.McpServers.Add(new McpServerDefinition
            {
                Name = "local-secure",
                Transport = "stdio",
                Command = "C:\\Private\\mcp-secret-tool.exe",
                Arguments = new List<string> { "--token=stdio-secret-42" },
                Environment = new Dictionary<string, string>
                {
                    { "PRIVATE_TOKEN", "environment-secret-42" }
                },
                Enabled = true,
                RequireConfirmation = true
            });
            settings.McpServers.Add(new McpServerDefinition
            {
                Name = "http-secure",
                Transport = "http",
                Url = "http://127.0.0.1:19001/mcp/private-route",
                Headers = new Dictionary<string, string>
                {
                    { "Authorization", "Bearer header-secret-42" }
                },
                Enabled = false,
                RequireConfirmation = true
            });

            store.Save(settings);
            string raw = File.ReadAllText(path, Encoding.UTF8);
            Assert(
                raw.IndexOf("stdio-secret-42", StringComparison.Ordinal) < 0,
                "stdio arguments encrypted");
            Assert(
                raw.IndexOf("environment-secret-42", StringComparison.Ordinal) < 0,
                "stdio environment encrypted");
            Assert(
                raw.IndexOf("private-route", StringComparison.Ordinal) < 0,
                "HTTP URL encrypted");
            Assert(
                raw.IndexOf("header-secret-42", StringComparison.Ordinal) < 0,
                "HTTP headers encrypted");

            ExtensionSettings loaded = new ExtensionStore(path).Load();
            Assert(loaded.Skills.Count == 2, "skills round trip");
            Assert(
                loaded.Skills[0].Instructions.EndsWith(
                    "😀",
                    StringComparison.Ordinal),
                "skill Unicode round trip");
            Assert(loaded.McpServers.Count == 2, "MCP round trip");
            Assert(
                loaded.McpServers[0].Command.EndsWith(
                    "mcp-secret-tool.exe",
                    StringComparison.Ordinal),
                "stdio command round trip");
            Assert(
                loaded.McpServers[1].Headers["Authorization"] ==
                    "Bearer header-secret-42",
                "HTTP header round trip");

            string prompt = loaded.BuildSystemPrompt();
            Assert(
                prompt.IndexOf("离线总结", StringComparison.Ordinal) >= 0,
                "enabled skill prompt");
            Assert(
                prompt.IndexOf("已停用技能", StringComparison.Ordinal) < 0,
                "disabled skill excluded");
        }

        private static void TestMcpValidation()
        {
            ExtensionSettings settings = new ExtensionSettings();
            settings.McpServers.Add(new McpServerDefinition
            {
                Name = "header-injection",
                Transport = "http",
                Url = "http://127.0.0.1:19002/mcp",
                Headers = new Dictionary<string, string>
                {
                    { "Authorization", "ok\r\nInjected: value" }
                }
            });
            Exception failure = null;
            try
            {
                ExtensionStore.Validate(settings);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert(
                failure is InvalidOperationException,
                "MCP header injection rejected");

            settings = new ExtensionSettings();
            settings.McpServers.Add(new McpServerDefinition
            {
                Name = "managed-header",
                Transport = "http",
                Url = "http://127.0.0.1:19003/mcp",
                Headers = new Dictionary<string, string>
                {
                    { "MCP-Protocol-Version", "override" }
                }
            });
            failure = null;
            try
            {
                ExtensionStore.Validate(settings);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert(
                failure is InvalidOperationException,
                "managed MCP header rejected");
        }

        private static void TestUtf8SizeBoundary(string root)
        {
            const int maximumBytes = 8 * 1024 * 1024;
            string instructions = new string('\u4e2d', 200000);
            string acceptedPath = Path.Combine(root, "utf8-boundary.xml");
            ExtensionSettings accepted = CreateLargeChineseSettings(
                instructions,
                13);
            new ExtensionStore(acceptedPath).Save(accepted);
            long acceptedBytes = new FileInfo(acceptedPath).Length;
            Assert(
                acceptedBytes <= maximumBytes,
                "UTF-8 settings below byte limit saved");
            ExtensionSettings loaded = new ExtensionStore(acceptedPath).Load();
            Assert(
                loaded.Skills.Count == 13 &&
                loaded.Skills[12].Instructions.Length == instructions.Length,
                "large Chinese settings reload");

            string rejectedPath = Path.Combine(root, "utf8-too-large.xml");
            ExtensionSettings rejected = CreateLargeChineseSettings(
                instructions,
                14);
            Exception failure = null;
            try
            {
                new ExtensionStore(rejectedPath).Save(rejected);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert(
                failure is InvalidOperationException &&
                failure.Message.IndexOf("8 MB", StringComparison.Ordinal) >= 0,
                "UTF-8 settings above byte limit rejected");
            Assert(
                !File.Exists(rejectedPath),
                "oversized settings not written");
        }

        private static ExtensionSettings CreateLargeChineseSettings(
            string instructions,
            int count)
        {
            ExtensionSettings settings = new ExtensionSettings();
            for (int index = 0; index < count; index++)
            {
                settings.Skills.Add(new SkillDefinition
                {
                    Name = "边界技能 " + index,
                    Instructions = instructions,
                    Enabled = true
                });
            }

            return settings;
        }

        private static void TestDamagedSettings(string root)
        {
            string path = Path.Combine(root, "damaged.xml");
            File.WriteAllText(path, "<Unexpected />", Encoding.UTF8);
            ExtensionStore store = new ExtensionStore(path);
            ExtensionSettings loaded = store.Load();
            Assert(loaded.Skills.Count == 0, "damaged settings disabled");
            Assert(!string.IsNullOrEmpty(store.LoadWarning), "damage warning");
            Assert(!File.Exists(path), "damaged source preserved by move");
            Assert(
                Directory.GetFiles(root, "damaged.xml.corrupt-*.xml").Length == 1,
                "damaged backup exists");
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name + " failed.");
            }

            Console.WriteLine("PASS | " + name);
        }
    }
}
