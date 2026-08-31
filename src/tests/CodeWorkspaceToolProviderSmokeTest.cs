using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FilePromptAIWin7
{
    internal static class CodeWorkspaceToolProviderSmokeTest
    {
        private static int Main()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "FilePromptAI-ProviderTest-" + Guid.NewGuid().ToString("N"));
            string dataRoot = Path.Combine(root, "data");
            Environment.SetEnvironmentVariable("FILEPROMPTAI_DATA_ROOT", dataRoot);
            Directory.CreateDirectory(root);
            try
            {
                string selected = Path.Combine(root, "selected.cs");
                string second = Path.Combine(root, "second.txt");
                File.WriteAllText(
                    selected,
                    "class Old\r\n{\r\n}\r\n",
                    new UTF8Encoding(false));
                File.WriteAllText(
                    second,
                    "needle line\r\n",
                    new UTF8Encoding(false));

                TestReadSearchAndApply(selected);
                TestNormalizedNoOpDoesNotCountAsEdit(selected);
                TestNormalizedContentChangePreviewMatchesWrite(root, selected);
                TestMixedNewLineOnlyChangeIsFullyVisible(root, selected);
                TestFinalNewLinePreviewMatchesWrite(root, selected);
                TestOversizedNewLineOnlyDiffIsRejected(root, selected);
                TestReadAuthorizationAndRejectedEdit(selected);
                TestOversizedDiffIsRejected(selected);
                TestStaleHash(selected);
                TestConfirmationIsMandatory(selected);
                TestMaximumEscapedReadResult(root, selected);
                TestErrorPathRedaction(selected, dataRoot);
                Console.WriteLine("PASS | code workspace tool provider");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL | code workspace tool provider");
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "FILEPROMPTAI_DATA_ROOT",
                    null);
                TryDeleteTree(root);
            }
        }

        private static void TestReadSearchAndApply(string selected)
        {
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                WorkspaceEditProposal shown = null;
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate(
                            WorkspaceEditProposal proposal,
                            CancellationToken cancellationToken)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            shown = proposal;
                            return Task.FromResult(true);
                        });
                Assert(provider.Tools.Count == 4, "four workspace tools");
                foreach (McpToolDefinition tool in provider.Tools)
                {
                    Assert(!tool.RequireConfirmation, "provider owns confirmation");
                }

                McpToolResult listed = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ListFilesToolName,
                    "{\"relative_directory\":\"\",\"max_results\":10}");
                Assert(!listed.IsError, "list succeeds");
                Assert(
                    listed.Content.IndexOf("selected.cs", StringComparison.Ordinal) >= 0 &&
                    listed.Content.IndexOf("workspace_root", StringComparison.Ordinal) < 0,
                    "list does not disclose absolute root");

                McpToolResult searched = Execute(
                    provider,
                    CodeWorkspaceToolProvider.SearchToolName,
                    "{\"query\":\"needle\",\"max_results\":10}");
                Assert(
                    !searched.IsError &&
                    searched.Content.IndexOf("second.txt", StringComparison.Ordinal) >= 0,
                    "search succeeds");
                AssertNoWorkspaceRoot(listed.Content, workspace.RootPath, "list response");
                AssertNoWorkspaceRoot(searched.Content, workspace.RootPath, "search response");

                McpToolResult read = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ReadFileToolName,
                    "{\"relative_path\":\"selected.cs\"}");
                Assert(!read.IsError, "read succeeds");
                IDictionary<string, object> readJson = Parse(read.Content);
                AssertNoWorkspaceRoot(read.Content, workspace.RootPath, "read response");
                string hash = Convert.ToString(readJson["sha256"]);
                string newText = "class New\r\n{\r\n}\r\n";
                McpToolResult applied = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", "selected.cs" },
                        { "base_sha256", hash },
                        { "new_text", newText }
                    }));
                Assert(!applied.IsError, "approved edit applies");
                AssertNoWorkspaceRoot(applied.Content, workspace.RootPath, "edit response");
                Assert(shown != null, "proposal displayed");
                Assert(
                    shown.OriginalText.IndexOf("Old", StringComparison.Ordinal) >= 0 &&
                    shown.NewText.IndexOf("New", StringComparison.Ordinal) >= 0 &&
                    shown.UnifiedDiff.IndexOf("-class Old", StringComparison.Ordinal) >= 0 &&
                    shown.UnifiedDiff.IndexOf("+class New", StringComparison.Ordinal) >= 0,
                    "proposal includes complete texts and diff");
                Assert(
                    File.ReadAllText(selected).IndexOf(
                        "class New",
                        StringComparison.Ordinal) >= 0,
                    "approved content written");

                McpToolResult invalidPath = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ReadFileToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", workspace.RootPath }
                    }));
                Assert(invalidPath.IsError, "absolute path rejected by provider");
                AssertNoWorkspaceRoot(
                    invalidPath.Content,
                    workspace.RootPath,
                    "error response");
            }
        }

        private static void TestReadAuthorizationAndRejectedEdit(string selected)
        {
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                int confirmations = 0;
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate
                        {
                            confirmations++;
                            return Task.FromResult(false);
                        });
                WorkspaceReadResult direct = workspace.ReadFile("selected.cs");
                McpToolResult unread = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", "selected.cs" },
                        { "base_sha256", direct.Sha256 },
                        { "new_text", "unread" }
                    }));
                Assert(unread.IsError, "edit without provider read is rejected");
                Assert(confirmations == 0,
                    "edit without provider read is not confirmable");

                IDictionary<string, object> read = ReadThroughProvider(
                    provider,
                    "selected.cs");
                string hash = Convert.ToString(read["sha256"]);
                McpToolResult rejected = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", "selected.cs" },
                        { "base_sha256", hash },
                        { "new_text", "rejected" }
                    }));
                Assert(rejected.IsError, "rejected edit reports error");
                Assert(confirmations == 1, "rejected edit was confirmed once");
                Assert(
                    File.ReadAllText(selected).IndexOf(
                        "class New",
                        StringComparison.Ordinal) >= 0,
                    "rejected edit does not write");

                McpToolResult repeated = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", "selected.cs" },
                        { "base_sha256", hash },
                        { "new_text", "repeated" }
                    }));
                Assert(repeated.IsError,
                    "one provider read cannot authorize a second proposal");
                Assert(confirmations == 1,
                    "repeated proposal is not shown for confirmation");
            }
        }

        private static void TestStaleHash(string selected)
        {
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                int confirmations = 0;
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate
                        {
                            confirmations++;
                            return Task.FromResult(true);
                        });
                IDictionary<string, object> read = ReadThroughProvider(
                    provider,
                    "selected.cs");
                string hash = Convert.ToString(read["sha256"]);
                File.WriteAllText(
                    selected,
                    "class ExternalChange\r\n{\r\n}\r\n",
                    new UTF8Encoding(false));

                McpToolResult stale = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", "selected.cs" },
                        { "base_sha256", hash },
                        { "new_text", "stale" }
                    }));
                Assert(stale.IsError, "file changed after provider read is rejected");
                Assert(confirmations == 0,
                    "stale edit is not shown for confirmation");
                Assert(
                    File.ReadAllText(selected).IndexOf(
                        "ExternalChange",
                        StringComparison.Ordinal) >= 0,
                    "stale edit preserves external file content");
            }
        }

        private static void TestNormalizedNoOpDoesNotCountAsEdit(
            string selected)
        {
            byte[] before = File.ReadAllBytes(selected);
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                int confirmations = 0;
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate
                        {
                            confirmations++;
                            return Task.FromResult(true);
                        });
                IDictionary<string, object> current = ReadThroughProvider(
                    provider,
                    "selected.cs");
                string currentText = Convert.ToString(current["text"]);
                string currentSha256 = Convert.ToString(current["sha256"]);
                string sameTextWithLf = currentText.Replace("\r\n", "\n");
                Assert(
                    !string.Equals(
                        currentText,
                        sameTextWithLf,
                        StringComparison.Ordinal),
                    "normalized no-op reaches core apply");
                McpToolResult result = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", "selected.cs" },
                        { "base_sha256", currentSha256 },
                        { "new_text", sameTextWithLf }
                    }));
                IDictionary<string, object> response = Parse(result.Content);
                Assert(!result.IsError, "normalized no-op succeeds");
                Assert(
                    Convert.ToString(response["status"]) == "unchanged",
                    "normalized no-op reports unchanged");
                Assert(confirmations == 0,
                    "normalized no-op does not show an empty confirmation");
                Assert(!provider.EditApplied,
                    "normalized no-op does not set EditApplied");
                Assert(!workspace.CanUndo,
                    "normalized no-op creates no backup or undo");
                AssertBytesEqual(
                    before,
                    File.ReadAllBytes(selected),
                    "normalized no-op leaves exact bytes");
            }
        }

        private static void TestMixedNewLineOnlyChangeIsFullyVisible(
            string root,
            string selected)
        {
            const string relative = "mixed-newline-diff.cs";
            string path = Path.Combine(root, relative);
            string original = "first\r\nsecond\nthird\r";
            string replacement = "first\nsecond\r\nthird\n";
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(original));
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                WorkspaceEditProposal shown = null;
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate(WorkspaceEditProposal proposal,
                            CancellationToken cancellationToken)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            shown = proposal;
                            return Task.FromResult(true);
                        });
                IDictionary<string, object> current = ReadThroughProvider(
                    provider,
                    relative);
                McpToolResult result = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", relative },
                        { "base_sha256", Convert.ToString(current["sha256"]) },
                        { "new_text", replacement }
                    }));

                Assert(!result.IsError, "newline-only edit applies");
                Assert(shown != null, "newline-only edit is confirmed");
                Assert(
                    shown.UnifiedDiff.IndexOf(
                        "-first [CRLF]",
                        StringComparison.Ordinal) >= 0 &&
                    shown.UnifiedDiff.IndexOf(
                        "+first [LF]",
                        StringComparison.Ordinal) >= 0 &&
                    shown.UnifiedDiff.IndexOf(
                        "-third [CR]",
                        StringComparison.Ordinal) >= 0 &&
                    shown.UnifiedDiff.IndexOf(
                        "+third [LF]",
                        StringComparison.Ordinal) >= 0,
                    "newline-only diff identifies every line ending");
                AssertBytesEqual(
                    Encoding.UTF8.GetBytes(replacement),
                    File.ReadAllBytes(path),
                    "newline-only diff matches exact written bytes");
            }
        }

        private static void TestNormalizedContentChangePreviewMatchesWrite(
            string root,
            string selected)
        {
            const string relative = "normalized-content.cs";
            string path = Path.Combine(root, relative);
            string original = "class Old\r\n{\r\n}\r\n";
            string proposed = "class New\n{\n}\n";
            string effective = "class New\r\n{\r\n}\r\n";
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(original));
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                WorkspaceEditProposal shown = null;
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate(WorkspaceEditProposal proposal,
                            CancellationToken cancellationToken)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            shown = proposal;
                            return Task.FromResult(true);
                        });
                IDictionary<string, object> current = ReadThroughProvider(
                    provider,
                    relative);
                McpToolResult result = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", relative },
                        { "base_sha256", Convert.ToString(current["sha256"]) },
                        { "new_text", proposed }
                    }));

                Assert(!result.IsError, "normalized content edit applies");
                Assert(
                    shown != null &&
                    string.Equals(shown.NewText, effective,
                        StringComparison.Ordinal),
                    "normalized content confirmation previews effective text");
                Assert(
                    shown.UnifiedDiff.IndexOf(
                        "+class New [CRLF]",
                        StringComparison.Ordinal) >= 0 &&
                    shown.UnifiedDiff.IndexOf(
                        "+class New [LF]",
                        StringComparison.Ordinal) < 0,
                    "normalized content diff uses effective line endings");
                AssertBytesEqual(
                    Encoding.UTF8.GetBytes(effective),
                    File.ReadAllBytes(path),
                    "normalized content preview matches exact written bytes");
            }
        }

        private static void TestFinalNewLinePreviewMatchesWrite(
            string root,
            string selected)
        {
            const string relative = "mixed-final-newline.cs";
            string path = Path.Combine(root, relative);
            string original = "first\r\nsecond\n";
            string proposed = "first\r\nsecond";
            string effective = "first\r\nsecond\r\n";
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(original));
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                WorkspaceEditProposal shown = null;
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate(WorkspaceEditProposal proposal,
                            CancellationToken cancellationToken)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            shown = proposal;
                            return Task.FromResult(true);
                        });
                IDictionary<string, object> current = ReadThroughProvider(
                    provider,
                    relative);
                McpToolResult result = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", relative },
                        { "base_sha256", Convert.ToString(current["sha256"]) },
                        { "new_text", proposed }
                    }));

                Assert(!result.IsError, "final-newline edit applies");
                Assert(
                    shown != null &&
                    string.Equals(shown.NewText, effective,
                        StringComparison.Ordinal),
                    "confirmation previews effective final newline");
                Assert(
                    shown.UnifiedDiff.IndexOf(
                        "-second [LF]",
                        StringComparison.Ordinal) >= 0 &&
                    shown.UnifiedDiff.IndexOf(
                        "+second [CRLF]",
                        StringComparison.Ordinal) >= 0,
                    "final-newline diff matches effective write");
                AssertBytesEqual(
                    Encoding.UTF8.GetBytes(effective),
                    File.ReadAllBytes(path),
                    "final-newline preview matches exact written bytes");
            }
        }

        private static void TestOversizedNewLineOnlyDiffIsRejected(
            string root,
            string selected)
        {
            const string relative = "oversized-newline-diff.cs";
            string path = Path.Combine(root, relative);
            StringBuilder original = new StringBuilder();
            StringBuilder replacement = new StringBuilder();
            for (int index = 0; index < 600; index++)
            {
                original.Append("line-").Append(index)
                    .Append(index % 2 == 0 ? "\r\n" : "\n");
                replacement.Append("line-").Append(index)
                    .Append(index % 2 == 0 ? "\n" : "\r\n");
            }
            byte[] originalBytes = Encoding.UTF8.GetBytes(original.ToString());
            File.WriteAllBytes(path, originalBytes);
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                int confirmations = 0;
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate
                        {
                            confirmations++;
                            return Task.FromResult(true);
                        });
                IDictionary<string, object> current = ReadThroughProvider(
                    provider,
                    relative);
                McpToolResult result = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", relative },
                        { "base_sha256", Convert.ToString(current["sha256"]) },
                        { "new_text", replacement.ToString() }
                    }));

                Assert(result.IsError, "oversized newline-only diff rejected");
                Assert(confirmations == 0,
                    "oversized newline-only diff is not confirmable");
                AssertBytesEqual(
                    originalBytes,
                    File.ReadAllBytes(path),
                    "oversized newline-only diff does not write");
            }
        }

        private static void TestConfirmationIsMandatory(string selected)
        {
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                Exception failure = null;
                try
                {
                    new CodeWorkspaceToolProvider(workspace, null);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                Assert(
                    failure is ArgumentNullException,
                    "missing confirmation rejected at construction");
            }
        }

        private static void TestOversizedDiffIsRejected(string selected)
        {
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                int confirmations = 0;
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate
                        {
                            confirmations++;
                            return Task.FromResult(true);
                        });
                IDictionary<string, object> current = ReadThroughProvider(
                    provider,
                    "selected.cs");
                StringBuilder replacement = new StringBuilder();
                for (int index = 0; index < 600; index++)
                {
                    replacement.Append("changed-").Append(index).Append("\r\n");
                }

                McpToolResult result = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", "selected.cs" },
                        { "base_sha256", Convert.ToString(current["sha256"]) },
                        { "new_text", replacement.ToString() }
                    }));
                Assert(result.IsError, "oversized diff rejected");
                Assert(confirmations == 0, "oversized diff is not confirmable");
                Assert(
                    File.ReadAllText(selected).IndexOf(
                        "class New",
                        StringComparison.Ordinal) >= 0,
                    "oversized diff does not write");
            }
        }

        private static void TestMaximumEscapedReadResult(
            string root,
            string selected)
        {
            string relative = "maximum-escaped.txt";
            string path = Path.Combine(root, relative);
            File.WriteAllBytes(
                path,
                Encoding.UTF8.GetBytes(new string('"', 256 * 1024)));
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate { return Task.FromResult(false); });
                McpToolResult result = Execute(
                    provider,
                    CodeWorkspaceToolProvider.ReadFileToolName,
                    Serialize(new Dictionary<string, object>
                    {
                        { "relative_path", relative }
                    }));
                Assert(!result.IsError, "maximum escaped file read succeeds");
                Assert(
                    result.Content.Length < 1024 * 1024,
                    "maximum escaped read result fits tool limit");
                IDictionary<string, object> parsed = Parse(result.Content);
                Assert(parsed != null, "maximum escaped read result is valid JSON");
                string hash = Convert.ToString(parsed["sha256"]);
                Assert(
                    hash.Length == 64 && IsLowerHex(hash),
                    "maximum escaped read returns SHA-256");
                Assert(
                    Convert.ToString(parsed["text"]).Length == 256 * 1024,
                    "maximum escaped read returns complete text");
            }
        }

        private static void TestErrorPathRedaction(
            string selected,
            string dataRoot)
        {
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selected))
            {
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate { return Task.FromResult(false); });
                MethodInfo error = typeof(CodeWorkspaceToolProvider).GetMethod(
                    "Error",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(error != null, "private error formatter exists");
                string slashWorkspace = workspace.RootPath.Replace('\\', '/');
                string slashData = dataRoot.Replace('\\', '/');
                McpToolResult result = (McpToolResult)error.Invoke(
                    provider,
                    new object[]
                    {
                        "workspace=" + workspace.RootPath +
                        "; workspaceSlash=" + slashWorkspace +
                        "; data=" + dataRoot +
                        "; dataSlash=" + slashData
                    });
                Assert(result.IsError, "redacted formatter reports error");
                AssertNoAbsolutePath(
                    result.Content,
                    workspace.RootPath,
                    "error formatter workspace path");
                AssertNoAbsolutePath(
                    result.Content,
                    dataRoot,
                    "error formatter app-data path");
                Assert(
                    result.Content.IndexOf(
                        "[工作区]",
                        StringComparison.Ordinal) >= 0 &&
                    result.Content.IndexOf(
                        "[程序数据]",
                        StringComparison.Ordinal) >= 0,
                    "error formatter keeps useful redaction labels");
            }
        }

        private static bool IsLowerHex(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static McpToolResult Execute(
            CodeWorkspaceToolProvider provider,
            string name,
            string arguments)
        {
            return provider.ExecuteAsync(
                new ModelToolCall
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    ArgumentsJson = arguments
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }

        private static IDictionary<string, object> ReadThroughProvider(
            CodeWorkspaceToolProvider provider,
            string relativePath)
        {
            McpToolResult result = Execute(
                provider,
                CodeWorkspaceToolProvider.ReadFileToolName,
                Serialize(new Dictionary<string, object>
                {
                    { "relative_path", relativePath }
                }));
            Assert(!result.IsError, "provider read authorizes one proposal");
            return Parse(result.Content);
        }

        private static IDictionary<string, object> Parse(string value)
        {
            return new JavaScriptSerializer().DeserializeObject(value) as
                IDictionary<string, object>;
        }

        private static string Serialize(object value)
        {
            return new JavaScriptSerializer().Serialize(value);
        }

        private static void TryDeleteTree(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name + " failed.");
            }

            Console.WriteLine("PASS | " + name);
        }

        private static void AssertBytesEqual(
            byte[] expected,
            byte[] actual,
            string name)
        {
            if (expected.Length != actual.Length)
            {
                throw new InvalidOperationException(name + " failed (length).");
            }
            for (int index = 0; index < expected.Length; index++)
            {
                if (expected[index] != actual[index])
                {
                    throw new InvalidOperationException(
                        name + " failed (byte " + index + ").");
                }
            }
            Console.WriteLine("PASS | " + name);
        }

        private static void AssertNoWorkspaceRoot(
            string response,
            string workspaceRoot,
            string name)
        {
            string value = response ?? string.Empty;
            string root = workspaceRoot ?? string.Empty;
            Assert(
                root.Length == 0 ||
                value.IndexOf(root, StringComparison.OrdinalIgnoreCase) < 0,
                name + " hides absolute workspace path");
            string alternate = root.Replace('\\', '/');
            Assert(
                alternate.Length == 0 ||
                value.IndexOf(alternate, StringComparison.OrdinalIgnoreCase) < 0,
                name + " hides slash-form workspace path");
        }

        private static void AssertNoAbsolutePath(
            string response,
            string path,
            string name)
        {
            string value = response ?? string.Empty;
            string root = (path ?? string.Empty).TrimEnd('\\', '/');
            Assert(
                root.Length == 0 ||
                value.IndexOf(root, StringComparison.OrdinalIgnoreCase) < 0,
                name + " hides backslash form");
            string alternate = root.Replace('\\', '/');
            Assert(
                alternate.Length == 0 ||
                value.IndexOf(alternate, StringComparison.OrdinalIgnoreCase) < 0,
                name + " hides slash form");
        }
    }
}
