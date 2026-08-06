using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FilePromptAIWin7
{
    internal sealed class ModelClient : IDisposable
    {
        private const int MaximumToolRounds = 8;
        private const int MaximumTools = 128;
        private const int MaximumToolCallsPerRound = 32;
        private const int MaximumTotalToolCalls = 64;
        private const int MaximumToolNameCharacters = 64;
        private const int MaximumToolCallIdCharacters = 256;
        private const int MaximumToolDescriptionCharacters = 2000;
        private const int MaximumToolSchemaCharacters = 200000;
        private const int MaximumCombinedToolDefinitionCharacters =
            4 * 1024 * 1024;
        private const int MaximumToolArgumentsCharacters = 1024 * 1024;
        private const int MaximumToolResultCharacters = 1024 * 1024;
        private const int MaximumToolTranscriptCharacters = 8 * 1024 * 1024;
        private const int MaximumRequestCharacters = 32 * 1024 * 1024;
        private const int MaximumResponseCharacters = 8 * 1024 * 1024;
        private const long MaximumResponseBytes = 16L * 1024L * 1024L;

        private readonly HttpClient client;
        private readonly JavaScriptSerializer json;

        public ModelClient()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate;
            handler.AllowAutoRedirect = false;
            handler.UseProxy = true;
            handler.Proxy = WebRequest.DefaultWebProxy;

            client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMilliseconds(Timeout.Infinite);
            json = new JavaScriptSerializer();
            json.MaxJsonLength = int.MaxValue;
            json.RecursionLimit = 256;
        }

        public async Task<string> GenerateAsync(
            ModelRequest request,
            Action<string> onDelta,
            Action<string> onStatus,
            CancellationToken cancellationToken)
        {
            ValidateRequest(request);
            EndpointAttempt attempt = BuildExactAttempt(request.EndpointUrl);
            Notify(onStatus, "正在连接模型接口…");

            AttemptException firstError = null;
            try
            {
                return await SendAttemptAsync(
                    attempt,
                    request,
                    true,
                    onDelta,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AttemptException exception)
            {
                firstError = exception;
            }
            catch (HttpRequestException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw CreateNetworkException(exception);
            }
            catch (IOException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new ModelCallException("连接中断：" + exception.Message);
            }
            catch (ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw;
            }

            if (!IsStreamUnsupported(firstError))
            {
                throw CreateUserFacingException(firstError);
            }

            Notify(onStatus, "接口不支持流式输出，正在改用普通请求…");
            AttemptException secondError = null;
            try
            {
                return await SendAttemptAsync(
                    attempt,
                    request,
                    false,
                    onDelta,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AttemptException exception)
            {
                secondError = exception;
            }
            catch (HttpRequestException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw CreateNetworkException(exception);
            }
            catch (IOException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new ModelCallException("连接中断：" + exception.Message);
            }
            catch (ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw;
            }

            throw CreateUserFacingException(secondError);
        }

        public async Task<string> GenerateWithToolsAsync(
            ModelRequest request,
            IList<McpToolDefinition> tools,
            Func<ModelToolCall, CancellationToken, Task<McpToolResult>>
                executeTool,
            Action<string> onDelta,
            Action<string> onStatus,
            CancellationToken cancellationToken)
        {
            ValidateRequest(request);
            if (tools == null || tools.Count == 0)
            {
                return await GenerateAsync(
                    request,
                    onDelta,
                    onStatus,
                    cancellationToken).ConfigureAwait(false);
            }

            if (executeTool == null)
            {
                throw new ArgumentNullException("executeTool");
            }

            EndpointAttempt attempt = BuildExactAttempt(request.EndpointUrl);
            ToolSet toolSet = BuildToolSet(tools);
            List<object> messages = BuildInitialMessages(request, true);
            HashSet<string> toolCallIds = new HashSet<string>(
                StringComparer.Ordinal);
            int toolRounds = 0;
            int totalToolCalls = 0;
            int toolTranscriptCharacters = 0;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Notify(
                        onStatus,
                        toolRounds == 0
                            ? "正在请求模型并准备工具…"
                            : "正在把工具结果交给模型…");

                    string payload = BuildToolPayload(
                        request,
                        messages,
                        toolSet.Payload);
                    object parsed = await SendToolAttemptAsync(
                        attempt,
                        request,
                        payload,
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    ToolRoundResponse response = ParseToolRoundResponse(
                        parsed,
                        toolSet.Names,
                        toolCallIds);

                    if (response.ToolCalls.Count == 0)
                    {
                        if (string.IsNullOrEmpty(response.Text))
                        {
                            throw new ModelCallException(
                                "模型没有返回可显示文本，也没有请求工具。");
                        }

                        Notify(onDelta, response.Text);
                        return response.Text;
                    }

                    if (toolRounds >= MaximumToolRounds)
                    {
                        throw new ModelCallException(
                            "模型连续请求工具超过 8 轮，已停止以避免无限循环。");
                    }

                    if (totalToolCalls + response.ToolCalls.Count >
                        MaximumTotalToolCalls)
                    {
                        throw new ModelCallException(
                            "本次生成请求的工具调用总数超过 64 个，已停止。");
                    }

                    messages.Add(response.AssistantMessage);
                    foreach (ModelToolCall toolCall in response.ToolCalls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int remaining = MaximumToolTranscriptCharacters -
                            toolTranscriptCharacters;
                        if (remaining <= 0)
                        {
                            throw new ModelCallException(
                                "工具结果累计超过 8 MB，已停止继续调用。");
                        }

                        Notify(onStatus, "正在调用工具 · " + toolCall.Name);
                        McpToolResult toolResult = await ExecuteToolAsync(
                            executeTool,
                            toolCall,
                            cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        string toolContent = FormatToolResult(
                            toolResult,
                            Math.Min(MaximumToolResultCharacters, remaining));
                        toolTranscriptCharacters += toolContent.Length;
                        messages.Add(new Dictionary<string, object>
                        {
                            { "role", "tool" },
                            { "tool_call_id", toolCall.Id },
                            { "content", toolContent }
                        });
                        Notify(onStatus, "工具已完成 · " + toolCall.Name);
                    }

                    totalToolCalls += response.ToolCalls.Count;
                    toolRounds++;
                }
            }
            catch (AttemptException exception)
            {
                throw CreateUserFacingException(exception);
            }
            catch (HttpRequestException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw CreateNetworkException(exception);
            }
            catch (IOException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new ModelCallException("连接中断：" + exception.Message);
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

        public async Task TestConnectionAsync(
            string endpointUrl,
            string apiKey,
            string modelName,
            CancellationToken cancellationToken)
        {
            ModelRequest request = new ModelRequest
            {
                EndpointUrl = endpointUrl,
                ApiKey = apiKey,
                ModelName = modelName,
                Prompt = "ping",
                Attachments = new List<InputItem>(),
                ConversationMessages = new List<ConversationMessage>()
            };
            ValidateRequest(request);
            EndpointAttempt attempt = BuildExactAttempt(request.EndpointUrl);

            try
            {
                await SendAttemptAsync(
                    attempt,
                    request,
                    false,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AttemptException exception)
            {
                throw CreateUserFacingException(exception);
            }
            catch (HttpRequestException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw CreateNetworkException(exception);
            }
            catch (IOException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new ModelCallException(
                    "\u8fde\u63a5\u4e2d\u65ad\uff1a" + exception.Message);
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

        private async Task<string> SendAttemptAsync(
            EndpointAttempt attempt,
            ModelRequest request,
            bool stream,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            string payload = BuildPayload(request, stream);
            if (payload.Length > MaximumRequestCharacters)
            {
                throw new ModelCallException(
                    "模型请求内容超过 32 MB 安全限制，请减少资料或会话内容。");
            }

            using (HttpRequestMessage message = new HttpRequestMessage(
                HttpMethod.Post,
                attempt.Url))
            {
                string key = (request.ApiKey ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    message.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", key);
                }

                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                    stream ? "text/event-stream" : "application/json"));
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                    "application/json",
                    0.9));
                message.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                using (response)
                using (CancellationTokenRegistration registration =
                    cancellationToken.Register(delegate { response.Dispose(); }))
                {
                    try
                    {
                        string requestId = GetRequestId(response);
                        if (!response.IsSuccessStatusCode)
                        {
                            string errorBody = await ReadBoundedResponseAsync(
                                response.Content,
                                cancellationToken).ConfigureAwait(false);
                            throw new AttemptException(
                                (int)response.StatusCode,
                                errorBody,
                                requestId);
                        }

                        string mediaType = response.Content.Headers.ContentType == null
                            ? string.Empty
                            : response.Content.Headers.ContentType.MediaType;

                        if (mediaType.IndexOf(
                            "text/event-stream",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            using (Stream streamBody = await response.Content
                                .ReadAsStreamAsync()
                                .ConfigureAwait(false))
                            {
                                return await ReadServerSentEventsAsync(
                                    streamBody,
                                    onDelta,
                                    cancellationToken).ConfigureAwait(false);
                            }
                        }

                        string body = await ReadBoundedResponseAsync(
                            response.Content,
                            cancellationToken).ConfigureAwait(false);
                        string result = ExtractText(Deserialize(body));
                        if (string.IsNullOrEmpty(result))
                        {
                            string error = ExtractErrorMessage(Deserialize(body));
                            if (!string.IsNullOrEmpty(error))
                            {
                                throw new ModelCallException(error);
                            }

                            throw new ModelCallException(
                                "接口请求成功，但响应中没有找到可显示的文本。");
                        }

                        Notify(onDelta, result);
                        return result;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        throw;
                    }
                    catch (IOException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        throw;
                    }
                    catch (HttpRequestException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        throw;
                    }
                }
            }
        }

        private async Task<object> SendToolAttemptAsync(
            EndpointAttempt attempt,
            ModelRequest request,
            string payload,
            CancellationToken cancellationToken)
        {
            using (HttpRequestMessage message = new HttpRequestMessage(
                HttpMethod.Post,
                attempt.Url))
            {
                string key = (request.ApiKey ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    message.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", key);
                }

                message.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
                message.Content = new StringContent(
                    payload,
                    Encoding.UTF8,
                    "application/json");

                HttpResponseMessage response = await client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                using (response)
                using (CancellationTokenRegistration registration =
                    cancellationToken.Register(
                        delegate { response.Dispose(); }))
                {
                    try
                    {
                        string requestId = GetRequestId(response);
                        string body = await ReadBoundedResponseAsync(
                            response.Content,
                            cancellationToken).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new AttemptException(
                                (int)response.StatusCode,
                                body,
                                requestId);
                        }

                        object parsed;
                        try
                        {
                            parsed = Deserialize(body);
                        }
                        catch (Exception exception)
                        {
                            throw new ModelCallException(
                                "模型接口返回了无法解析的 JSON：" +
                                exception.Message);
                        }

                        if (parsed == null)
                        {
                            throw new ModelCallException(
                                "模型接口返回了空响应。");
                        }

                        string error = ExtractErrorMessage(parsed);
                        if (!string.IsNullOrEmpty(error))
                        {
                            throw new ModelCallException(error);
                        }

                        return parsed;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(
                                cancellationToken);
                        }

                        throw;
                    }
                    catch (IOException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(
                                cancellationToken);
                        }

                        throw;
                    }
                    catch (HttpRequestException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(
                                cancellationToken);
                        }

                        throw;
                    }
                }
            }
        }

        private static async Task<string> ReadBoundedResponseAsync(
            HttpContent content,
            CancellationToken cancellationToken)
        {
            if (content == null)
            {
                return string.Empty;
            }

            long? contentLength = content.Headers.ContentLength;
            if (contentLength.HasValue &&
                contentLength.Value > MaximumResponseBytes)
            {
                throw new ModelCallException(
                    "模型接口响应超过 16 MB 安全限制。");
            }

            StringBuilder result = new StringBuilder();
            char[] buffer = new char[4096];
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
                    int read = await reader.ReadAsync(
                        buffer,
                        0,
                        buffer.Length).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (result.Length >
                        MaximumResponseCharacters - read)
                    {
                        throw new ModelCallException(
                            "模型接口响应超过 8 MB 字符安全限制。");
                    }

                    result.Append(buffer, 0, read);
                }
            }

            return result.ToString();
        }

        private async Task<string> ReadServerSentEventsAsync(
            Stream stream,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            StringBuilder result = new StringBuilder();
            StringBuilder eventData = new StringBuilder();
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
                    string line;
                    try
                    {
                        line = await reader.ReadLineAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        throw;
                    }

                    if (line == null)
                    {
                        ProcessEventData(eventData, result, onDelta);
                        break;
                    }

                    if (line.Length > MaximumResponseCharacters)
                    {
                        throw new ModelCallException(
                            "模型接口响应超过 8 MB 字符安全限制。");
                    }

                    if (line.Length == 0)
                    {
                        bool completed = ProcessEventData(eventData, result, onDelta);
                        if (completed)
                        {
                            break;
                        }

                        continue;
                    }

                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (eventData.Length > 0)
                        {
                            if (eventData.Length >= MaximumResponseCharacters)
                            {
                                throw new ModelCallException(
                                    "模型接口响应超过 8 MB 字符安全限制。");
                            }

                            eventData.Append('\n');
                        }

                        string eventLine = line.Substring(5).TrimStart();
                        if (eventData.Length >
                            MaximumResponseCharacters - eventLine.Length)
                        {
                            throw new ModelCallException(
                                "模型接口响应超过 8 MB 字符安全限制。");
                        }

                        eventData.Append(eventLine);
                    }
                }
            }

            if (result.Length == 0)
            {
                throw new ModelCallException(
                    "接口建立了流式连接，但没有返回文本内容。");
            }

            return result.ToString();
        }

        private bool ProcessEventData(
            StringBuilder eventData,
            StringBuilder result,
            Action<string> onDelta)
        {
            if (eventData.Length == 0)
            {
                return false;
            }

            string data = eventData.ToString();
            eventData.Clear();
            if (string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            object parsed;
            try
            {
                parsed = Deserialize(data);
            }
            catch
            {
                string keepalive = data.Trim();
                if (string.Equals(
                    keepalive,
                    "ping",
                    StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        keepalive,
                        "keepalive",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        keepalive,
                        "[KEEPALIVE]",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                throw new ModelCallException(
                    "流式响应包含无法解析的数据，请检查接口兼容性。");
            }

            string error = ExtractErrorMessage(parsed);
            if (!string.IsNullOrEmpty(error))
            {
                throw new ModelCallException(error);
            }

            string delta = ExtractDelta(parsed);
            if (!string.IsNullOrEmpty(delta))
            {
                EnsureResponseCapacity(result.Length, delta.Length);
                result.Append(delta);
                Notify(onDelta, delta);
            }
            else if (result.Length == 0)
            {
                string fullText = ExtractText(parsed);
                if (!string.IsNullOrEmpty(fullText))
                {
                    EnsureResponseCapacity(result.Length, fullText.Length);
                    result.Append(fullText);
                    Notify(onDelta, fullText);
                }
            }

            return false;
        }

        private static void EnsureResponseCapacity(
            int currentLength,
            int additionalLength)
        {
            if (additionalLength < 0 ||
                currentLength > MaximumResponseCharacters - additionalLength)
            {
                throw new ModelCallException(
                    "模型接口响应超过 8 MB 字符安全限制。");
            }
        }

        private string BuildPayload(ModelRequest request, bool stream)
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["model"] = request.ModelName;
            root["stream"] = stream;
            root["messages"] = BuildInitialMessages(request, true).ToArray();
            return json.Serialize(root);
        }

        private List<object> BuildInitialMessages(
            ModelRequest request,
            bool includeSystemPrompt)
        {
            List<object> messages = new List<object>();
            if (includeSystemPrompt &&
                !string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                messages.Add(new Dictionary<string, object>
                {
                    { "role", "system" },
                    { "content", request.SystemPrompt.Trim() }
                });
            }

            IList<ConversationMessage> history =
                request.ConversationMessages;
            if (history != null)
            {
                foreach (ConversationMessage historyMessage in history)
                {
                    if (historyMessage == null)
                    {
                        continue;
                    }

                    messages.Add(new Dictionary<string, object>
                    {
                        {
                            "role",
                            ConversationMessage.NormalizeRole(
                                historyMessage.Role)
                        },
                        {
                            "content",
                            historyMessage.Content ?? string.Empty
                        }
                    });
                }
            }

            IList<InputItem> attachments = request.Attachments ??
                new List<InputItem>();
            object content;
            if (attachments.Count == 0)
            {
                content = request.Prompt;
            }
            else
            {
                List<object> parts = new List<object>();
                parts.Add(new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", request.Prompt }
                });
                AddChatAttachments(parts, attachments);
                content = parts.ToArray();
            }

            messages.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", content }
            });
            return messages;
        }

        private string BuildToolPayload(
            ModelRequest request,
            IList<object> messages,
            object[] tools)
        {
            Dictionary<string, object> root =
                new Dictionary<string, object>();
            root["model"] = request.ModelName;
            root["stream"] = false;
            root["messages"] = messages.ToArray();
            root["tools"] = tools;
            root["tool_choice"] = "auto";
            string payload = json.Serialize(root);
            if (payload.Length > MaximumRequestCharacters)
            {
                throw new ModelCallException(
                    "包含工具和会话历史的请求超过 32 MB 安全限制，" +
                    "请新建会话或减少附件。");
            }

            return payload;
        }

        private ToolSet BuildToolSet(IList<McpToolDefinition> tools)
        {
            if (tools.Count > MaximumTools)
            {
                throw new ModelCallException(
                    "一次最多可向模型提供 128 个工具。");
            }

            List<object> payload = new List<object>();
            Dictionary<string, McpToolDefinition> names =
                new Dictionary<string, McpToolDefinition>(
                    StringComparer.OrdinalIgnoreCase);
            int combinedCharacters = 0;
            foreach (McpToolDefinition tool in tools)
            {
                if (tool == null)
                {
                    throw new ModelCallException("工具定义不能为空。");
                }

                string name = tool.PublicName ?? string.Empty;
                if (!IsValidToolName(name))
                {
                    throw new ModelCallException(
                        "工具名称必须为 1 到 64 位字母、数字、下划线或连字符。");
                }

                if (names.ContainsKey(name))
                {
                    throw new ModelCallException(
                        "工具名称重复：“" + name + "”。");
                }

                string description = (tool.Description ?? string.Empty).Trim();
                if (description.Length > MaximumToolDescriptionCharacters)
                {
                    throw new ModelCallException(
                        "工具“" + name + "”的描述超过 2,000 字符。");
                }

                object schema = tool.InputSchema ?? CreateEmptyToolSchema();
                if (AsDictionary(schema) == null)
                {
                    throw new ModelCallException(
                        "工具“" + name + "”的输入结构必须是 JSON 对象。");
                }

                string serializedSchema;
                try
                {
                    serializedSchema = json.Serialize(schema);
                }
                catch (Exception exception)
                {
                    throw new ModelCallException(
                        "工具“" + name + "”的输入结构无法序列化：" +
                        exception.Message);
                }

                if (serializedSchema.Length > MaximumToolSchemaCharacters)
                {
                    throw new ModelCallException(
                        "工具“" + name + "”的输入结构超过 200,000 字符。");
                }

                object safeSchema;
                try
                {
                    safeSchema = Deserialize(serializedSchema);
                }
                catch (Exception exception)
                {
                    throw new ModelCallException(
                        "工具“" + name + "”的输入结构无法读取：" +
                        exception.Message);
                }

                combinedCharacters += name.Length + description.Length +
                    serializedSchema.Length;
                if (combinedCharacters >
                    MaximumCombinedToolDefinitionCharacters)
                {
                    throw new ModelCallException(
                        "全部工具定义合计超过 4 MB 安全限制。");
                }

                names.Add(name, tool);
                payload.Add(new Dictionary<string, object>
                {
                    { "type", "function" },
                    {
                        "function",
                        new Dictionary<string, object>
                        {
                            { "name", name },
                            { "description", description },
                            { "parameters", safeSchema }
                        }
                    }
                });
            }

            return new ToolSet
            {
                Payload = payload.ToArray(),
                Names = names
            };
        }

        private static IDictionary<string, object> CreateEmptyToolSchema()
        {
            return new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", new Dictionary<string, object>() }
            };
        }

        private static bool IsValidToolName(string name)
        {
            if (string.IsNullOrEmpty(name) ||
                name.Length > MaximumToolNameCharacters)
            {
                return false;
            }

            foreach (char character in name)
            {
                bool valid = (character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' || character == '-';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        private ToolRoundResponse ParseToolRoundResponse(
            object parsed,
            IDictionary<string, McpToolDefinition> availableTools,
            ISet<string> seenToolCallIds)
        {
            IDictionary<string, object> root = AsDictionary(parsed);
            IList choices = AsList(GetValue(root, "choices"));
            if (root == null || choices == null || choices.Count == 0)
            {
                throw new ModelCallException(
                    "模型工具响应中没有找到 choices。");
            }

            IDictionary<string, object> choice = AsDictionary(choices[0]);
            IDictionary<string, object> message =
                AsDictionary(GetValue(choice, "message"));
            if (choice == null || message == null)
            {
                throw new ModelCallException(
                    "模型工具响应中没有找到 assistant message。");
            }

            ToolRoundResponse response = new ToolRoundResponse();
            response.Text = ExtractContent(GetValue(message, "content"));
            if (string.IsNullOrEmpty(response.Text))
            {
                response.Text = GetString(message, "refusal");
            }

            object toolCallsValue = GetValue(message, "tool_calls");
            if (toolCallsValue == null)
            {
                return response;
            }

            IList toolCalls = AsList(toolCallsValue);
            if (toolCalls == null)
            {
                throw new ModelCallException(
                    "模型返回的 tool_calls 必须是数组。");
            }

            if (toolCalls.Count == 0)
            {
                return response;
            }

            if (toolCalls.Count > MaximumToolCallsPerRound)
            {
                throw new ModelCallException(
                    "模型单轮请求的工具数量超过 32 个。");
            }

            List<object> normalizedCalls = new List<object>();
            foreach (object value in toolCalls)
            {
                IDictionary<string, object> call = AsDictionary(value);
                if (call == null)
                {
                    throw new ModelCallException(
                        "模型返回了无效的工具调用对象。");
                }

                string type = GetString(call, "type");
                if (!string.IsNullOrEmpty(type) &&
                    !string.Equals(
                        type,
                        "function",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ModelCallException(
                        "当前仅支持 function 类型的模型工具调用。");
                }

                string id = GetString(call, "id");
                if (string.IsNullOrWhiteSpace(id) ||
                    id.Length > MaximumToolCallIdCharacters)
                {
                    throw new ModelCallException(
                        "模型返回了缺失或过长的工具调用 ID。");
                }

                if (!seenToolCallIds.Add(id))
                {
                    throw new ModelCallException(
                        "模型重复使用了工具调用 ID：“" + id + "”。");
                }

                IDictionary<string, object> function =
                    AsDictionary(GetValue(call, "function"));
                string name = GetString(function, "name");
                if (!IsValidToolName(name) ||
                    !availableTools.ContainsKey(name))
                {
                    throw new ModelCallException(
                        "模型请求了未授权工具：“" +
                        (string.IsNullOrWhiteSpace(name) ? "未知" : name) +
                        "”。");
                }

                string argumentsJson = NormalizeToolArguments(function, name);
                ModelToolCall parsedCall = new ModelToolCall
                {
                    Id = id,
                    Name = name,
                    ArgumentsJson = argumentsJson
                };
                response.ToolCalls.Add(parsedCall);
                normalizedCalls.Add(new Dictionary<string, object>
                {
                    { "id", id },
                    { "type", "function" },
                    {
                        "function",
                        new Dictionary<string, object>
                        {
                            { "name", name },
                            { "arguments", argumentsJson }
                        }
                    }
                });
            }

            response.AssistantMessage = new Dictionary<string, object>
            {
                { "role", "assistant" },
                {
                    "content",
                    string.IsNullOrEmpty(response.Text)
                        ? null
                        : response.Text
                },
                { "tool_calls", normalizedCalls.ToArray() }
            };
            return response;
        }

        private string NormalizeToolArguments(
            IDictionary<string, object> function,
            string toolName)
        {
            if (function == null)
            {
                throw new ModelCallException(
                    "工具“" + toolName + "”缺少 function 对象。");
            }

            object argumentsValue = GetValue(function, "arguments");
            string argumentsJson = argumentsValue as string;
            if (argumentsJson == null)
            {
                if (argumentsValue == null)
                {
                    argumentsJson = "{}";
                }
                else
                {
                    try
                    {
                        argumentsJson = json.Serialize(argumentsValue);
                    }
                    catch (Exception exception)
                    {
                        throw new ModelCallException(
                            "工具“" + toolName + "”的参数无法序列化：" +
                            exception.Message);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                argumentsJson = "{}";
            }

            if (argumentsJson.Length > MaximumToolArgumentsCharacters)
            {
                throw new ModelCallException(
                    "工具“" + toolName + "”的参数超过 1 MB 安全限制。");
            }

            object parsedArguments;
            try
            {
                parsedArguments = Deserialize(argumentsJson);
            }
            catch (Exception exception)
            {
                throw new ModelCallException(
                    "模型为工具“" + toolName + "”生成了无效 JSON 参数：" +
                    exception.Message);
            }

            if (AsDictionary(parsedArguments) == null)
            {
                throw new ModelCallException(
                    "工具“" + toolName + "”的参数必须是 JSON 对象。");
            }

            return argumentsJson;
        }

        private static async Task<McpToolResult> ExecuteToolAsync(
            Func<ModelToolCall, CancellationToken, Task<McpToolResult>>
                executeTool,
            ModelToolCall toolCall,
            CancellationToken cancellationToken)
        {
            try
            {
                Task<McpToolResult> task = executeTool(
                    toolCall,
                    cancellationToken);
                if (task == null)
                {
                    throw new InvalidOperationException(
                        "工具执行回调没有返回任务。");
                }

                McpToolResult result = await task.ConfigureAwait(false);
                if (result == null)
                {
                    throw new InvalidOperationException(
                        "工具执行回调没有返回结果。");
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new ModelCallException(
                    "工具“" + toolCall.Name + "”执行超时或被取消。");
            }
            catch (ModelCallException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ModelCallException(
                    "工具“" + toolCall.Name + "”执行失败：" +
                    exception.Message);
            }
        }

        private static string FormatToolResult(
            McpToolResult result,
            int maximumCharacters)
        {
            string content = result.Content ?? string.Empty;
            if (result.IsError)
            {
                content = string.IsNullOrWhiteSpace(content)
                    ? "[工具执行失败，未返回错误详情]"
                    : "[工具执行失败]\r\n" + content;
            }
            else if (string.IsNullOrEmpty(content))
            {
                content = "[工具执行成功，未返回文本内容]";
            }

            return TruncateWithNotice(
                content,
                maximumCharacters,
                "\r\n[工具结果过长，已截断]");
        }

        private static string TruncateWithNotice(
            string value,
            int maximumCharacters,
            string notice)
        {
            value = value ?? string.Empty;
            if (maximumCharacters <= 0)
            {
                return string.Empty;
            }

            if (value.Length <= maximumCharacters)
            {
                return value;
            }

            notice = notice ?? string.Empty;
            if (notice.Length >= maximumCharacters)
            {
                return notice.Substring(0, maximumCharacters);
            }

            int cutoff = maximumCharacters - notice.Length;
            if (cutoff > 0 && cutoff < value.Length &&
                char.IsHighSurrogate(value[cutoff - 1]) &&
                char.IsLowSurrogate(value[cutoff]))
            {
                cutoff--;
            }

            return value.Substring(0, cutoff) + notice;
        }

        private static void AddChatAttachments(
            IList<object> content,
            IEnumerable<InputItem> attachments)
        {
            foreach (InputItem item in attachments)
            {
                if (item == null || item.BinaryData == null)
                {
                    continue;
                }

                string dataUri = CreateDataUri(item);
                if (item.Kind == InputKind.Image)
                {
                    content.Add(new Dictionary<string, object>
                    {
                        { "type", "image_url" },
                        {
                            "image_url",
                            new Dictionary<string, object>
                            {
                                { "url", dataUri }
                            }
                        }
                    });
                }
                else if (item.Kind == InputKind.File)
                {
                    content.Add(new Dictionary<string, object>
                    {
                        { "type", "file" },
                        {
                            "file",
                            new Dictionary<string, object>
                            {
                                { "filename", item.Name },
                                { "file_data", dataUri }
                            }
                        }
                    });
                }
            }
        }

        private static string CreateDataUri(InputItem item)
        {
            string mime = string.IsNullOrWhiteSpace(item.MimeType)
                ? "application/octet-stream"
                : item.MimeType;
            return "data:" + mime + ";base64," +
                Convert.ToBase64String(item.BinaryData);
        }

        private object Deserialize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return json.DeserializeObject(value);
        }

        private static string ExtractDelta(object parsed)
        {
            IDictionary<string, object> root = AsDictionary(parsed);
            if (root == null)
            {
                return string.Empty;
            }

            string type = GetString(root, "type");
            if (!string.IsNullOrEmpty(type) &&
                type.IndexOf("output_text.delta", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GetString(root, "delta");
            }

            IList choices = AsList(GetValue(root, "choices"));
            if (choices != null && choices.Count > 0)
            {
                IDictionary<string, object> choice = AsDictionary(choices[0]);
                IDictionary<string, object> delta =
                    AsDictionary(GetValue(choice, "delta"));
                string content = ExtractContent(GetValue(delta, "content"));
                if (!string.IsNullOrEmpty(content))
                {
                    return content;
                }

                string choiceText = GetString(choice, "text");
                if (!string.IsNullOrEmpty(choiceText))
                {
                    return choiceText;
                }
            }

            IDictionary<string, object> topDelta =
                AsDictionary(GetValue(root, "delta"));
            return ExtractContent(GetValue(topDelta, "content"));
        }

        private static string ExtractText(object parsed)
        {
            IDictionary<string, object> root = AsDictionary(parsed);
            if (root == null)
            {
                return parsed as string ?? string.Empty;
            }

            string outputText = GetString(root, "output_text");
            if (!string.IsNullOrEmpty(outputText))
            {
                return outputText;
            }

            IList choices = AsList(GetValue(root, "choices"));
            if (choices != null && choices.Count > 0)
            {
                IDictionary<string, object> choice = AsDictionary(choices[0]);
                IDictionary<string, object> message =
                    AsDictionary(GetValue(choice, "message"));
                string content = ExtractContent(GetValue(message, "content"));
                if (!string.IsNullOrEmpty(content))
                {
                    return content;
                }

                content = ExtractContent(GetValue(choice, "text"));
                if (!string.IsNullOrEmpty(content))
                {
                    return content;
                }
            }

            IDictionary<string, object> messageRoot =
                AsDictionary(GetValue(root, "message"));
            string messageContent = ExtractContent(GetValue(messageRoot, "content"));
            if (!string.IsNullOrEmpty(messageContent))
            {
                return messageContent;
            }

            StringBuilder output = new StringBuilder();
            AppendOutputArray(output, AsList(GetValue(root, "output")));
            if (output.Length > 0)
            {
                return output.ToString();
            }

            IDictionary<string, object> response =
                AsDictionary(GetValue(root, "response"));
            AppendOutputArray(output, AsList(GetValue(response, "output")));
            if (output.Length > 0)
            {
                return output.ToString();
            }

            return ExtractContent(GetValue(root, "content"));
        }

        private static void AppendOutputArray(StringBuilder output, IList items)
        {
            if (items == null)
            {
                return;
            }

            foreach (object item in items)
            {
                IDictionary<string, object> itemDictionary = AsDictionary(item);
                object contentValue = GetValue(itemDictionary, "content");
                string content = ExtractContent(contentValue);
                if (string.IsNullOrEmpty(content))
                {
                    content = GetString(itemDictionary, "text");
                }

                if (!string.IsNullOrEmpty(content))
                {
                    output.Append(content);
                }
            }
        }

        private static string ExtractContent(object content)
        {
            if (content == null)
            {
                return string.Empty;
            }

            string stringContent = content as string;
            if (stringContent != null)
            {
                return stringContent;
            }

            IDictionary<string, object> dictionary = AsDictionary(content);
            if (dictionary != null)
            {
                string text = GetString(dictionary, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }

                return GetString(dictionary, "content");
            }

            IList list = AsList(content);
            if (list == null)
            {
                return string.Empty;
            }

            StringBuilder result = new StringBuilder();
            foreach (object item in list)
            {
                result.Append(ExtractContent(item));
            }

            return result.ToString();
        }

        private static string ExtractErrorMessage(object parsed)
        {
            IDictionary<string, object> root = AsDictionary(parsed);
            if (root == null)
            {
                return string.Empty;
            }

            IDictionary<string, object> error = AsDictionary(GetValue(root, "error"));
            string message = GetString(error, "message");
            if (!string.IsNullOrEmpty(message))
            {
                return message;
            }

            string type = GetString(root, "type");
            if (!string.IsNullOrEmpty(type) &&
                type.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = GetString(root, "message");
                if (!string.IsNullOrEmpty(message))
                {
                    return message;
                }
            }

            return string.Empty;
        }

        private static IDictionary<string, object> AsDictionary(object value)
        {
            return value as IDictionary<string, object>;
        }

        private static IList AsList(object value)
        {
            return value as IList;
        }

        private static object GetValue(
            IDictionary<string, object> dictionary,
            string key)
        {
            if (dictionary == null)
            {
                return null;
            }

            object value;
            return dictionary.TryGetValue(key, out value) ? value : null;
        }

        private static string GetString(
            IDictionary<string, object> dictionary,
            string key)
        {
            object value = GetValue(dictionary, key);
            return value == null
                ? string.Empty
                : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static EndpointAttempt BuildExactAttempt(string endpoint)
        {
            Uri uri;
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ModelCallException("URL 必须是完整的 http:// 或 https:// 地址。");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ModelCallException("URL 中不能包含用户名或密码。");
            }

            return new EndpointAttempt
            {
                Url = uri
            };
        }

        private static bool IsStreamUnsupported(AttemptException exception)
        {
            if (exception.StatusCode != 400 && exception.StatusCode != 422)
            {
                return false;
            }

            string body = exception.Body ?? string.Empty;
            return ContainsAll(body, "stream", "not support") ||
                ContainsAll(body, "stream", "unsupported") ||
                ContainsAll(body, "stream", "不支持");
        }

        private static bool ContainsAll(string value, string first, string second)
        {
            return value.IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0 &&
                value.IndexOf(second, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private ModelCallException CreateUserFacingException(AttemptException exception)
        {
            string serverMessage = string.Empty;
            try
            {
                serverMessage = ExtractErrorMessage(Deserialize(exception.Body));
            }
            catch
            {
                // Use the HTTP summary below.
            }

            string prefix;
            switch (exception.StatusCode)
            {
                case 400:
                case 422:
                    prefix = "请求格式或模型参数不匹配。";
                    break;
                case 401:
                    prefix = "API Key 无效，或接口认证方式不匹配。";
                    break;
                case 403:
                    prefix = "当前 Key 没有访问该接口或模型的权限。";
                    break;
                case 404:
                    prefix = "URL 或模型名称不正确。";
                    break;
                case 413:
                    prefix = "提交内容过大，请减少文件或缩小图片。";
                    break;
                case 429:
                    prefix = "请求过于频繁，或账户额度不足。";
                    break;
                default:
                    if (exception.StatusCode >= 300 &&
                        exception.StatusCode < 400)
                    {
                        prefix = "接口返回了重定向，请填写最终的完整请求 URL。";
                    }
                    else
                    {
                        prefix = exception.StatusCode >= 500
                            ? "模型服务暂时异常。"
                            : "模型接口返回错误。";
                    }
                    break;
            }

            string message = prefix;
            if (!string.IsNullOrWhiteSpace(serverMessage))
            {
                message += "\r\n\r\n服务端信息：" + serverMessage;
            }

            if (!string.IsNullOrWhiteSpace(exception.RequestId))
            {
                message += "\r\n请求 ID：" + exception.RequestId;
            }

            return new ModelCallException(
                message,
                exception.StatusCode,
                exception.RequestId);
        }

        private static ModelCallException CreateNetworkException(HttpRequestException exception)
        {
            StringBuilder details = new StringBuilder();
            Exception current = exception;
            while (current != null)
            {
                if (!string.IsNullOrWhiteSpace(current.Message))
                {
                    if (details.Length > 0)
                    {
                        details.Append(" ");
                    }

                    details.Append(current.Message);
                }

                current = current.InnerException;
            }

            string message = details.ToString();
            if (message.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("secure channel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("安全通道", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new ModelCallException(
                    "无法建立 HTTPS 安全连接。请确认 Windows 7 已启用 TLS 1.2、" +
                    "系统根证书已更新，并检查系统日期。");
            }

            return new ModelCallException("无法连接模型接口：" + message);
        }

        private static string GetRequestId(HttpResponseMessage response)
        {
            string[] names = { "x-request-id", "request-id", "cf-ray" };
            foreach (string name in names)
            {
                IEnumerable<string> values;
                if (response.Headers.TryGetValues(name, out values))
                {
                    return values.FirstOrDefault() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static void ValidateRequest(ModelRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (string.IsNullOrWhiteSpace(request.EndpointUrl))
            {
                throw new ModelCallException("请填写 URL。");
            }

            if (string.IsNullOrWhiteSpace(request.ModelName))
            {
                throw new ModelCallException("请填写模型名称。");
            }

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                throw new ModelCallException("没有可提交的文字内容。");
            }
        }

        private static void Notify(Action<string> callback, string value)
        {
            if (callback != null && !string.IsNullOrEmpty(value))
            {
                callback(value);
            }
        }

        public void Dispose()
        {
            client.Dispose();
        }

        private sealed class ToolSet
        {
            public object[] Payload { get; set; }
            public IDictionary<string, McpToolDefinition> Names { get; set; }
        }

        private sealed class ToolRoundResponse
        {
            public string Text { get; set; }
            public IList<ModelToolCall> ToolCalls { get; private set; }
            public object AssistantMessage { get; set; }

            public ToolRoundResponse()
            {
                Text = string.Empty;
                ToolCalls = new List<ModelToolCall>();
            }
        }

        private sealed class EndpointAttempt
        {
            public Uri Url { get; set; }
        }

        private sealed class AttemptException : Exception
        {
            public int StatusCode { get; private set; }
            public string Body { get; private set; }
            public string RequestId { get; private set; }

            public AttemptException(int statusCode, string body, string requestId)
                : base("HTTP " + statusCode.ToString(CultureInfo.InvariantCulture))
            {
                StatusCode = statusCode;
                Body = body;
                RequestId = requestId;
            }
        }
    }
}
