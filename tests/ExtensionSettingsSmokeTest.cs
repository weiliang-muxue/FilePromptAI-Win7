using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FilePromptAIWin7
{
    internal static class ExtensionSettingsSmokeTest
    {
        private static int Main(string[] args)
        {
            if (args != null && args.Length == 4 &&
                args[0] == "--hold-exclusive-lock")
            {
                return HoldFileLock(args[1], args[2], args[3], true);
            }

            if (args != null && args.Length == 4 &&
                args[0] == "--hold-readable-lock")
            {
                return HoldFileLock(args[1], args[2], args[3], false);
            }

            string root = Path.Combine(
                Path.GetTempPath(),
                "FilePromptAIExtensions-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                TestSkillParsing();
                TestMcpImport();
                TestUtf8FileImport(root);
                TestUtf8FileImportRejections(root);
                TestMcpValidation();
                TestEncryptedRoundTrip(root);
                TestUtf8SizeBoundary(root);
                TestDamagedSettings(root);
                TestExclusiveLock(root);
                TestDamageBackupFailure(root);
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

        private static void TestUtf8FileImport(string root)
        {
            string markdownPath = Path.Combine(root, "SKILL.md");
            File.WriteAllText(
                markdownPath,
                "---\r\n" +
                "name: file-review\r\n" +
                "description: \"从离线文件导入\"\r\n" +
                "---\r\n" +
                "# 审阅流程\r\n先列风险，再列依据。",
                new UTF8Encoding(true));
            string markdownText = ExtensionImport.ReadUtf8ImportFile(
                markdownPath);
            SkillDefinition markdown = ExtensionImport.ParseSkill(
                markdownText);
            Assert(
                markdownText.Length > 0 && markdownText[0] != '\uFEFF' &&
                markdown.Name == "file-review" &&
                markdown.Description == "从离线文件导入" &&
                markdown.Instructions.IndexOf(
                    "先列风险",
                    StringComparison.Ordinal) >= 0,
                "strict UTF-8 SKILL.md file import");

            string skillJsonPath = Path.Combine(root, "skill.json");
            File.WriteAllText(
                skillJsonPath,
                "{\"name\":\"离线合同审阅\",\"description\":\"文件 JSON\"," +
                "\"instructions\":\"逐条列出风险\",\"enabled\":false}",
                new UTF8Encoding(false));
            SkillDefinition jsonSkill = ExtensionImport.ParseSkill(
                ExtensionImport.ReadUtf8ImportFile(skillJsonPath));
            Assert(
                jsonSkill.Name == "离线合同审阅" &&
                jsonSkill.Description == "文件 JSON" &&
                jsonSkill.Instructions == "逐条列出风险" &&
                !jsonSkill.Enabled,
                "strict UTF-8 skill JSON file import");

            string mcpJsonPath = Path.Combine(root, "mcp.json");
            File.WriteAllText(
                mcpJsonPath,
                "{\"mcpServers\":{" +
                "\"local-file\":{\"command\":\"tool.exe\",\"enabled\":true}," +
                "\"http-file\":{\"url\":\"http://127.0.0.1:19002/mcp\"," +
                "\"enabled\":true}}}",
                new UTF8Encoding(false));
            IList<McpServerDefinition> fileServers =
                ExtensionImport.ParseMcpServers(
                    ExtensionImport.ReadUtf8ImportFile(mcpJsonPath));
            Assert(
                fileServers.Count == 2 &&
                fileServers[0].Transport == "stdio" &&
                fileServers[1].Transport == "http" &&
                fileServers.All(server => !server.Enabled),
                "MCP JSON file import remains disabled");
        }

        private static void TestUtf8FileImportRejections(string root)
        {
            Exception directoryFailure = CaptureFailure(delegate
            {
                ExtensionImport.ReadUtf8ImportFile(root);
            });
            Assert(
                directoryFailure is InvalidOperationException,
                "import directory rejected");

            string emptyPath = Path.Combine(root, "empty-import.txt");
            File.WriteAllBytes(emptyPath, new byte[0]);
            Exception emptyFailure = CaptureFailure(delegate
            {
                ExtensionImport.ReadUtf8ImportFile(emptyPath);
            });
            Assert(
                emptyFailure is InvalidDataException,
                "empty import file rejected");

            string bomOnlyPath = Path.Combine(root, "bom-only-import.txt");
            File.WriteAllBytes(
                bomOnlyPath,
                new byte[] { 0xEF, 0xBB, 0xBF });
            Exception bomOnlyFailure = CaptureFailure(delegate
            {
                ExtensionImport.ReadUtf8ImportFile(bomOnlyPath);
            });
            Assert(
                bomOnlyFailure is InvalidDataException,
                "BOM-only import file rejected");

            string invalidPath = Path.Combine(root, "invalid-utf8.txt");
            File.WriteAllBytes(
                invalidPath,
                new byte[] { 0xE4, 0xB8, 0xAD, 0xC3, 0x28 });
            Exception invalidFailure = CaptureFailure(delegate
            {
                ExtensionImport.ReadUtf8ImportFile(invalidPath);
            });
            Assert(
                invalidFailure is InvalidDataException &&
                invalidFailure.InnerException is DecoderFallbackException,
                "invalid UTF-8 import file rejected");

            string boundaryPath = Path.Combine(root, "maximum-import.txt");
            File.WriteAllBytes(
                boundaryPath,
                Enumerable.Repeat(
                    (byte)'a',
                    ExtensionImport.MaximumImportFileBytes).ToArray());
            Assert(
                ExtensionImport.ReadUtf8ImportFile(boundaryPath).Length ==
                    ExtensionImport.MaximumImportFileBytes,
                "2 MiB import file accepted");

            string oversizedPath = Path.Combine(root, "oversized-import.txt");
            File.WriteAllBytes(
                oversizedPath,
                Enumerable.Repeat(
                    (byte)'a',
                    ExtensionImport.MaximumImportFileBytes + 1).ToArray());
            Exception oversizedFailure = CaptureFailure(delegate
            {
                ExtensionImport.ReadUtf8ImportFile(oversizedPath);
            });
            Assert(
                oversizedFailure is InvalidDataException &&
                oversizedFailure.Message.IndexOf(
                    "2 MiB",
                    StringComparison.Ordinal) >= 0,
                "import file above 2 MiB rejected");
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
            Assert(!store.IsWriteProtected, "successful damage backup permits rebuild");
        }

        private static void TestExclusiveLock(string root)
        {
            string path = Path.Combine(root, "locked-extensions.xml");
            ExtensionSettings expected = CreateLockSettings();
            new ExtensionStore(path).Save(expected);
            byte[] original = File.ReadAllBytes(path);
            string ready = path + ".ready";
            string release = path + ".release";
            ExtensionStore protectedStore = new ExtensionStore(path);
            Process holder = StartLockHolder(
                "--hold-exclusive-lock",
                path,
                ready,
                release);
            try
            {
                WaitForLockHolder(holder, ready);
                ExtensionSettings unavailable = protectedStore.Load();
                Assert(unavailable.Skills.Count == 0, "locked extensions return empty settings");
                Assert(protectedStore.IsWriteProtected, "locked extensions enable sticky protection");
                Assert(
                    protectedStore.LoadWarning.IndexOf(
                        "无法安全读取",
                        StringComparison.Ordinal) >= 0 &&
                    protectedStore.LoadWarning.IndexOf(
                        "损坏",
                        StringComparison.Ordinal) < 0,
                    "locked extensions report access warning");
                Assert(
                    File.Exists(path) &&
                    Directory.GetFiles(
                        root,
                        "locked-extensions.xml.corrupt-*.xml").Length == 0,
                    "locked extensions create no corrupt backup");
            }
            finally
            {
                ReleaseLockHolder(holder, release);
            }

            ExtensionSettings loaded = new ExtensionStore(path).Load();
            Assert(
                loaded.Skills.Count == 1 &&
                loaded.Skills[0].Name == expected.Skills[0].Name &&
                original.SequenceEqual(File.ReadAllBytes(path)),
                "extensions load after exclusive lock release");
            Exception saveFailure = CaptureFailure(delegate
            {
                protectedStore.Save(new ExtensionSettings());
            });
            Assert(
                saveFailure is InvalidOperationException &&
                original.SequenceEqual(File.ReadAllBytes(path)),
                "extension write protection remains sticky after release");
        }

        private static void TestDamageBackupFailure(string root)
        {
            string path = Path.Combine(root, "unmovable-damaged.xml");
            byte[] original = Encoding.UTF8.GetBytes("<FilePromptAIExtensions");
            File.WriteAllBytes(path, original);
            string ready = path + ".ready";
            string release = path + ".release";
            ExtensionStore store = new ExtensionStore(path);
            Process holder = StartLockHolder(
                "--hold-readable-lock",
                path,
                ready,
                release);
            try
            {
                WaitForLockHolder(holder, ready);
                store.Load();
                Assert(
                    store.IsWriteProtected &&
                    store.LoadWarning.IndexOf(
                        "无法创建安全备份",
                        StringComparison.Ordinal) >= 0,
                    "failed extension damage backup enables protection");
                Assert(
                    original.SequenceEqual(File.ReadAllBytes(path)) &&
                    Directory.GetFiles(
                        root,
                        "unmovable-damaged.xml.corrupt-*.xml").Length == 0,
                    "failed extension damage backup preserves original bytes");
            }
            finally
            {
                ReleaseLockHolder(holder, release);
            }

            Exception saveFailure = CaptureFailure(delegate
            {
                store.Save(CreateLockSettings());
            });
            Assert(
                saveFailure is InvalidOperationException &&
                original.SequenceEqual(File.ReadAllBytes(path)),
                "damaged extension store rejects save after lock release");
        }

        private static ExtensionSettings CreateLockSettings()
        {
            ExtensionSettings settings = new ExtensionSettings();
            settings.Skills.Add(new SkillDefinition
            {
                Name = "锁定测试技能",
                Instructions = "保留原始配置。",
                Enabled = true
            });
            return settings;
        }

        private static Exception CaptureFailure(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static Process StartLockHolder(
            string mode,
            string path,
            string ready,
            string release)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = System.Reflection.Assembly
                .GetExecutingAssembly().Location;
            startInfo.Arguments = mode + " " + QuoteArgument(path) + " " +
                QuoteArgument(ready) + " " + QuoteArgument(release);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            return Process.Start(startInfo);
        }

        private static void WaitForLockHolder(Process holder, string ready)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(ready))
            {
                if (holder == null || holder.HasExited)
                {
                    throw new InvalidOperationException(
                        "Extension lock holder exited before acquiring the lock.");
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "Timed out waiting for extension file lock.");
                }

                Thread.Sleep(25);
            }
        }

        private static void ReleaseLockHolder(Process holder, string release)
        {
            File.WriteAllText(release, "release", Encoding.ASCII);
            if (holder == null)
            {
                return;
            }

            if (!holder.WaitForExit(5000))
            {
                holder.Kill();
                holder.WaitForExit();
            }

            int exitCode = holder.ExitCode;
            holder.Dispose();
            Assert(exitCode == 0, "extension lock holder exits");
        }

        private static int HoldFileLock(
            string path,
            string ready,
            string release,
            bool exclusive)
        {
            try
            {
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                    exclusive ? FileShare.None : FileShare.Read))
                {
                    File.WriteAllText(ready, "ready", Encoding.ASCII);
                    DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                    while (!File.Exists(release))
                    {
                        if (DateTime.UtcNow >= deadline)
                        {
                            return 2;
                        }

                        Thread.Sleep(25);
                    }
                }

                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 3;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
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
