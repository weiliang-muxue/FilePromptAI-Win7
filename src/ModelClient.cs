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

namespace FilePromptWin7
{
    internal sealed class ModelClient : IDisposable
    {
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
                            string errorBody = await response.Content
                            .ReadAsStringAsync()
                            .ConfigureAwait(false);
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

                        string body = await response.Content
                            .ReadAsStringAsync()
                            .ConfigureAwait(false);
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
                            eventData.Append('\n');
                        }

                        eventData.Append(line.Substring(5).TrimStart());
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
                result.Append(delta);
                Notify(onDelta, delta);
            }
            else if (result.Length == 0)
            {
                string fullText = ExtractText(parsed);
                if (!string.IsNullOrEmpty(fullText))
                {
                    result.Append(fullText);
                    Notify(onDelta, fullText);
                }
            }

            return false;
        }

        private string BuildPayload(ModelRequest request, bool stream)
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["model"] = request.ModelName;
            root["stream"] = stream;

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

            List<object> messages = new List<object>();
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
                            ConversationMessage.NormalizeRole(historyMessage.Role)
                        },
                        {
                            "content",
                            historyMessage.Content ?? string.Empty
                        }
                    });
                }
            }

            messages.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", content }
            });
            root["messages"] = messages.ToArray();
            return json.Serialize(root);
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
