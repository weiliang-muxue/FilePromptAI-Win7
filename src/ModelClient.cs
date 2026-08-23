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
        private const long MaximumRequestBytes = 32L * 1024L * 1024L;
        private const long MinimumRequestEnvelopeBytes = 256L * 1024L;
        private const int MaximumDisplayedServerErrorCharacters = 2000;
        private const int MaximumListedModels = 4096;
        private const int MaximumModelIdentifierCharacters = 512;
        private const int MaximumModelListCharacters = 1024 * 1024;
        private const long MaximumModelListBytes = 2L * 1024L * 1024L;
        private const int MaximumResponseCharacters = 8 * 1024 * 1024;
        private const long MaximumResponseBytes = 16L * 1024L * 1024L;
        private const int DefaultResponseHeadersTimeoutMilliseconds = 30000;
        private const int DefaultAttachmentResponseHeadersTimeoutMilliseconds =
            120000;
        private const int DefaultResponseReadIdleTimeoutMilliseconds = 60000;
        private const int DefaultMaximumRequestAttempts = 3;
        private const int DefaultRetryBaseDelayMilliseconds = 500;
        private const int DefaultMaximumRetryAfterMilliseconds = 30000;

        private readonly HttpClient client;
        private readonly JavaScriptSerializer json;
        private readonly TimeSpan responseHeadersTimeout;
        private readonly TimeSpan attachmentResponseHeadersTimeout;
        private readonly TimeSpan responseReadIdleTimeout;
        private readonly int maximumRequestAttempts;
        private readonly TimeSpan retryBaseDelay;
        private readonly TimeSpan maximumRetryAfter;

        public ModelClient()
            : this(
                DefaultResponseHeadersTimeoutMilliseconds,
                DefaultResponseReadIdleTimeoutMilliseconds,
                DefaultMaximumRequestAttempts,
                DefaultRetryBaseDelayMilliseconds,
                DefaultMaximumRetryAfterMilliseconds,
                DefaultAttachmentResponseHeadersTimeoutMilliseconds)
        {
        }

        internal ModelClient(
            int responseHeadersTimeoutMilliseconds,
            int responseReadIdleTimeoutMilliseconds,
            int maximumRequestAttempts,
            int retryBaseDelayMilliseconds,
            int maximumRetryAfterMilliseconds)
            : this(
                responseHeadersTimeoutMilliseconds,
                responseReadIdleTimeoutMilliseconds,
                maximumRequestAttempts,
                retryBaseDelayMilliseconds,
                maximumRetryAfterMilliseconds,
                Math.Max(
                    responseHeadersTimeoutMilliseconds,
                    DefaultAttachmentResponseHeadersTimeoutMilliseconds))
        {
        }

        internal ModelClient(
            int responseHeadersTimeoutMilliseconds,
            int responseReadIdleTimeoutMilliseconds,
            int maximumRequestAttempts,
            int retryBaseDelayMilliseconds,
            int maximumRetryAfterMilliseconds,
            int attachmentResponseHeadersTimeoutMilliseconds)
        {
            if (responseHeadersTimeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "responseHeadersTimeoutMilliseconds");
            }

            if (responseReadIdleTimeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "responseReadIdleTimeoutMilliseconds");
            }

            if (maximumRequestAttempts <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "maximumRequestAttempts");
            }

            if (retryBaseDelayMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    "retryBaseDelayMilliseconds");
            }

            if (maximumRetryAfterMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "maximumRetryAfterMilliseconds");
            }

            if (attachmentResponseHeadersTimeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "attachmentResponseHeadersTimeoutMilliseconds");
            }

            HttpClientHandler handler = new HttpClientHandler();
            handler.AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate;
            handler.AllowAutoRedirect = false;
            handler.UseProxy = false;
            handler.Proxy = null;

            client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMilliseconds(Timeout.Infinite);
            responseHeadersTimeout = TimeSpan.FromMilliseconds(
                responseHeadersTimeoutMilliseconds);
            attachmentResponseHeadersTimeout = TimeSpan.FromMilliseconds(
                attachmentResponseHeadersTimeoutMilliseconds);
            responseReadIdleTimeout = TimeSpan.FromMilliseconds(
                responseReadIdleTimeoutMilliseconds);
            this.maximumRequestAttempts = maximumRequestAttempts;
            retryBaseDelay = TimeSpan.FromMilliseconds(
                retryBaseDelayMilliseconds);
            maximumRetryAfter = TimeSpan.FromMilliseconds(
                maximumRetryAfterMilliseconds);
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

                throw CreateNetworkException(exception, request);
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

            if (HasBinaryAttachments(request) ||
                !IsStreamUnsupported(firstError))
            {
                throw CreateUserFacingException(firstError, request);
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

                throw CreateNetworkException(exception, request);
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

            throw CreateUserFacingException(secondError, request);
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
            ValidateBinaryAttachmentBudget(
                request,
                toolSet.EstimatedBytes);
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

                    if (toolRounds == 0 && HasBinaryAttachments(request))
                    {
                        ReplaceBinaryAttachmentsForToolFollowUp(
                            messages,
                            request);
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
                throw CreateUserFacingException(exception, request);
            }
            catch (HttpRequestException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw CreateNetworkException(exception, request);
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
                throw CreateUserFacingException(exception, request);
            }
            catch (HttpRequestException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw CreateNetworkException(exception, request);
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

        public async Task<IList<string>> FetchModelsAsync(
            string endpointUrl,
            string apiKey,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                throw new ModelCallException("请填写 URL。");
            }

            Uri modelsUrl = BuildModelsEndpoint(
                BuildExactAttempt(endpointUrl).Url);
            ModelRequest request = new ModelRequest
            {
                EndpointUrl = modelsUrl.AbsoluteUri,
                ApiKey = apiKey ?? string.Empty,
                Attachments = new List<InputItem>()
            };

            try
            {
                using (HttpRequestMessage message = new HttpRequestMessage(
                    HttpMethod.Get,
                    modelsUrl))
                {
                    string key = (apiKey ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(key))
                    {
                        message.Headers.Authorization =
                            new AuthenticationHeaderValue("Bearer", key);
                    }

                    message.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue(
                            "application/json"));
                    HttpResponseMessage response = await SendForHeadersAsync(
                        message,
                        responseHeadersTimeout,
                        cancellationToken).ConfigureAwait(false);
                    using (response)
                    using (CancellationTokenRegistration registration =
                        cancellationToken.Register(
                            delegate { response.Dispose(); }))
                    {
                        string body = await ReadBoundedModelListResponseAsync(
                            response.Content,
                            cancellationToken).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw CreateModelListException(
                                (int)response.StatusCode,
                                body,
                                GetRequestId(response));
                        }

                        object parsed;
                        try
                        {
                            parsed = Deserialize(body);
                        }
                        catch (Exception exception)
                        {
                            throw new ModelCallException(
                                "模型列表接口返回了无法解析的 JSON：" +
                                exception.Message);
                        }

                        IList<string> models = ParseModelIdentifiers(parsed);
                        if (models.Count == 0)
                        {
                            throw new ModelCallException(
                                "模型列表接口请求成功，但响应中没有可用的模型 ID。" +
                                "仍可在模型名称框中手动输入。");
                        }

                        return models;
                    }
                }
            }
            catch (ModelCallException)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw CreateNetworkException(exception, request);
            }
            catch (IOException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new ModelCallException(
                    "读取模型列表时连接中断：" + exception.Message);
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
            EnsureSerializedRequestSize(payload, HasBinaryAttachments(request));

            bool emittedDelta = false;
            bool hasBinaryAttachments = HasBinaryAttachments(request);
            Action<string> trackedDelta = delegate(string value)
            {
                emittedDelta = true;
                Notify(onDelta, value);
            };

            // A retry is safe only while no visible stream delta has escaped.
            for (int attemptNumber = 1;
                attemptNumber <= maximumRequestAttempts;
                attemptNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan? retryAfter = null;
                try
                {
                    return await SendSingleAttemptAsync(
                        attempt,
                        request,
                        payload,
                        stream,
                        trackedDelta,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AttemptException exception)
                {
                    if (hasBinaryAttachments ||
                        emittedDelta ||
                        attemptNumber >= maximumRequestAttempts ||
                        !CanRetry(exception))
                    {
                        throw;
                    }

                    retryAfter = exception.RetryAfter;
                }
                catch (ConnectionAttemptException)
                {
                    if (hasBinaryAttachments ||
                        emittedDelta ||
                        attemptNumber >= maximumRequestAttempts)
                    {
                        throw;
                    }
                }

                await DelayBeforeRetryAsync(
                    attemptNumber,
                    retryAfter,
                    cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("模型请求重试状态无效。");
        }

        private async Task<string> SendSingleAttemptAsync(
            EndpointAttempt attempt,
            ModelRequest request,
            string payload,
            bool stream,
            Action<string> onDelta,
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

                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                    stream ? "text/event-stream" : "application/json"));
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                    "application/json",
                    0.9));
                message.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await SendForHeadersAsync(
                    message,
                    GetResponseHeadersTimeout(request),
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
                                requestId,
                                GetRetryAfter(response));
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
            bool hasBinaryAttachments = HasBinaryAttachments(request);
            for (int attemptNumber = 1;
                attemptNumber <= maximumRequestAttempts;
                attemptNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan? retryAfter = null;
                try
                {
                    return await SendSingleToolAttemptAsync(
                        attempt,
                        request,
                        payload,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AttemptException exception)
                {
                    if (hasBinaryAttachments ||
                        attemptNumber >= maximumRequestAttempts ||
                        !CanRetry(exception))
                    {
                        throw;
                    }

                    retryAfter = exception.RetryAfter;
                }
                catch (ConnectionAttemptException)
                {
                    if (hasBinaryAttachments ||
                        attemptNumber >= maximumRequestAttempts)
                    {
                        throw;
                    }
                }

                await DelayBeforeRetryAsync(
                    attemptNumber,
                    retryAfter,
                    cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("工具请求重试状态无效。");
        }

        private async Task<object> SendSingleToolAttemptAsync(
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

                HttpResponseMessage response = await SendForHeadersAsync(
                    message,
                    GetResponseHeadersTimeout(request),
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
                                requestId,
                                GetRetryAfter(response));
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

        private async Task<HttpResponseMessage> SendForHeadersAsync(
            HttpRequestMessage message,
            TimeSpan headersTimeout,
            CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken))
            {
                timeoutCancellation.CancelAfter(headersTimeout);
                try
                {
                    return await client.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw new ConnectionAttemptException(
                        "连接或等待响应头超时（" +
                        FormatSeconds(headersTimeout) + " 秒）。",
                        exception);
                }
                catch (HttpRequestException exception)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw new ConnectionAttemptException(
                        exception.Message,
                        exception);
                }
                catch (IOException exception)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw new ConnectionAttemptException(
                        exception.Message,
                        exception);
                }
            }
        }

        private bool CanRetry(AttemptException exception)
        {
            if (exception.StatusCode != 429 &&
                exception.StatusCode != 502 &&
                exception.StatusCode != 503 &&
                exception.StatusCode != 504)
            {
                return false;
            }

            return !exception.RetryAfter.HasValue ||
                exception.RetryAfter.Value <= maximumRetryAfter;
        }

        private async Task DelayBeforeRetryAsync(
            int completedAttemptNumber,
            TimeSpan? retryAfter,
            CancellationToken cancellationToken)
        {
            double multiplier = Math.Pow(
                2.0,
                Math.Min(completedAttemptNumber - 1, 10));
            double milliseconds = Math.Min(
                retryBaseDelay.TotalMilliseconds * multiplier,
                maximumRetryAfter.TotalMilliseconds);
            TimeSpan delay = TimeSpan.FromMilliseconds(milliseconds);
            if (retryAfter.HasValue && retryAfter.Value > delay)
            {
                delay = retryAfter.Value;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private async Task<T> AwaitReadWithIdleTimeoutAsync<T>(
            Task<T> readTask,
            CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken))
            {
                Task timeoutTask = Task.Delay(
                    responseReadIdleTimeout,
                    timeoutCancellation.Token);
                Task completed = await Task.WhenAny(
                    readTask,
                    timeoutTask).ConfigureAwait(false);
                if (completed == readTask || readTask.IsCompleted)
                {
                    timeoutCancellation.Cancel();
                    return await readTask.ConfigureAwait(false);
                }

                ObserveFault(readTask);
                cancellationToken.ThrowIfCancellationRequested();
                throw new ConnectionAttemptException(
                    "模型接口响应读取超时（连续 " +
                    FormatSeconds(responseReadIdleTimeout) +
                    " 秒未收到数据）。",
                    null);
            }
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(
                delegate(Task completed)
                {
                    AggregateException ignored = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static string FormatSeconds(TimeSpan value)
        {
            return value.TotalSeconds.ToString(
                "0.###",
                CultureInfo.InvariantCulture);
        }

        private async Task<string> ReadBoundedResponseAsync(
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
                    int read = await AwaitReadWithIdleTimeoutAsync(
                        reader.ReadAsync(buffer, 0, buffer.Length),
                        cancellationToken).ConfigureAwait(false);
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

        private async Task<string> ReadBoundedModelListResponseAsync(
            HttpContent content,
            CancellationToken cancellationToken)
        {
            if (content == null)
            {
                return string.Empty;
            }

            long? contentLength = content.Headers.ContentLength;
            if (contentLength.HasValue &&
                contentLength.Value > MaximumModelListBytes)
            {
                throw new ModelCallException(
                    "模型列表响应超过 2 MB 安全限制。");
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
                    int read = await AwaitReadWithIdleTimeoutAsync(
                        reader.ReadAsync(buffer, 0, buffer.Length),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (result.Length > MaximumModelListCharacters - read)
                    {
                        throw new ModelCallException(
                            "模型列表响应超过 1 MB 字符安全限制。");
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
            string eventName = string.Empty;
            bool streamCompleted = false;
            // Closing a socket is not a successful stream terminator.
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
                        line = await AwaitReadWithIdleTimeoutAsync(
                            reader.ReadLineAsync(),
                            cancellationToken).ConfigureAwait(false);
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
                        streamCompleted = ProcessEventData(
                            eventData,
                            eventName,
                            result,
                            onDelta);
                        break;
                    }

                    if (line.Length > MaximumResponseCharacters)
                    {
                        throw new ModelCallException(
                            "模型接口响应超过 8 MB 字符安全限制。");
                    }

                    if (line.Length == 0)
                    {
                        streamCompleted = ProcessEventData(
                            eventData,
                            eventName,
                            result,
                            onDelta);
                        eventName = string.Empty;
                        if (streamCompleted)
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
                    else if (line.StartsWith(
                        "event:",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        eventName = line.Substring(6).Trim();
                    }
                }
            }

            if (!streamCompleted)
            {
                throw new ConnectionAttemptException(
                    "流式响应未完整结束，请重试；已收到的部分内容不会保存为成功回复。",
                    null);
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
            string eventName,
            StringBuilder result,
            Action<string> onDelta)
        {
            if (eventData.Length == 0)
            {
                return IsCompletionName(eventName);
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

            return IsCompletionName(eventName) ||
                IsExplicitStreamCompletion(parsed);
        }

        private static bool IsExplicitStreamCompletion(object parsed)
        {
            IDictionary<string, object> root = AsDictionary(parsed);
            if (root == null)
            {
                return false;
            }

            object flag = GetValue(root, "done");
            if (IsTrue(flag))
            {
                return true;
            }

            flag = GetValue(root, "completed");
            if (IsTrue(flag))
            {
                return true;
            }

            if (IsCompletionName(GetString(root, "status")) ||
                IsCompletionName(GetString(root, "type")) ||
                IsCompletionName(GetString(root, "event")))
            {
                return true;
            }

            if (HasFinishReason(root))
            {
                return true;
            }

            IList choices = AsList(GetValue(root, "choices"));
            if (choices == null)
            {
                return false;
            }

            foreach (object choiceValue in choices)
            {
                if (HasFinishReason(AsDictionary(choiceValue)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasFinishReason(
            IDictionary<string, object> value)
        {
            if (value == null)
            {
                return false;
            }

            object reason = GetValue(value, "finish_reason");
            return reason != null &&
                !string.IsNullOrWhiteSpace(Convert.ToString(
                    reason,
                    CultureInfo.InvariantCulture));
        }

        private static bool IsTrue(object value)
        {
            return value is bool && (bool)value;
        }

        private static bool IsCompletionName(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            return string.Equals(
                    normalized,
                    "done",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    normalized,
                    "complete",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    normalized,
                    "completed",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    normalized,
                    "succeeded",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    normalized,
                    "response.completed",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    normalized,
                    "message_stop",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    normalized,
                    "message.completed",
                    StringComparison.OrdinalIgnoreCase);
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

        private TimeSpan GetResponseHeadersTimeout(ModelRequest request)
        {
            return HasBinaryAttachments(request)
                ? attachmentResponseHeadersTimeout
                : responseHeadersTimeout;
        }

        private static bool HasBinaryAttachments(ModelRequest request)
        {
            return request != null &&
                HasBinaryAttachments(request.Attachments);
        }

        private static bool HasBinaryAttachments(
            IEnumerable<InputItem> attachments)
        {
            if (attachments == null)
            {
                return false;
            }

            foreach (InputItem item in attachments)
            {
                if (item != null && item.BinaryData != null &&
                    item.BinaryData.Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ValidateBinaryAttachmentBudget(
            ModelRequest request,
            long additionalEstimatedBytes)
        {
            if (request == null || !HasBinaryAttachments(request))
            {
                return;
            }

            long estimatedBytes = AddSaturated(
                MinimumRequestEnvelopeBytes,
                Math.Max(0L, additionalEstimatedBytes));
            estimatedBytes = AddSaturated(
                estimatedBytes,
                EstimateRequestTextBytes(request));
            int attachmentCount = 0;
            foreach (InputItem item in request.Attachments ??
                new List<InputItem>())
            {
                if (item == null || item.BinaryData == null ||
                    item.BinaryData.Length == 0)
                {
                    continue;
                }

                attachmentCount++;
                long sourceBytes = item.BinaryData.LongLength;
                long base64Bytes = ((sourceBytes + 2L) / 3L) * 4L;
                long metadataBytes = 512L +
                    Encoding.UTF8.GetByteCount(item.Name ?? string.Empty) +
                    Encoding.UTF8.GetByteCount(item.MimeType ?? string.Empty);
                estimatedBytes = AddSaturated(
                    estimatedBytes,
                    AddSaturated(base64Bytes, metadataBytes));
            }

            if (estimatedBytes > MaximumRequestBytes)
            {
                throw new ModelCallException(
                    "附件编码后预计约 " +
                    FormatMegabytes(estimatedBytes) +
                    " MB，超过模型请求 32 MB 安全限制。" +
                    "请减少附件数量或缩小文件后重试。" +
                    (attachmentCount > 0
                        ? "（当前二进制附件 " +
                            attachmentCount.ToString(
                                CultureInfo.InvariantCulture) + " 个）"
                        : string.Empty));
            }
        }

        private static long EstimateRequestTextBytes(ModelRequest request)
        {
            long total = 0L;
            total = AddSaturated(
                total,
                Encoding.UTF8.GetByteCount(request.ModelName ?? string.Empty));
            total = AddSaturated(
                total,
                Encoding.UTF8.GetByteCount(request.SystemPrompt ?? string.Empty));
            total = AddSaturated(
                total,
                Encoding.UTF8.GetByteCount(request.Prompt ?? string.Empty));
            foreach (ConversationMessage message in request.ConversationMessages ??
                new List<ConversationMessage>())
            {
                if (message != null)
                {
                    total = AddSaturated(
                        total,
                        Encoding.UTF8.GetByteCount(
                            message.Content ?? string.Empty));
                }
            }

            return total;
        }

        private static void EnsureSerializedRequestSize(
            string payload,
            bool hasBinaryAttachments)
        {
            long payloadBytes = Encoding.UTF8.GetByteCount(
                payload ?? string.Empty);
            if (payloadBytes <= MaximumRequestBytes)
            {
                return;
            }

            throw new ModelCallException(
                hasBinaryAttachments
                    ? "附件编码后的模型请求约 " +
                        FormatMegabytes(payloadBytes) +
                        " MB，超过 32 MB 安全限制。" +
                        "请减少附件数量、缩小文件或新建会话后重试。"
                    : "模型请求约 " +
                        FormatMegabytes(payloadBytes) +
                        " MB，超过 32 MB 安全限制。" +
                        "请减少资料或会话内容。");
        }

        private static long AddSaturated(long left, long right)
        {
            if (left < 0L || right < 0L || left > long.MaxValue - right)
            {
                return long.MaxValue;
            }

            return left + right;
        }

        private static string FormatMegabytes(long bytes)
        {
            return (bytes / 1024d / 1024d).ToString(
                "0.0",
                CultureInfo.InvariantCulture);
        }

        private string BuildPayload(ModelRequest request, bool stream)
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["model"] = request.ModelName;
            root["stream"] = stream;
            root["messages"] = BuildInitialMessages(request, true).ToArray();
            AddGenerationOptions(root, request);
            return json.Serialize(root);
        }

        private static void AddGenerationOptions(
            IDictionary<string, object> root,
            ModelRequest request)
        {
            if (request.Temperature.HasValue)
            {
                root["temperature"] = request.Temperature.Value;
            }

            if (request.TopP.HasValue)
            {
                root["top_p"] = request.TopP.Value;
            }

            if (request.MaxOutputTokens.HasValue)
            {
                root["max_tokens"] = request.MaxOutputTokens.Value;
            }
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
            AddGenerationOptions(root, request);
            string payload = json.Serialize(root);
            EnsureSerializedRequestSize(
                payload,
                HasBinaryAttachments(request));

            return payload;
        }

        private static void ReplaceBinaryAttachmentsForToolFollowUp(
            IList<object> messages,
            ModelRequest request)
        {
            if (messages == null || messages.Count == 0 ||
                !HasBinaryAttachments(request))
            {
                return;
            }

            IDictionary<string, object> currentMessage =
                messages[messages.Count - 1] as IDictionary<string, object>;
            if (currentMessage == null ||
                !string.Equals(
                    GetString(currentMessage, "role"),
                    "user",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelCallException(
                    "无法安全准备附件请求的工具后续回合。");
            }

            currentMessage["content"] =
                (request.Prompt ?? string.Empty) +
                "\r\n\r\n[二进制附件已在本轮首次请求中提供。" +
                "为避免自动重复上传，后续 MCP 工具回合只保留附件信息。]" +
                BuildBinaryAttachmentSummary(request);
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
                Names = names,
                EstimatedBytes = AddSaturated(
                    combinedCharacters * 3L,
                    payload.Count * 256L)
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
            string text = parsed as string;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return LimitServerErrorMessage(text);
            }

            IDictionary<string, object> root = AsDictionary(parsed);
            if (root == null)
            {
                return string.Empty;
            }

            IDictionary<string, object> error = AsDictionary(GetValue(root, "error"));
            string message = GetString(error, "message");
            if (!string.IsNullOrEmpty(message))
            {
                return LimitServerErrorMessage(message);
            }

            string errorText = GetValue(root, "error") as string;
            if (!string.IsNullOrWhiteSpace(errorText))
            {
                return LimitServerErrorMessage(errorText);
            }

            message = GetString(root, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return LimitServerErrorMessage(message);
            }

            object detailValue = GetValue(root, "detail");
            string detail = detailValue as string;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                return LimitServerErrorMessage(detail);
            }

            IDictionary<string, object> detailObject =
                AsDictionary(detailValue);
            detail = GetString(detailObject, "message");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                return LimitServerErrorMessage(detail);
            }

            string type = GetString(root, "type");
            if (!string.IsNullOrEmpty(type) &&
                type.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = GetString(root, "message");
                if (!string.IsNullOrEmpty(message))
                {
                    return LimitServerErrorMessage(message);
                }
            }

            return string.Empty;
        }

        private string ExtractErrorMessageFromBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            try
            {
                string parsedMessage = ExtractErrorMessage(
                    Deserialize(body));
                if (!string.IsNullOrWhiteSpace(parsedMessage))
                {
                    return parsedMessage;
                }
            }
            catch
            {
                // A proxy may return a plain-text error instead of JSON.
            }

            string trimmed = body.Trim();
            if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return LimitServerErrorMessage(trimmed);
        }

        private static string LimitServerErrorMessage(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            StringBuilder safe = new StringBuilder();
            foreach (char character in value.Trim())
            {
                if (!char.IsControl(character) || character == '\r' ||
                    character == '\n' || character == '\t')
                {
                    safe.Append(character);
                }
            }

            return TruncateWithNotice(
                safe.ToString(),
                MaximumDisplayedServerErrorCharacters,
                "\r\n[服务端信息过长，已截断]");
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

        private static Uri BuildModelsEndpoint(Uri endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException("endpoint");
            }

            string path = endpoint.AbsolutePath.TrimEnd('/');
            string[] requestSuffixes =
            {
                "/chat/completions",
                "/completions",
                "/responses",
                "/embeddings",
                "/images/generations"
            };
            string modelsPath = null;
            foreach (string suffix in requestSuffixes)
            {
                if (path.EndsWith(
                    suffix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    modelsPath = path.Substring(0, path.Length - suffix.Length) +
                        "/models";
                    break;
                }
            }

            if (modelsPath == null && path.EndsWith(
                "/models",
                StringComparison.OrdinalIgnoreCase))
            {
                modelsPath = path;
            }
            if (modelsPath == null && path.EndsWith(
                "/v1",
                StringComparison.OrdinalIgnoreCase))
            {
                modelsPath = path + "/models";
            }
            if (modelsPath == null)
            {
                throw new ModelCallException(
                    "无法从当前请求 URL 推导模型列表地址。" +
                    "请使用以 /chat/completions、/responses、/completions、" +
                    "/embeddings 或 /images/generations 结尾的 OpenAI 兼容 URL，" +
                    "或继续手动输入模型名称。");
            }

            UriBuilder builder = new UriBuilder(endpoint);
            builder.Path = modelsPath;
            builder.Fragment = string.Empty;
            return builder.Uri;
        }

        private IList<string> ParseModelIdentifiers(object parsed)
        {
            IDictionary<string, object> root = AsDictionary(parsed);
            IList entries = root == null
                ? null
                : AsList(GetValue(root, "data"));

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> result = new List<string>();
            foreach (object entry in entries ?? new object[0])
            {
                IDictionary<string, object> item = AsDictionary(entry);
                object identifierValue = item == null
                    ? null
                    : GetValue(item, "id");
                string identifier = identifierValue as string;

                if (identifier == null ||
                    identifier.Any(character => char.IsControl(character)))
                {
                    continue;
                }

                identifier = identifier.Trim();
                if (identifier.Length == 0 ||
                    identifier.Length > MaximumModelIdentifierCharacters ||
                    !seen.Add(identifier))
                {
                    continue;
                }

                result.Add(identifier);
                if (result.Count >= MaximumListedModels)
                {
                    break;
                }
            }

            result.Sort(delegate(string left, string right)
            {
                int comparison = StringComparer.OrdinalIgnoreCase.Compare(
                    left,
                    right);
                return comparison != 0
                    ? comparison
                    : StringComparer.Ordinal.Compare(left, right);
            });
            return result;
        }

        private ModelCallException CreateModelListException(
            int statusCode,
            string body,
            string requestId)
        {
            string message;
            switch (statusCode)
            {
                case 401:
                    message = "API Key 无效，无法获取模型列表。";
                    break;
                case 403:
                    message = "当前 Key 没有读取模型列表的权限。";
                    break;
                case 404:
                    message = "当前服务没有提供兼容的 /models 接口。" +
                        "仍可手动输入模型名称。";
                    break;
                default:
                    message = statusCode >= 300 && statusCode < 400
                        ? "模型列表接口返回了重定向；为避免把 API Key 发往其他地址，" +
                            "程序没有跟随重定向。"
                        : (statusCode >= 500
                            ? "模型服务暂时无法提供模型列表。"
                            : "模型列表接口返回错误。");
                    break;
            }

            string serverMessage = ExtractErrorMessageFromBody(body);
            if (!string.IsNullOrWhiteSpace(serverMessage))
            {
                message += "\r\n\r\n服务端信息：" + serverMessage;
            }
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                message += "\r\n请求 ID：" + requestId;
            }

            return new ModelCallException(message, statusCode, requestId);
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

        private ModelCallException CreateUserFacingException(
            AttemptException exception,
            ModelRequest request)
        {
            string serverMessage = ExtractErrorMessageFromBody(exception.Body);
            bool hasBinaryAttachments = HasBinaryAttachments(request);

            string prefix;
            switch (exception.StatusCode)
            {
                case 400:
                case 422:
                    if (hasBinaryAttachments &&
                        IsStreamUnsupported(exception))
                    {
                        prefix =
                            "接口不支持附件请求的流式输出。为避免重复上传，" +
                            "程序没有自动改用普通请求；请调整接口支持流式附件，" +
                            "或移除附件后重试。";
                    }
                    else
                    {
                        prefix = hasBinaryAttachments
                            ? "接口拒绝了附件请求，当前 URL 或模型可能不支持图片或" +
                                "内联文件输入。请改用支持附件的模型，或移除附件后重试。"
                            : "请求格式或模型参数不匹配。";
                    }
                    break;
                case 415:
                    prefix = hasBinaryAttachments
                        ? "接口不接受本次附件格式，当前 URL 或模型可能不支持图片或" +
                            "内联文件输入。请改用支持附件的模型，或移除附件后重试。"
                        : "接口不接受本次请求的内容类型。";
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
                    prefix = hasBinaryAttachments
                        ? "附件编码后的提交内容过大，请减少附件数量或缩小文件。"
                        : "提交内容过大，请减少资料或会话内容。";
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
            if (hasBinaryAttachments &&
                (exception.StatusCode == 400 ||
                    exception.StatusCode == 413 ||
                    exception.StatusCode == 415 ||
                    exception.StatusCode == 422))
            {
                message += BuildBinaryAttachmentSummary(request);
            }

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

        private static string BuildBinaryAttachmentSummary(
            ModelRequest request)
        {
            StringBuilder summary = new StringBuilder();
            int total = 0;
            int displayed = 0;
            foreach (InputItem item in request == null ||
                request.Attachments == null
                    ? new List<InputItem>()
                    : request.Attachments)
            {
                if (item == null || item.BinaryData == null ||
                    item.BinaryData.Length == 0)
                {
                    continue;
                }

                total++;
                if (displayed >= 8)
                {
                    continue;
                }

                if (summary.Length == 0)
                {
                    summary.Append("\r\n\r\n本轮附件：");
                }

                string name = (item.Name ?? "未命名附件")
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Trim();
                if (name.Length > 120)
                {
                    name = name.Substring(0, 117) + "...";
                }

                summary.Append("\r\n- ");
                summary.Append(name);
                summary.Append("（");
                summary.Append(item.GetKindText());
                summary.Append("，");
                summary.Append(item.GetSizeText());
                summary.Append("）");
                displayed++;
            }

            if (total > displayed)
            {
                summary.Append("\r\n- 另有 ");
                summary.Append((total - displayed).ToString(
                    CultureInfo.InvariantCulture));
                summary.Append(" 个附件");
            }

            return summary.ToString();
        }

        private static ModelCallException CreateNetworkException(
            HttpRequestException exception,
            ModelRequest request)
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

            if (HasBinaryAttachments(request))
            {
                return new ModelCallException(
                    "附件上传、服务端解析或等待响应失败：" + message +
                    "\r\n\r\n为避免重复提交，程序没有自动重新上传附件；" +
                    "请确认接口支持附件并检查服务状态后手动重试。" +
                    BuildBinaryAttachmentSummary(request));
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

        private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
        {
            RetryConditionHeaderValue header;
            try
            {
                header = response.Headers.RetryAfter;
            }
            catch (FormatException)
            {
                return null;
            }

            if (header == null)
            {
                return null;
            }

            if (header.Delta.HasValue)
            {
                return header.Delta.Value < TimeSpan.Zero
                    ? TimeSpan.Zero
                    : header.Delta.Value;
            }

            if (header.Date.HasValue)
            {
                TimeSpan remaining = header.Date.Value - DateTimeOffset.UtcNow;
                return remaining < TimeSpan.Zero
                    ? TimeSpan.Zero
                    : remaining;
            }

            return null;
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

            if (request.Temperature.HasValue &&
                (double.IsNaN(request.Temperature.Value) ||
                 double.IsInfinity(request.Temperature.Value) ||
                 request.Temperature.Value < 0d ||
                 request.Temperature.Value > 2d))
            {
                throw new ModelCallException(
                    "temperature 必须在 0 到 2 之间。");
            }

            if (request.TopP.HasValue &&
                (double.IsNaN(request.TopP.Value) ||
                 double.IsInfinity(request.TopP.Value) ||
                 request.TopP.Value < 0d ||
                 request.TopP.Value > 1d))
            {
                throw new ModelCallException("top_p 必须在 0 到 1 之间。");
            }

            if (request.MaxOutputTokens.HasValue &&
                (request.MaxOutputTokens.Value < 1 ||
                 request.MaxOutputTokens.Value > 1048576))
            {
                throw new ModelCallException(
                    "最大输出 Token 必须在 1 到 1,048,576 之间。");
            }

            ValidateBinaryAttachmentBudget(request, 0L);
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
            public long EstimatedBytes { get; set; }
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
            public TimeSpan? RetryAfter { get; private set; }

            public AttemptException(
                int statusCode,
                string body,
                string requestId,
                TimeSpan? retryAfter)
                : base("HTTP " + statusCode.ToString(CultureInfo.InvariantCulture))
            {
                StatusCode = statusCode;
                Body = body;
                RequestId = requestId;
                RetryAfter = retryAfter;
            }
        }

        private sealed class ConnectionAttemptException : HttpRequestException
        {
            public ConnectionAttemptException(
                string message,
                Exception innerException)
                : base(message, innerException)
            {
            }
        }
    }
}
