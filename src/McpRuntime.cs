using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace FilePromptAIWin7
{
    internal sealed class McpRuntime : IDisposable
    {
        private const int MaximumTools = 128;
        private readonly List<IMcpConnection> connections;
        private readonly Dictionary<string, ToolBinding> bindings;
        private readonly List<McpToolDefinition> tools;

        private McpRuntime()
        {
            connections = new List<IMcpConnection>();
            bindings = new Dictionary<string, ToolBinding>(
                StringComparer.OrdinalIgnoreCase);
            tools = new List<McpToolDefinition>();
        }

        public IList<McpToolDefinition> Tools
        {
            get { return tools.AsReadOnly(); }
        }

        public static async Task<McpRuntime> ConnectAsync(
            IEnumerable<McpServerDefinition> definitions,
            Action<string> onStatus,
            CancellationToken cancellationToken)
        {
            McpRuntime runtime = new McpRuntime();
            try
            {
                IList<McpServerDefinition> enabled = (definitions ??
                    new McpServerDefinition[0])
                    .Where(server => server != null && server.Enabled)
                    .Select(server => server.Clone())
                    .ToList();
                if (enabled.Count > 20)
                {
                    throw new McpException("最多同时启用 20 个 MCP 服务。");
                }

                List<string> failures = new List<string>();
                foreach (McpServerDefinition server in enabled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Notify(onStatus, "正在连接 MCP · " + server.Name);
                    IMcpConnection connection = null;
                    try
                    {
                        ExtensionSettings validation =
                            new ExtensionSettings();
                        validation.McpServers.Add(server);
                        ExtensionStore.Validate(validation);
                        connection = CreateConnection(server);
                        await connection.InitializeAsync(cancellationToken)
                            .ConfigureAwait(false);
                        IList<McpToolDefinition> discovered =
                            await connection.ListToolsAsync(cancellationToken)
                                .ConfigureAwait(false);
                        if (discovered.Count > MaximumTools -
                            runtime.tools.Count)
                        {
                            throw new McpException(
                                "已启用的 MCP 工具总数超过 128 个，请减少服务或工具数量。");
                        }

                        foreach (McpToolDefinition tool in discovered)
                        {
                            string publicName = BuildPublicName(
                                server,
                                tool.ToolName,
                                runtime.bindings.Keys);
                            tool.PublicName = publicName;
                            tool.ServerId = server.Id;
                            tool.ServerName = server.Name;
                            tool.RequireConfirmation =
                                server.RequireConfirmation;
                            runtime.tools.Add(tool);
                            runtime.bindings.Add(
                                publicName,
                                new ToolBinding
                                {
                                    Connection = connection,
                                    Tool = tool
                                });
                        }

                        runtime.connections.Add(connection);
                    }
                    catch (OperationCanceledException)
                    {
                        DisposeConnection(connection);
                        throw;
                    }
                    catch (Exception exception)
                    {
                        DisposeConnection(connection);
                        string failure = "MCP 服务“" + server.Name +
                            "”连接失败：" + exception.Message;
                        failures.Add(failure);
                        Notify(onStatus, failure + "；已跳过");
                    }
                }

                if (enabled.Count > 0 && runtime.connections.Count == 0 &&
                    failures.Count > 0)
                {
                    throw new McpException(
                        "所有已启用的 MCP 服务均连接失败。\r\n" +
                        string.Join("\r\n", failures));
                }

                Notify(
                    onStatus,
                    runtime.tools.Count == 0
                        ? (failures.Count == 0
                            ? "MCP 已连接，但没有发现可用工具"
                            : "MCP 可用服务已连接，但没有发现工具；另有 " +
                                failures.Count + " 个服务失败")
                        : "MCP 已就绪 · " + runtime.tools.Count + " 个工具" +
                            (failures.Count == 0
                                ? string.Empty
                                : " · " + failures.Count + " 个服务已跳过"));
                return runtime;
            }
            catch (Exception exception)
            {
                runtime.Dispose();
                if (exception is OperationCanceledException ||
                    exception is McpException)
                {
                    throw;
                }

                throw new McpException(
                    "MCP 初始化失败：" + exception.Message,
                    exception);
            }
        }

        public McpToolDefinition GetTool(string publicName)
        {
            ToolBinding binding;
            return bindings.TryGetValue(
                publicName ?? string.Empty,
                out binding)
                ? binding.Tool
                : null;
        }

        public Task<McpToolResult> CallToolAsync(
            string publicName,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            ToolBinding binding;
            if (!bindings.TryGetValue(
                publicName ?? string.Empty,
                out binding))
            {
                throw new McpException(
                    "模型请求了未知 MCP 工具：“" + publicName + "”。");
            }

            return binding.Connection.CallToolAsync(
                binding.Tool.ToolName,
                argumentsJson,
                cancellationToken);
        }

        public void Dispose()
        {
            for (int index = connections.Count - 1; index >= 0; index--)
            {
                try
                {
                    connections[index].Dispose();
                }
                catch
                {
                    // Continue releasing the remaining MCP connections.
                }
            }

            connections.Clear();
            bindings.Clear();
            tools.Clear();
        }

        private static IMcpConnection CreateConnection(
            McpServerDefinition definition)
        {
            return string.Equals(
                definition.Transport,
                "http",
                StringComparison.OrdinalIgnoreCase)
                ? (IMcpConnection)new HttpMcpConnection(definition)
                : new StdioMcpConnection(definition);
        }

        private static void DisposeConnection(IMcpConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            try
            {
                connection.Dispose();
            }
            catch
            {
                // Preserve the original initialization failure.
            }
        }

        private static string BuildPublicName(
            McpServerDefinition server,
            string toolName,
            IEnumerable<string> existingNames)
        {
            string source = "mcp__" + SanitizeName(server.Name) + "__" +
                SanitizeName(toolName);
            string value = source;
            if (value.Length > 64)
            {
                value = value.Substring(0, 53).TrimEnd('_') + "_" +
                    ShortHash(source);
            }

            HashSet<string> existing = new HashSet<string>(
                existingNames,
                StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(value))
            {
                return value;
            }

            string suffix = "_" + ShortHash(server.Id + ":" + toolName);
            int keep = Math.Max(1, 64 - suffix.Length);
            string candidate = value.Substring(
                0,
                Math.Min(keep, value.Length)) + suffix;
            int duplicate = 2;
            while (existing.Contains(candidate))
            {
                string numberedSuffix = suffix + "_" +
                    duplicate.ToString(CultureInfo.InvariantCulture);
                keep = Math.Max(1, 64 - numberedSuffix.Length);
                candidate = value.Substring(
                    0,
                    Math.Min(keep, value.Length)) + numberedSuffix;
                duplicate++;
            }

            return candidate;
        }

        private static string SanitizeName(string value)
        {
            StringBuilder result = new StringBuilder();
            foreach (char character in value ?? string.Empty)
            {
                bool valid = (character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' || character == '-';
                result.Append(valid ? character : '_');
            }

            string sanitized = result.ToString().Trim('_');
            return string.IsNullOrEmpty(sanitized) ? "tool" : sanitized;
        }

        private static string ShortHash(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            using (System.Security.Cryptography.SHA256 hash =
                System.Security.Cryptography.SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(bytes);
                StringBuilder result = new StringBuilder(10);
                for (int index = 0; index < 5; index++)
                {
                    result.Append(digest[index].ToString("x2"));
                }

                return result.ToString();
            }
        }

        private static void Notify(Action<string> callback, string value)
        {
            if (callback != null && !string.IsNullOrWhiteSpace(value))
            {
                callback(value);
            }
        }

        private sealed class ToolBinding
        {
            public IMcpConnection Connection { get; set; }
            public McpToolDefinition Tool { get; set; }
        }
    }

    internal interface IMcpConnection : IDisposable
    {
        Task InitializeAsync(CancellationToken cancellationToken);
        Task<IList<McpToolDefinition>> ListToolsAsync(
            CancellationToken cancellationToken);
        Task<McpToolResult> CallToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken);
    }

    internal abstract class McpConnectionBase : IMcpConnection
    {
        private const string LatestProtocolVersion = "2025-06-18";
        protected const int MaximumMessageCharacters = 16 * 1024 * 1024;
        protected const int MaximumToolResultCharacters = 1000000;
        private static readonly HashSet<string> SupportedProtocolVersions =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "2024-11-05",
                "2025-03-26",
                "2025-06-18"
            };

        private int nextRequestId;
        private string negotiatedProtocolVersion;

        protected McpConnectionBase(McpServerDefinition definition)
        {
            Definition = definition;
            Json = new JavaScriptSerializer();
            Json.MaxJsonLength = MaximumMessageCharacters;
            Json.RecursionLimit = 128;
            negotiatedProtocolVersion = LatestProtocolVersion;
        }

        protected McpServerDefinition Definition { get; private set; }
        protected JavaScriptSerializer Json { get; private set; }
        protected string NegotiatedProtocolVersion
        {
            get { return negotiatedProtocolVersion; }
        }

        public async Task InitializeAsync(
            CancellationToken cancellationToken)
        {
            object result = await SendRequestAsync(
                "initialize",
                new Dictionary<string, object>
                {
                    { "protocolVersion", LatestProtocolVersion },
                    {
                        "capabilities",
                        new Dictionary<string, object>()
                    },
                    {
                        "clientInfo",
                        new Dictionary<string, object>
                        {
                            { "name", "FilePromptAIWin7" },
                            { "version", "1.7.0" }
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
            IDictionary<string, object> initialize = AsDictionary(result);
            string protocolVersion = GetString(
                initialize,
                "protocolVersion");
            if (initialize == null ||
                string.IsNullOrWhiteSpace(protocolVersion) ||
                protocolVersion.Length > 64)
            {
                throw new McpException(
                    "MCP 服务“" + Definition.Name +
                    "”返回了无效的 initialize 响应。");
            }

            if (!SupportedProtocolVersions.Contains(protocolVersion))
            {
                throw new McpException(
                    "MCP 服务“" + Definition.Name + "”选择了不支持的协议版本“" +
                    protocolVersion + "”。本程序支持：" +
                    string.Join("、", SupportedProtocolVersions.OrderBy(
                        value => value)) + "。");
            }

            negotiatedProtocolVersion = protocolVersion;

            await SendNotificationAsync(
                "notifications/initialized",
                new Dictionary<string, object>(),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<IList<McpToolDefinition>> ListToolsAsync(
            CancellationToken cancellationToken)
        {
            List<McpToolDefinition> result =
                new List<McpToolDefinition>();
            string cursor = string.Empty;
            for (int page = 0; page < 20; page++)
            {
                Dictionary<string, object> parameters =
                    new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(cursor))
                {
                    parameters["cursor"] = cursor;
                }

                object response = await SendRequestAsync(
                    "tools/list",
                    parameters,
                    cancellationToken).ConfigureAwait(false);
                IDictionary<string, object> body = AsDictionary(response);
                IList toolItems = AsList(GetValue(body, "tools"));
                if (toolItems == null)
                {
                    throw new McpException(
                        "MCP 服务“" + Definition.Name +
                        "”的 tools/list 响应无效。");
                }

                foreach (object item in toolItems)
                {
                    IDictionary<string, object> tool = AsDictionary(item);
                    string name = GetString(tool, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        throw new McpException(
                            "MCP 服务“" + Definition.Name +
                            "”返回了没有名称的工具。");
                    }

                    if (name.Length > 256)
                    {
                        throw new McpException(
                            "MCP 服务“" + Definition.Name +
                            "”返回了超过 256 字符的工具名称。");
                    }

                    string description = GetString(tool, "description");
                    if (description.Length > 2000)
                    {
                        description = description.Substring(0, 2000);
                    }

                    object schema = GetValue(tool, "inputSchema");
                    if (schema == null)
                    {
                        schema = new Dictionary<string, object>
                        {
                            { "type", "object" },
                            {
                                "properties",
                                new Dictionary<string, object>()
                            }
                        };
                    }
                    else if (AsDictionary(schema) == null)
                    {
                        throw new McpException(
                            "MCP 工具“" + name +
                            "”的输入结构必须是 JSON 对象。");
                    }

                    string serializedSchema = Json.Serialize(schema);
                    if (serializedSchema.Length > 200000)
                    {
                        throw new McpException(
                            "MCP 工具“" + name + "”的输入结构过大。");
                    }

                    result.Add(new McpToolDefinition
                    {
                        ToolName = name,
                        Description = description,
                        InputSchema = schema
                    });
                }

                cursor = GetString(body, "nextCursor");
                if (string.IsNullOrEmpty(cursor))
                {
                    return result;
                }
            }

            throw new McpException(
                "MCP 服务“" + Definition.Name +
                "”的工具分页超过安全限制。");
        }

        public async Task<McpToolResult> CallToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            object arguments = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                try
                {
                    arguments = Json.DeserializeObject(argumentsJson);
                }
                catch (Exception exception)
                {
                    throw new McpException(
                        "模型为 MCP 工具生成了无效 JSON 参数：" +
                        exception.Message,
                        exception);
                }
            }

            if (AsDictionary(arguments) == null)
            {
                throw new McpException("MCP 工具参数必须是 JSON 对象。");
            }

            object response = await SendRequestAsync(
                "tools/call",
                new Dictionary<string, object>
                {
                    { "name", toolName },
                    { "arguments", arguments }
                },
                cancellationToken).ConfigureAwait(false);
            IDictionary<string, object> body = AsDictionary(response);
            if (body == null)
            {
                throw new McpException("MCP 工具返回了无效结果。");
            }

            McpToolResult result = new McpToolResult();
            result.IsError = GetBoolean(body, "isError");
            result.Content = RedactConfigurationValues(
                FormatToolResult(body));
            return result;
        }

        private string RedactConfigurationValues(string content)
        {
            string result = content ?? string.Empty;
            List<string> sensitive = new List<string>();
            sensitive.Add(Definition.Command);
            sensitive.Add(Definition.WorkingDirectory);
            sensitive.Add(Definition.Url);
            sensitive.AddRange(Definition.Arguments ?? new string[0]);
            sensitive.AddRange((Definition.Environment ??
                new Dictionary<string, string>()).Values);
            sensitive.AddRange((Definition.Headers ??
                new Dictionary<string, string>()).Values);

            foreach (string value in sensitive
                .Where(value => !string.IsNullOrWhiteSpace(value) &&
                    value.Length >= 8)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => value.Length))
            {
                result = ReplaceOrdinalIgnoreCase(
                    result,
                    value,
                    "[已隐藏 MCP 配置]");
            }

            if (result.Length <= MaximumToolResultCharacters)
            {
                return result;
            }

            int cutoff = MaximumToolResultCharacters;
            if (char.IsHighSurrogate(result[cutoff - 1]) &&
                char.IsLowSurrogate(result[cutoff]))
            {
                cutoff--;
            }

            return result.Substring(0, cutoff) +
                "\r\n[工具结果脱敏后超过 1,000,000 字符，已截断]";
        }

        private static string ReplaceOrdinalIgnoreCase(
            string source,
            string oldValue,
            string newValue)
        {
            int index = source.IndexOf(
                oldValue,
                StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return source;
            }

            StringBuilder result = new StringBuilder(source.Length);
            int offset = 0;
            while (index >= 0)
            {
                result.Append(source, offset, index - offset);
                result.Append(newValue);
                offset = index + oldValue.Length;
                index = source.IndexOf(
                    oldValue,
                    offset,
                    StringComparison.OrdinalIgnoreCase);
            }

            result.Append(source, offset, source.Length - offset);
            return result.ToString();
        }

        protected async Task<object> SendRequestAsync(
            string method,
            object parameters,
            CancellationToken cancellationToken)
        {
            int id = Interlocked.Increment(ref nextRequestId);
            Dictionary<string, object> request =
                new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", id },
                    { "method", method },
                    { "params", parameters }
                };
            IDictionary<string, object> response = await SendEnvelopeAsync(
                request,
                id,
                true,
                cancellationToken).ConfigureAwait(false);
            IDictionary<string, object> error = AsDictionary(
                GetValue(response, "error"));
            if (error != null)
            {
                string message = GetString(error, "message");
                throw new McpException(
                    "MCP 服务“" + Definition.Name + "”返回错误：" +
                    (string.IsNullOrWhiteSpace(message)
                        ? "未知 JSON-RPC 错误"
                        : message));
            }

            if (!response.ContainsKey("result"))
            {
                throw new McpException(
                    "MCP 服务“" + Definition.Name +
                    "”没有返回 JSON-RPC result。");
            }

            return response["result"];
        }

        protected async Task SendNotificationAsync(
            string method,
            object parameters,
            CancellationToken cancellationToken)
        {
            Dictionary<string, object> notification =
                new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "method", method },
                    { "params", parameters }
                };
            await SendEnvelopeAsync(
                notification,
                0,
                false,
                cancellationToken).ConfigureAwait(false);
        }

        protected abstract Task<IDictionary<string, object>>
            SendEnvelopeAsync(
                IDictionary<string, object> envelope,
                int expectedId,
                bool expectsResponse,
                CancellationToken cancellationToken);

        protected string Serialize(object value)
        {
            string serialized = Json.Serialize(value);
            if (serialized.Length > MaximumMessageCharacters)
            {
                throw new McpException("MCP JSON-RPC 消息超过 16 MB 安全限制。");
            }

            return serialized;
        }

        protected IDictionary<string, object> DeserializeEnvelope(string text)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                text.Length > MaximumMessageCharacters)
            {
                throw new McpException("MCP 返回了空消息或超大消息。");
            }

            IDictionary<string, object> value;
            try
            {
                value = Json.DeserializeObject(text) as
                    IDictionary<string, object>;
            }
            catch (Exception exception)
            {
                throw new McpException(
                    "MCP 返回了无法解析的 JSON-RPC 消息。",
                    exception);
            }

            if (value == null || GetString(value, "jsonrpc") != "2.0")
            {
                throw new McpException("MCP 返回了无效的 JSON-RPC 消息。");
            }

            return value;
        }

        protected static bool MatchesId(
            IDictionary<string, object> envelope,
            int expectedId)
        {
            object id = GetValue(envelope, "id");
            long parsed;
            return id != null && long.TryParse(
                Convert.ToString(id, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed) && parsed == expectedId;
        }

        private string FormatToolResult(IDictionary<string, object> body)
        {
            StringBuilder text = new StringBuilder();
            IList content = AsList(GetValue(body, "content"));
            if (content != null)
            {
                foreach (object item in content)
                {
                    IDictionary<string, object> block = AsDictionary(item);
                    string type = GetString(block, "type");
                    if (string.Equals(
                        type,
                        "text",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        AppendSection(text, GetString(block, "text"));
                    }
                    else if (string.Equals(
                        type,
                        "image",
                        StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            type,
                            "audio",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        AppendSection(
                            text,
                            "[MCP 返回了" +
                            (type == "image" ? "图片" : "音频") +
                            "数据；当前模型工具消息仅传递文本说明。]");
                    }
                    else
                    {
                        AppendSection(text, Json.Serialize(item));
                    }
                }
            }

            object structured = GetValue(body, "structuredContent");
            if (structured != null)
            {
                AppendSection(text, Json.Serialize(structured));
            }

            if (text.Length == 0)
            {
                text.Append(Json.Serialize(body));
            }

            if (text.Length > MaximumToolResultCharacters)
            {
                int cutoff = MaximumToolResultCharacters;
                if (char.IsHighSurrogate(text[cutoff - 1]) &&
                    char.IsLowSurrogate(text[cutoff]))
                {
                    cutoff--;
                }

                return text.ToString(0, cutoff) +
                    "\r\n[工具结果超过 1,000,000 字符，已截断]";
            }

            return text.ToString();
        }

        private static void AppendSection(StringBuilder target, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (target.Length > 0)
            {
                target.AppendLine();
            }

            target.Append(value);
        }

        protected static IDictionary<string, object> AsDictionary(object value)
        {
            return value as IDictionary<string, object>;
        }

        protected static IList AsList(object value)
        {
            return value as IList;
        }

        protected static object GetValue(
            IDictionary<string, object> value,
            string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result)
                ? result
                : null;
        }

        protected static string GetString(
            IDictionary<string, object> value,
            string key)
        {
            object result = GetValue(value, key);
            return result == null
                ? string.Empty
                : Convert.ToString(result, CultureInfo.InvariantCulture);
        }

        protected static bool GetBoolean(
            IDictionary<string, object> value,
            string key)
        {
            object result = GetValue(value, key);
            bool parsed;
            return result != null && bool.TryParse(
                Convert.ToString(result, CultureInfo.InvariantCulture),
                out parsed) && parsed;
        }

        public abstract void Dispose();
    }

    internal sealed class StdioMcpConnection : McpConnectionBase
    {
        private const int MaximumStderrCharacters = 16000;
        private static readonly object ProcessEnvironmentSync = new object();
        private readonly object processSync;
        private readonly object stderrSync;
        private readonly StringBuilder stderr;
        private Process process;
        private SafeJobHandle processJob;

        public StdioMcpConnection(McpServerDefinition definition)
            : base(definition)
        {
            processSync = new object();
            stderrSync = new object();
            stderr = new StringBuilder();
        }

        protected override async Task<IDictionary<string, object>>
            SendEnvelopeAsync(
                IDictionary<string, object> envelope,
                int expectedId,
                bool expectsResponse,
                CancellationToken cancellationToken)
        {
            EnsureStarted();
            cancellationToken.ThrowIfCancellationRequested();
            using (CancellationTokenRegistration registration =
                cancellationToken.Register(KillProcess))
            {
                try
                {
                    await process.StandardInput.WriteLineAsync(
                        Serialize(envelope)).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync()
                        .ConfigureAwait(false);
                    if (!expectsResponse)
                    {
                        return new Dictionary<string, object>();
                    }

                    while (true)
                    {
                        string line = await process.StandardOutput
                            .ReadLineAsync()
                            .ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (line == null)
                        {
                            throw new McpException(
                                "MCP 服务“" + Definition.Name +
                                "”已退出。" + BuildStderrSuffix());
                        }

                        IDictionary<string, object> response =
                            DeserializeEnvelope(line);
                        if (MatchesId(response, expectedId))
                        {
                            return response;
                        }

                        if (GetValue(response, "id") != null &&
                            !string.IsNullOrEmpty(GetString(response, "method")))
                        {
                            await SendMethodNotFoundAsync(response)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (IOException exception)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw new McpException(
                        "MCP stdio 连接中断。" + BuildStderrSuffix(),
                        exception);
                }
                catch (ObjectDisposedException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw;
                }
            }
        }

        public override void Dispose()
        {
            KillProcess();
            Process current;
            SafeJobHandle job;
            lock (processSync)
            {
                current = process;
                process = null;
                job = processJob;
                processJob = null;
            }

            if (job != null)
            {
                job.Dispose();
            }

            if (current != null)
            {
                current.Dispose();
            }
        }

        private void EnsureStarted()
        {
            if (process != null)
            {
                return;
            }

            string workingDirectory = string.IsNullOrWhiteSpace(
                Definition.WorkingDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetFullPath(Definition.WorkingDirectory);
            if (!Directory.Exists(workingDirectory))
            {
                throw new McpException(
                    "MCP 工作目录不存在：“" +
                    Definition.WorkingDirectory + "”。");
            }

            string resolvedCommand = ResolveCommand(
                Definition.Command,
                workingDirectory,
                Definition.Environment);
            bool usesCommandInterpreter = IsBatchFile(resolvedCommand);
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = usesCommandInterpreter
                ? GetCommandInterpreterPath()
                : resolvedCommand;
            start.Arguments = usesCommandInterpreter
                ? BuildBatchArguments(
                    resolvedCommand,
                    Definition.Arguments)
                : BuildArguments(Definition.Arguments);
            start.WorkingDirectory = workingDirectory;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            process = new Process();
            process.StartInfo = start;
            process.EnableRaisingEvents = true;
            process.ErrorDataReceived += delegate(
                object sender,
                DataReceivedEventArgs args)
            {
                if (string.IsNullOrEmpty(args.Data))
                {
                    return;
                }

                lock (stderrSync)
                {
                    if (stderr.Length < MaximumStderrCharacters)
                    {
                        stderr.AppendLine(args.Data);
                    }
                }
            };
            SafeJobHandle job = TryCreateKillOnCloseJob();
            try
            {
                if (!StartProcessWithEnvironment(
                    process,
                    Definition.Environment))
                {
                    throw new McpException("MCP 进程未能启动。");
                }

                if (job != null && !AssignProcessToJobObject(
                    job,
                    process.Handle))
                {
                    job.Dispose();
                    job = null;
                }

                lock (processSync)
                {
                    processJob = job;
                    job = null;
                }

                process.BeginErrorReadLine();
            }
            catch (Exception exception)
            {
                if (job != null)
                {
                    job.Dispose();
                }

                KillProcess();
                Process failed = process;
                process = null;
                if (failed != null)
                {
                    failed.Dispose();
                }

                throw new McpException(
                    "无法启动 MCP 服务“" + Definition.Name + "”：" +
                    exception.Message,
                    exception);
            }
        }

        private static bool StartProcessWithEnvironment(
            Process target,
            IDictionary<string, string> overrides)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return target.Start();
            }

            // .NET Framework can throw while materializing
            // ProcessStartInfo.EnvironmentVariables when the inherited block
            // contains case-variant duplicates such as Path and PATH. Apply
            // explicit overrides only for the instant CreateProcess inherits
            // the block, then restore the parent process immediately.
            lock (ProcessEnvironmentSync)
            {
                Dictionary<string, string> previous =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (KeyValuePair<string, string> pair in overrides)
                    {
                        previous[pair.Key] = Environment.GetEnvironmentVariable(
                            pair.Key,
                            EnvironmentVariableTarget.Process);
                        Environment.SetEnvironmentVariable(
                            pair.Key,
                            pair.Value ?? string.Empty,
                            EnvironmentVariableTarget.Process);
                    }

                    return target.Start();
                }
                finally
                {
                    foreach (KeyValuePair<string, string> pair in previous)
                    {
                        Environment.SetEnvironmentVariable(
                            pair.Key,
                            pair.Value,
                            EnvironmentVariableTarget.Process);
                    }
                }
            }
        }

        private async Task SendMethodNotFoundAsync(
            IDictionary<string, object> request)
        {
            Dictionary<string, object> response =
                new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", GetValue(request, "id") },
                    {
                        "error",
                        new Dictionary<string, object>
                        {
                            { "code", -32601 },
                            {
                                "message",
                                "FilePrompt AI does not support server-initiated requests."
                            }
                        }
                    }
                };
            await process.StandardInput.WriteLineAsync(Serialize(response))
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        private string BuildStderrSuffix()
        {
            lock (stderrSync)
            {
                string value = stderr.ToString().Trim();
                return string.IsNullOrEmpty(value)
                    ? string.Empty
                    : "\r\n服务日志：" + value;
            }
        }

        private void KillProcess()
        {
            Process current;
            SafeJobHandle job;
            lock (processSync)
            {
                current = process;
                job = processJob;
                processJob = null;
            }

            if (current == null)
            {
                if (job != null)
                {
                    job.Dispose();
                }

                return;
            }

            try
            {
                if (job != null)
                {
                    job.Dispose();
                }
                else if (!current.HasExited)
                {
                    KillDescendantProcesses(current.Id);
                    current.Kill();
                }

                if (!current.HasExited)
                {
                    current.WaitForExit(1000);
                }
            }
            catch
            {
                // The process may have exited between the checks.
            }
        }

        private static string ResolveCommand(
            string command,
            string workingDirectory,
            IDictionary<string, string> environment)
        {
            string value = (command ?? string.Empty).Trim();
            if (value.Length >= 2 && value[0] == '"' &&
                value[value.Length - 1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new McpException("MCP 命令不能为空。");
            }

            List<string> extensions = GetExecutableExtensions(
                GetEffectiveEnvironmentVariable("PATHEXT", environment));
            bool hasDirectory = Path.IsPathRooted(value) ||
                value.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                value.IndexOf(Path.AltDirectorySeparatorChar) >= 0;
            if (hasDirectory)
            {
                string path = Path.IsPathRooted(value)
                    ? value
                    : Path.Combine(workingDirectory, value);
                string resolved = FindExecutable(path, extensions);
                if (!string.IsNullOrEmpty(resolved))
                {
                    return resolved;
                }
            }
            else
            {
                List<string> searchDirectories = new List<string>();
                searchDirectories.Add(workingDirectory);
                string path = GetEffectiveEnvironmentVariable(
                    "PATH",
                    environment);
                foreach (string item in (path ?? string.Empty).Split(';'))
                {
                    string directory = item.Trim().Trim('"');
                    if (directory.Length == 0)
                    {
                        continue;
                    }

                    if (!Path.IsPathRooted(directory))
                    {
                        directory = Path.Combine(
                            workingDirectory,
                            directory);
                    }

                    searchDirectories.Add(directory);
                }

                foreach (string directory in searchDirectories.Distinct(
                    StringComparer.OrdinalIgnoreCase))
                {
                    string candidate;
                    try
                    {
                        candidate = Path.Combine(directory, value);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    string resolved = FindExecutable(candidate, extensions);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        return resolved;
                    }
                }
            }

            throw new McpException(
                "找不到 MCP 命令“" + value +
                "”。请检查命令名称以及 PATH/PATHEXT 配置。");
        }

        private static string FindExecutable(
            string path,
            IList<string> extensions)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return string.Empty;
            }

            if (File.Exists(fullPath) && IsSupportedExecutable(fullPath))
            {
                return fullPath;
            }

            if (!string.IsNullOrEmpty(Path.GetExtension(fullPath)))
            {
                return string.Empty;
            }

            foreach (string extension in extensions)
            {
                string candidate = fullPath + extension;
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static List<string> GetExecutableExtensions(string value)
        {
            bool useDefaults = string.IsNullOrWhiteSpace(value);
            string source = useDefaults
                ? ".COM;.EXE;.BAT;.CMD"
                : value;
            List<string> result = new List<string>();
            foreach (string item in source.Split(';'))
            {
                string extension = item.Trim();
                if (extension.Length == 0)
                {
                    continue;
                }

                if (extension[0] != '.')
                {
                    extension = "." + extension;
                }

                if (string.Equals(extension, ".COM", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".EXE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".BAT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".CMD", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(extension);
                }
            }

            if (result.Count == 0 && useDefaults)
            {
                result.AddRange(new[] { ".COM", ".EXE", ".BAT", ".CMD" });
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string GetEffectiveEnvironmentVariable(
            string name,
            IDictionary<string, string> overrides)
        {
            if (overrides != null)
            {
                foreach (KeyValuePair<string, string> pair in overrides)
                {
                    if (string.Equals(
                        pair.Key,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value ?? string.Empty;
                    }
                }
            }

            return Environment.GetEnvironmentVariable(
                name,
                EnvironmentVariableTarget.Process) ?? string.Empty;
        }

        private static bool IsSupportedExecutable(string path)
        {
            string extension = Path.GetExtension(path);
            return string.IsNullOrEmpty(extension) ||
                string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBatchFile(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetCommandInterpreterPath()
        {
            string path = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            if (!File.Exists(path))
            {
                throw new McpException("找不到 Windows 命令解释器 cmd.exe。");
            }

            return path;
        }

        private static string BuildBatchArguments(
            string command,
            IEnumerable<string> arguments)
        {
            if (command.IndexOf('%') >= 0 || command.IndexOf('"') >= 0)
            {
                throw new McpException(
                    "批处理 MCP 命令路径不能包含百分号或双引号。");
            }

            StringBuilder result = new StringBuilder();
            result.Append("/d /s /v:off /c \"\"");
            result.Append(command);
            result.Append('"');
            foreach (string argument in arguments ?? new string[0])
            {
                string value = argument ?? string.Empty;
                if (value.IndexOf('"') >= 0 || value.IndexOf('%') >= 0 ||
                    value.IndexOf('\0') >= 0 || value.IndexOf('\r') >= 0 ||
                    value.IndexOf('\n') >= 0)
                {
                    throw new McpException(
                        "批处理 MCP 参数不能包含双引号、百分号或换行符。");
                }

                result.Append(" \"");
                result.Append(value);
                result.Append('"');
            }

            result.Append('"');
            return result.ToString();
        }

        private static SafeJobHandle TryCreateKillOnCloseJob()
        {
            SafeJobHandle job = CreateJobObject(IntPtr.Zero, null);
            if (job == null || job.IsInvalid)
            {
                if (job != null)
                {
                    job.Dispose();
                }

                return null;
            }

            JobObjectExtendedLimitInformation information =
                new JobObjectExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags =
                JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                ref information,
                (uint)Marshal.SizeOf(typeof(
                    JobObjectExtendedLimitInformation))))
            {
                job.Dispose();
                return null;
            }

            return job;
        }

        private static void KillDescendantProcesses(int rootProcessId)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(
                ToolhelpSnapshotProcess,
                0);
            if (snapshot == InvalidHandleValue)
            {
                return;
            }

            Dictionary<int, List<int>> children =
                new Dictionary<int, List<int>>();
            try
            {
                ProcessEntry32 entry = new ProcessEntry32();
                entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        int parentId = unchecked((int)entry.ParentProcessId);
                        int processId = unchecked((int)entry.ProcessId);
                        List<int> values;
                        if (!children.TryGetValue(parentId, out values))
                        {
                            values = new List<int>();
                            children[parentId] = values;
                        }

                        values.Add(processId);
                        entry.Size = (uint)Marshal.SizeOf(
                            typeof(ProcessEntry32));
                    }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            KillDescendants(rootProcessId, children, new HashSet<int>());
        }

        private static void KillDescendants(
            int processId,
            IDictionary<int, List<int>> children,
            ISet<int> visited)
        {
            if (!visited.Add(processId))
            {
                return;
            }

            List<int> values;
            if (!children.TryGetValue(processId, out values))
            {
                return;
            }

            foreach (int childId in values)
            {
                KillDescendants(childId, children, visited);
                try
                {
                    using (Process child = Process.GetProcessById(childId))
                    {
                        if (!child.HasExited)
                        {
                            child.Kill();
                            child.WaitForExit(500);
                        }
                    }
                }
                catch
                {
                    // The child may have exited between snapshot and cleanup.
                }
            }
        }

        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint ToolhelpSnapshotProcess = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableFile;
        }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeJobHandle()
                : base(true)
            {
            }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeJobHandle CreateJobObject(
            IntPtr securityAttributes,
            string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(
            SafeJobHandle job,
            IntPtr processHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(
            uint flags,
            uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(
            IntPtr snapshot,
            ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(
            IntPtr snapshot,
            ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private static string BuildArguments(IEnumerable<string> arguments)
        {
            return string.Join(
                " ",
                (arguments ?? new string[0]).Select(QuoteArgument));
        }

        private static string QuoteArgument(string argument)
        {
            string value = argument ?? string.Empty;
            if (value.Length > 0 && value.IndexOfAny(
                new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return value;
            }

            StringBuilder result = new StringBuilder();
            result.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }

                result.Append('\\', backslashes);
                backslashes = 0;
                result.Append(character);
            }

            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }
    }

    internal sealed class HttpMcpConnection : McpConnectionBase
    {
        private readonly HttpClient client;
        private readonly Uri endpoint;
        private string sessionId;

        public HttpMcpConnection(McpServerDefinition definition)
            : base(definition)
        {
            endpoint = new Uri(definition.Url, UriKind.Absolute);
            HttpClientHandler handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;
            handler.AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate;
            handler.UseProxy = true;
            handler.Proxy = WebRequest.DefaultWebProxy;
            client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMilliseconds(Timeout.Infinite);
        }

        protected override async Task<IDictionary<string, object>>
            SendEnvelopeAsync(
                IDictionary<string, object> envelope,
                int expectedId,
                bool expectsResponse,
                CancellationToken cancellationToken)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Post,
                endpoint))
            {
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue(
                        "text/event-stream",
                        0.9));
                request.Headers.TryAddWithoutValidation(
                    "MCP-Protocol-Version",
                    NegotiatedProtocolVersion);
                if (!string.IsNullOrEmpty(sessionId))
                {
                    request.Headers.TryAddWithoutValidation(
                        "Mcp-Session-Id",
                        sessionId);
                }

                foreach (KeyValuePair<string, string> pair in
                    Definition.Headers)
                {
                    if (IsReservedHeader(pair.Key))
                    {
                        throw new McpException(
                            "MCP 请求头“" + pair.Key + "”由程序管理，不能覆盖。");
                    }

                    if (!request.Headers.TryAddWithoutValidation(
                        pair.Key,
                        pair.Value ?? string.Empty))
                    {
                        throw new McpException(
                            "MCP 请求头“" + pair.Key + "”无效。");
                    }
                }

                request.Content = new StringContent(
                    Serialize(envelope),
                    Encoding.UTF8,
                    "application/json");
                HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                using (response)
                using (CancellationTokenRegistration registration =
                    cancellationToken.Register(delegate { response.Dispose(); }))
                {
                    CaptureSessionId(response);
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await ReadBoundedContentAsync(
                            response.Content,
                            65536,
                            cancellationToken).ConfigureAwait(false);
                        if (errorBody.Length > 4000)
                        {
                            errorBody = errorBody.Substring(0, 4000);
                        }

                        throw new McpException(
                            "MCP HTTP " +
                            ((int)response.StatusCode).ToString(
                                CultureInfo.InvariantCulture) +
                            "：" + errorBody);
                    }

                    if (!expectsResponse ||
                        response.StatusCode == HttpStatusCode.Accepted ||
                        response.StatusCode == HttpStatusCode.NoContent)
                    {
                        return new Dictionary<string, object>();
                    }

                    string mediaType = response.Content.Headers.ContentType == null
                        ? string.Empty
                        : response.Content.Headers.ContentType.MediaType;
                    if (mediaType.IndexOf(
                        "text/event-stream",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return await ReadSseAsync(
                            response.Content,
                            expectedId,
                            cancellationToken).ConfigureAwait(false);
                    }

                    string body = await ReadBoundedContentAsync(
                        response.Content,
                        MaximumMessageCharacters,
                        cancellationToken).ConfigureAwait(false);
                    IDictionary<string, object> result =
                        DeserializeEnvelope(body);
                    if (!MatchesId(result, expectedId))
                    {
                        throw new McpException(
                            "MCP HTTP 响应 ID 与请求不匹配。");
                    }

                    return result;
                }
            }
        }

        public override void Dispose()
        {
            client.Dispose();
        }

        private static async Task<string> ReadBoundedContentAsync(
            HttpContent content,
            int maximumCharacters,
            CancellationToken cancellationToken)
        {
            try
            {
                long maximumBytes = (long)maximumCharacters * 4L;
                if (content.Headers.ContentLength.HasValue &&
                    content.Headers.ContentLength.Value > maximumBytes)
                {
                    throw new McpException("MCP HTTP 响应超过安全限制。");
                }

                using (Stream stream = await content.ReadAsStreamAsync()
                    .ConfigureAwait(false))
                using (StreamReader reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    true,
                    4096,
                    true))
                {
                    StringBuilder result = new StringBuilder();
                    char[] buffer = new char[4096];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = await reader.ReadAsync(
                            buffer,
                            0,
                            buffer.Length).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            return result.ToString();
                        }

                        if (result.Length + read > maximumCharacters)
                        {
                            throw new McpException(
                                "MCP HTTP 响应超过安全限制。");
                        }

                        result.Append(buffer, 0, read);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (ObjectDisposedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        private async Task<IDictionary<string, object>> ReadSseAsync(
            HttpContent content,
            int expectedId,
            CancellationToken cancellationToken)
        {
            try
            {
                StringBuilder data = new StringBuilder();
                using (Stream stream = await content.ReadAsStreamAsync()
                    .ConfigureAwait(false))
                using (StreamReader reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    true,
                    4096,
                    true))
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string line = await reader.ReadLineAsync()
                            .ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }

                        if (line.Length == 0)
                        {
                            if (data.Length > 0)
                            {
                                IDictionary<string, object> response =
                                    DeserializeEnvelope(data.ToString());
                                data.Clear();
                                if (MatchesId(response, expectedId))
                                {
                                    return response;
                                }
                            }

                            continue;
                        }

                        if (line.StartsWith(
                            "data:",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            if (data.Length > 0)
                            {
                                data.Append('\n');
                            }

                            data.Append(line.Substring(5).TrimStart());
                            if (data.Length > MaximumMessageCharacters)
                            {
                                throw new McpException(
                                    "MCP SSE 消息超过 16 MB 安全限制。");
                            }
                        }
                    }
                }

                throw new McpException("MCP SSE 流结束前没有返回对应响应。");
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (ObjectDisposedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        private void CaptureSessionId(HttpResponseMessage response)
        {
            IEnumerable<string> values;
            if (response.Headers.TryGetValues("Mcp-Session-Id", out values))
            {
                string value = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(value) && value.Length <= 4096)
                {
                    sessionId = value;
                }
            }
        }

        private static bool IsReservedHeader(string name)
        {
            string value = (name ?? string.Empty).Trim();
            return string.Equals(value, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    "Content-Length",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    "Transfer-Encoding",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    "Connection",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    "Mcp-Session-Id",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    "MCP-Protocol-Version",
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
