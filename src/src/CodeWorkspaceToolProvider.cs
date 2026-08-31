using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FilePromptAIWin7
{
    internal sealed class WorkspaceEditProposal
    {
        public string RelativePath { get; internal set; }
        public string ExpectedSha256 { get; internal set; }
        public string OriginalText { get; internal set; }
        public string NewText { get; internal set; }
        public string UnifiedDiff { get; internal set; }
    }

    internal sealed class CodeWorkspaceToolProvider
    {
        internal const string ListFilesToolName = "workspace_list_files";
        internal const string SearchToolName = "workspace_search";
        internal const string ReadFileToolName = "workspace_read_file";
        internal const string ProposeEditToolName = "workspace_propose_edit";

        private const int DefaultMaximumResults = 100;
        private const int MaximumResults = 500;
        private const int MaximumDiffLines = 500;
        private const int MaximumDiffCharacters = 256 * 1024;
        private readonly CodeWorkspace workspace;
        private readonly Func<WorkspaceEditProposal, CancellationToken, Task<bool>>
            confirmEdit;
        private readonly JavaScriptSerializer json;
        private readonly IList<McpToolDefinition> tools;
        private readonly object readAuthorizationLock;
        private readonly Dictionary<string, string> readAuthorizations;

        public CodeWorkspaceToolProvider(
            CodeWorkspace workspace,
            Func<WorkspaceEditProposal, CancellationToken, Task<bool>>
                confirmEdit)
        {
            if (workspace == null)
            {
                throw new ArgumentNullException("workspace");
            }

            if (confirmEdit == null)
            {
                throw new ArgumentNullException(
                    "confirmEdit",
                    "代码写入必须提供逐次差异确认回调。");
            }

            this.workspace = workspace;
            this.confirmEdit = confirmEdit;
            json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
            tools = CreateTools();
            readAuthorizationLock = new object();
            readAuthorizations = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        }

        public IList<McpToolDefinition> Tools
        {
            get { return new List<McpToolDefinition>(tools); }
        }

        public bool EditApplied { get; private set; }

        public async Task<McpToolResult> ExecuteAsync(
            ModelToolCall call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (call == null || string.IsNullOrWhiteSpace(call.Name))
            {
                return Error("缺少工作区工具名称。");
            }

            try
            {
                IDictionary<string, object> arguments = ParseArguments(call);
                if (string.Equals(
                    call.Name,
                    ListFilesToolName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return ExecuteListFiles(arguments);
                }

                if (string.Equals(
                    call.Name,
                    SearchToolName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return ExecuteSearch(arguments);
                }

                if (string.Equals(
                    call.Name,
                    ReadFileToolName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return ExecuteReadFile(arguments);
                }

                if (string.Equals(
                    call.Name,
                    ProposeEditToolName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return await ExecuteProposeEditAsync(
                        arguments,
                        cancellationToken).ConfigureAwait(false);
                }

                return Error("未授权的工作区工具：“" + call.Name + "”。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Error(exception.Message);
            }
        }

        private McpToolResult ExecuteListFiles(
            IDictionary<string, object> arguments)
        {
            string directory = OptionalString(arguments, "relative_directory");
            int maximum = OptionalResultLimit(arguments);
            IList<WorkspaceFileInfo> files = workspace.ListFiles(
                directory,
                maximum);
            List<object> values = new List<object>();
            foreach (WorkspaceFileInfo file in files)
            {
                values.Add(new Dictionary<string, object>
                {
                    { "relative_path", file.RelativePath },
                    { "length", file.Length },
                    {
                        "last_write_utc",
                        file.LastWriteUtc.ToUniversalTime().ToString("o")
                    }
                });
            }

            return Success(new Dictionary<string, object>
            {
                { "files", values.ToArray() },
                { "returned", values.Count },
                { "limit", maximum }
            });
        }

        private McpToolResult ExecuteSearch(
            IDictionary<string, object> arguments)
        {
            string directory = OptionalString(arguments, "relative_directory");
            string query = RequiredString(arguments, "query", false);
            int maximum = OptionalResultLimit(arguments);
            IList<WorkspaceSearchResult> matches = workspace.Search(
                directory,
                query,
                maximum);
            List<object> values = new List<object>();
            foreach (WorkspaceSearchResult match in matches)
            {
                values.Add(new Dictionary<string, object>
                {
                    { "relative_path", match.RelativePath },
                    { "line_number", match.LineNumber },
                    { "line_text", match.LineText }
                });
            }

            return Success(new Dictionary<string, object>
            {
                { "matches", values.ToArray() },
                { "returned", values.Count },
                { "limit", maximum }
            });
        }

        private McpToolResult ExecuteReadFile(
            IDictionary<string, object> arguments)
        {
            string path = RequiredString(arguments, "relative_path", false);
            WorkspaceReadResult result = workspace.ReadFile(path);
            lock (readAuthorizationLock)
            {
                readAuthorizations[result.RelativePath] = result.Sha256;
            }
            return Success(new Dictionary<string, object>
            {
                { "relative_path", result.RelativePath },
                { "text", result.Text },
                { "sha256", result.Sha256 },
                { "encoding", result.EncodingName },
                { "has_bom", result.HasBom },
                { "new_line", result.NewLine },
                { "has_final_new_line", result.HasFinalNewLine },
                { "length", result.Length }
            });
        }

        private async Task<McpToolResult> ExecuteProposeEditAsync(
            IDictionary<string, object> arguments,
            CancellationToken cancellationToken)
        {
            string path = RequiredString(arguments, "relative_path", false);
            string expectedSha256 = RequiredString(
                arguments,
                "base_sha256",
                false);
            string newText = RequiredString(arguments, "new_text", true);
            string authorizationPath = path.Replace('/', '\\');
            string authorizedSha256;
            lock (readAuthorizationLock)
            {
                if (!readAuthorizations.TryGetValue(
                        authorizationPath,
                        out authorizedSha256) ||
                    !string.Equals(
                        authorizedSha256,
                        expectedSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Error(
                        "本轮工具尚未读取该文件，或 base_sha256 不是最近一次 " +
                        "workspace_read_file 返回的值。请先调用读取工具再修改。");
                }

                // One read authorizes one proposal only. Rejection, stale
                // content, or an apply failure all require a fresh read.
                readAuthorizations.Remove(authorizationPath);
            }
            WorkspaceEditPreview preview = workspace.PreviewEdit(
                new WorkspaceEdit(path, expectedSha256, newText));
            if (!preview.HasChanges)
            {
                return Success(new Dictionary<string, object>
                {
                    { "status", "unchanged" },
                    { "relative_path", preview.RelativePath },
                    { "sha256", preview.ExpectedSha256 }
                });
            }

            WorkspaceEditProposal proposal = new WorkspaceEditProposal
            {
                RelativePath = preview.RelativePath,
                ExpectedSha256 = preview.ExpectedSha256,
                OriginalText = preview.OriginalText,
                NewText = preview.NewText,
                UnifiedDiff = BuildUnifiedDiff(
                    preview.RelativePath,
                    preview.OriginalText,
                    preview.NewText)
            };
            cancellationToken.ThrowIfCancellationRequested();
            Task<bool> confirmation = confirmEdit(proposal, cancellationToken);
            if (confirmation == null)
            {
                return Error("代码写入确认回调没有返回结果，未写入文件。");
            }

            bool approved = await confirmation.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!approved)
            {
                return Error("用户拒绝了本次代码修改，未写入文件。");
            }

            WorkspaceEditResult result = workspace.ApplyEdit(
                new WorkspaceEdit(
                    preview.RelativePath,
                    preview.ExpectedSha256,
                    preview.NewText));
            bool applied = result.BackupCreated &&
                !string.Equals(
                    result.Sha256,
                    preview.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase);
            if (applied)
            {
                EditApplied = true;
            }
            return Success(new Dictionary<string, object>
            {
                { "status", applied ? "applied" : "unchanged" },
                { "relative_path", result.RelativePath },
                { "sha256", result.Sha256 },
                { "backup_created", result.BackupCreated },
                { "undo_available", result.UndoAvailable }
            });
        }

        private IDictionary<string, object> ParseArguments(ModelToolCall call)
        {
            object parsed = json.DeserializeObject(
                string.IsNullOrWhiteSpace(call.ArgumentsJson)
                    ? "{}"
                    : call.ArgumentsJson);
            IDictionary<string, object> result =
                parsed as IDictionary<string, object>;
            if (result == null)
            {
                throw new InvalidOperationException(
                    "工具参数必须是 JSON 对象。");
            }

            return result;
        }

        private static string RequiredString(
            IDictionary<string, object> arguments,
            string name,
            bool allowEmpty)
        {
            object raw;
            string value = arguments != null && arguments.TryGetValue(name, out raw)
                ? raw as string
                : null;
            if (value == null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            {
                throw new InvalidOperationException(
                    "工具参数“" + name + "”必须是" +
                    (allowEmpty ? "字符串" : "非空字符串") + "。");
            }

            return value;
        }

        private static string OptionalString(
            IDictionary<string, object> arguments,
            string name)
        {
            object raw;
            if (arguments == null || !arguments.TryGetValue(name, out raw) ||
                raw == null)
            {
                return string.Empty;
            }

            string value = raw as string;
            if (value == null)
            {
                throw new InvalidOperationException(
                    "工具参数“" + name + "”必须是字符串。");
            }

            return value;
        }

        private static int OptionalResultLimit(
            IDictionary<string, object> arguments)
        {
            object raw;
            if (arguments == null || !arguments.TryGetValue("max_results", out raw) ||
                raw == null)
            {
                return DefaultMaximumResults;
            }

            long parsed;
            string serialized = Convert.ToString(
                raw,
                CultureInfo.InvariantCulture);
            if (!long.TryParse(
                serialized,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed))
            {
                throw new InvalidOperationException(
                    "工具参数“max_results”必须是整数。");
            }

            if (parsed < 1 || parsed > MaximumResults)
            {
                throw new InvalidOperationException(
                    "工具参数“max_results”必须在 1 到 500 之间。");
            }

            return (int)parsed;
        }

        private McpToolResult Success(object value)
        {
            return new McpToolResult
            {
                Content = json.Serialize(value),
                IsError = false
            };
        }

        private McpToolResult Error(string message)
        {
            return new McpToolResult
            {
                Content = string.IsNullOrWhiteSpace(message)
                    ? "工作区工具执行失败。"
                    : RedactSensitivePaths(message),
                IsError = true
            };
        }

        private string RedactSensitivePaths(string value)
        {
            string result = value ?? string.Empty;
            try
            {
                result = RedactPath(result, AppDataPath.Root, "[程序数据]");
            }
            catch
            {
            }

            return RedactPath(result, workspace.RootPath, "[工作区]");
        }

        private static string RedactPath(
            string value,
            string path,
            string replacement)
        {
            string result = value ?? string.Empty;
            string root = (path ?? string.Empty).TrimEnd('\\', '/');
            if (root.Length == 0)
            {
                return result;
            }

            result = ReplaceIgnoreCase(result, root, replacement);
            string alternate = root.Replace('\\', '/');
            if (!string.Equals(alternate, root, StringComparison.Ordinal))
            {
                result = ReplaceIgnoreCase(result, alternate, replacement);
            }

            return result;
        }

        private static string ReplaceIgnoreCase(
            string value,
            string oldValue,
            string newValue)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldValue))
            {
                return value ?? string.Empty;
            }

            int offset = 0;
            StringBuilder result = new StringBuilder();
            while (offset < value.Length)
            {
                int found = value.IndexOf(
                    oldValue,
                    offset,
                    StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    result.Append(value, offset, value.Length - offset);
                    break;
                }

                result.Append(value, offset, found - offset);
                result.Append(newValue ?? string.Empty);
                offset = found + oldValue.Length;
            }

            return result.ToString();
        }

        private static IList<McpToolDefinition> CreateTools()
        {
            return new List<McpToolDefinition>
            {
                CreateTool(
                    ListFilesToolName,
                    "列出所选文件所在代码目录内的文件。路径必须相对于工作区根目录，" +
                    "空路径表示根目录。不能访问工作区外部。",
                    Properties(
                        StringProperty("relative_directory", "相对目录，可留空。"),
                        IntegerProperty("max_results", "返回数量，1 到 500。")),
                    new string[0]),
                CreateTool(
                    SearchToolName,
                    "在所选文件所在代码目录内搜索文本，并返回相对路径、行号和匹配行。" +
                    "不能访问工作区外部。",
                    Properties(
                        StringProperty("relative_directory", "相对目录，可留空。"),
                        StringProperty("query", "要搜索的非空文本。"),
                        IntegerProperty("max_results", "返回数量，1 到 500。")),
                    new[] { "query" }),
                CreateTool(
                    ReadFileToolName,
                    "读取工作区内一个已存在的文本文件。返回完整文本和 sha256；" +
                    "单个文件最大 256 KiB；修改前必须先读取并使用返回的 sha256。",
                    Properties(
                        StringProperty("relative_path", "工作区内的相对文件路径。")),
                    new[] { "relative_path" }),
                CreateTool(
                    ProposeEditToolName,
                    "提交一个已存在文本文件的完整新内容。base_sha256 必须来自最近一次读取；" +
                    "同一次读取只允许提交一次修改。程序会显示差异并且只有用户逐次确认后" +
                    "才写入，不能创建或删除文件，修改后的文件最大 256 KiB。",
                    Properties(
                        StringProperty("relative_path", "工作区内的相对文件路径。"),
                        StringProperty("base_sha256", "读取文件时返回的 SHA-256。"),
                        StringProperty("new_text", "修改后的完整文件内容，可为空字符串。")),
                    new[] { "relative_path", "base_sha256", "new_text" })
            };
        }

        private static McpToolDefinition CreateTool(
            string name,
            string description,
            IDictionary<string, object> properties,
            string[] required)
        {
            return new McpToolDefinition
            {
                PublicName = name,
                ServerId = "builtin-code-workspace",
                ServerName = "代码工作区",
                ToolName = name,
                Description = description,
                InputSchema = new Dictionary<string, object>
                {
                    { "type", "object" },
                    { "properties", properties },
                    { "required", required },
                    { "additionalProperties", false }
                },
                // The provider always performs its own mandatory diff approval.
                RequireConfirmation = false
            };
        }

        private static IDictionary<string, object> Properties(
            params KeyValuePair<string, object>[] values)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> value in values)
            {
                result.Add(value.Key, value.Value);
            }

            return result;
        }

        private static KeyValuePair<string, object> StringProperty(
            string name,
            string description)
        {
            return new KeyValuePair<string, object>(
                name,
                new Dictionary<string, object>
                {
                    { "type", "string" },
                    { "description", description }
                });
        }

        private static KeyValuePair<string, object> IntegerProperty(
            string name,
            string description)
        {
            return new KeyValuePair<string, object>(
                name,
                new Dictionary<string, object>
                {
                    { "type", "integer" },
                    { "minimum", 1 },
                    { "maximum", MaximumResults },
                    { "description", description }
                });
        }

        private static string BuildUnifiedDiff(
            string relativePath,
            string originalText,
            string newText)
        {
            relativePath = (relativePath ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            DiffLine[] oldLines = SplitLines(originalText);
            DiffLine[] newLines = SplitLines(newText);
            int prefix = 0;
            while (prefix < oldLines.Length && prefix < newLines.Length &&
                string.Equals(
                    oldLines[prefix],
                    newLines[prefix]))
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < oldLines.Length - prefix &&
                suffix < newLines.Length - prefix &&
                string.Equals(
                    oldLines[oldLines.Length - 1 - suffix],
                    newLines[newLines.Length - 1 - suffix]))
            {
                suffix++;
            }

            int contextStart = Math.Max(0, prefix - 3);
            int oldChangedEnd = oldLines.Length - suffix;
            int newChangedEnd = newLines.Length - suffix;
            int oldContextEnd = Math.Min(oldLines.Length, oldChangedEnd + 3);
            int newContextEnd = Math.Min(newLines.Length, newChangedEnd + 3);
            StringBuilder diff = new StringBuilder();
            diff.Append("--- a/").Append(relativePath).AppendLine();
            diff.Append("+++ b/").Append(relativePath).AppendLine();
            diff.Append("@@ -").Append(contextStart + 1).Append(',')
                .Append(oldContextEnd - contextStart).Append(" +")
                .Append(contextStart + 1).Append(',')
                .Append(newContextEnd - contextStart).AppendLine(" @@");
            int emitted = 0;
            for (int index = contextStart; index < prefix; index++)
            {
                if (!AppendDiffLine(diff, ' ', oldLines[index], ref emitted))
                {
                    throw CreateDiffTooLargeException();
                }
            }

            for (int index = prefix; index < oldChangedEnd; index++)
            {
                if (!AppendDiffLine(diff, '-', oldLines[index], ref emitted))
                {
                    throw CreateDiffTooLargeException();
                }
            }

            for (int index = prefix; index < newChangedEnd; index++)
            {
                if (!AppendDiffLine(diff, '+', newLines[index], ref emitted))
                {
                    throw CreateDiffTooLargeException();
                }
            }

            int trailing = Math.Min(oldContextEnd - oldChangedEnd,
                newContextEnd - newChangedEnd);
            for (int index = 0; index < trailing; index++)
            {
                if (!AppendDiffLine(
                    diff,
                    ' ',
                    oldLines[oldChangedEnd + index],
                    ref emitted))
                {
                    throw CreateDiffTooLargeException();
                }
            }

            return diff.ToString();
        }

        private static bool AppendDiffLine(
            StringBuilder diff,
            char prefix,
            DiffLine line,
            ref int emitted)
        {
            string value = line == null ? string.Empty : line.DisplayText;
            if (emitted >= MaximumDiffLines ||
                diff.Length + value.Length + 3 >
                    MaximumDiffCharacters)
            {
                return false;
            }

            diff.Append(prefix).AppendLine(value);
            emitted++;
            return true;
        }

        private static InvalidOperationException CreateDiffTooLargeException()
        {
            return new InvalidOperationException(
                "代码差异超过 500 行或 256 KB，无法完整显示并安全确认，" +
                "因此未写入文件。请缩小单次修改范围。");
        }

        private static DiffLine[] SplitLines(string value)
        {
            string text = value ?? string.Empty;
            List<DiffLine> lines = new List<DiffLine>();
            int start = 0;
            int index = 0;
            while (index < text.Length)
            {
                string ending = null;
                int endingLength = 0;
                if (text[index] == '\r')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        ending = "CRLF";
                        endingLength = 2;
                    }
                    else
                    {
                        ending = "CR";
                        endingLength = 1;
                    }
                }
                else if (text[index] == '\n')
                {
                    ending = "LF";
                    endingLength = 1;
                }

                if (ending == null)
                {
                    index++;
                    continue;
                }

                lines.Add(new DiffLine(
                    text.Substring(start, index - start),
                    ending));
                index += endingLength;
                start = index;
            }
            if (start < text.Length)
            {
                lines.Add(new DiffLine(
                    text.Substring(start),
                    "EOF"));
            }
            return lines.ToArray();
        }

        private sealed class DiffLine
        {
            private readonly string text;
            private readonly string ending;

            public DiffLine(string text, string ending)
            {
                this.text = text ?? string.Empty;
                this.ending = ending ?? "EOF";
            }

            public string DisplayText
            {
                get { return text + " [" + ending + "]"; }
            }

            public override bool Equals(object value)
            {
                DiffLine other = value as DiffLine;
                return other != null &&
                    string.Equals(text, other.text, StringComparison.Ordinal) &&
                    string.Equals(ending, other.ending, StringComparison.Ordinal);
            }

            public override int GetHashCode()
            {
                return (text.GetHashCode() * 397) ^ ending.GetHashCode();
            }
        }
    }
}
