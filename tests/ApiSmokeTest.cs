using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class ApiSmokeTest
{
    private const string HistoryUser = "Previous user question";
    private const string HistoryAssistant = "Previous assistant answer";
    private const string CurrentPrompt =
        "Authorized extract: authorized-notes.txt\nOnly this pasted text may be sent.";
    private const string SensitiveLocalPath =
        @"C:\Private\never-send\source.png";

    private static int Main(string[] args)
    {
        try
        {
            string applicationPath = ResolveApplicationPath(args);
            Console.WriteLine("Application: " + applicationPath);
            int generationResult = RunGenerationTest(applicationPath);
            int connectionResult = RunConnectionTest(applicationPath);
            return generationResult == 0 && connectionResult == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Exception actual = exception;
            while (actual is TargetInvocationException &&
                actual.InnerException != null)
            {
                actual = actual.InnerException;
            }

            Console.Error.WriteLine("FAIL | " + actual.GetType().FullName);
            Console.Error.WriteLine(actual.ToString());
            return 2;
        }
    }

    private static int RunGenerationTest(string applicationPath)
    {
        ServicePointManager.Expect100Continue = false;
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<string> server = Task.Factory.StartNew(
            delegate { return HandleOneRequest(listener, true); });

        object client = null;
        try
        {
            Assembly application = Assembly.LoadFrom(applicationPath);
            Type clientType = application.GetType(
                "FilePromptWin7.ModelClient",
                true);
            Type requestType = application.GetType(
                "FilePromptWin7.ModelRequest",
                true);
            Type itemType = application.GetType(
                "FilePromptWin7.InputItem",
                true);
            Type messageType = application.GetType(
                "FilePromptWin7.ConversationMessage",
                true);

            client = Activator.CreateInstance(clientType, true);
            object request = Activator.CreateInstance(requestType, true);
            requestType.GetProperty("EndpointUrl").SetValue(
                request,
                "http://127.0.0.1:" + port +
                    "/custom/full/request?tenant=abc&mode=chat",
                null);
            requestType.GetProperty("ApiKey").SetValue(
                request,
                "test-secret",
                null);
            requestType.GetProperty("ModelName").SetValue(
                request,
                "custom-model",
                null);
            requestType.GetProperty("Prompt").SetValue(
                request,
                CurrentPrompt,
                null);

            object history = CreateHistory(messageType);
            requestType.GetProperty("ConversationMessages").SetValue(
                request,
                history,
                null);

            object attachments = CreateAttachments(itemType);
            requestType.GetProperty("Attachments").SetValue(
                request,
                attachments,
                null);

            StringBuilder streamed = new StringBuilder();
            Action<string> onDelta = delegate(string value)
            {
                streamed.Append(value);
            };
            Action<string> onStatus = delegate { };
            MethodInfo generate = clientType.GetMethod(
                "GenerateAsync",
                BindingFlags.Instance | BindingFlags.Public);
            object taskObject = generate.Invoke(
                client,
                new object[]
                {
                    request,
                    onDelta,
                    onStatus,
                    CancellationToken.None
                });
            Task task = (Task)taskObject;
            if (!task.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("ModelClient request timed out.");
            }

            string result = Convert.ToString(
                taskObject.GetType().GetProperty("Result").GetValue(
                    taskObject,
                    null));
            if (!server.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Local request capture timed out.");
            }

            string rawRequest = server.Result;
            TestResults results = InspectRequest(rawRequest, result, streamed.ToString());
            results.Print();
            return results.AllPassed ? 0 : 1;
        }
        finally
        {
            if (client != null)
            {
                ((IDisposable)client).Dispose();
            }

            listener.Stop();
        }
    }

    private static int RunConnectionTest(string applicationPath)
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<string> server = Task.Factory.StartNew(
            delegate { return HandleOneRequest(listener, false); });

        object client = null;
        try
        {
            Assembly application = Assembly.LoadFrom(applicationPath);
            Type clientType = application.GetType(
                "FilePromptWin7.ModelClient",
                true);
            client = Activator.CreateInstance(clientType, true);
            MethodInfo testConnection = clientType.GetMethod(
                "TestConnectionAsync",
                BindingFlags.Instance | BindingFlags.Public);
            if (testConnection == null)
            {
                throw new MissingMethodException(
                    clientType.FullName,
                    "TestConnectionAsync");
            }

            object taskObject = testConnection.Invoke(
                client,
                new object[]
                {
                    "http://127.0.0.1:" + port +
                        "/health/custom?tenant=abc&mode=connection-test",
                    "connection-secret",
                    "connection-model",
                    CancellationToken.None
                });
            Task task = (Task)taskObject;
            if (!task.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("Connection test request timed out.");
            }

            if (!server.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "Connection test request capture timed out.");
            }

            ConnectionTestResults results =
                InspectConnectionRequest(server.Result);
            results.Print();
            return results.AllPassed ? 0 : 1;
        }
        finally
        {
            if (client != null)
            {
                ((IDisposable)client).Dispose();
            }

            listener.Stop();
        }
    }

    private static object CreateHistory(Type messageType)
    {
        Type listType = typeof(List<>).MakeGenericType(messageType);
        IList history = (IList)Activator.CreateInstance(listType);

        object user = Activator.CreateInstance(messageType, true);
        messageType.GetProperty("Role").SetValue(user, "user", null);
        messageType.GetProperty("Content").SetValue(user, HistoryUser, null);
        history.Add(user);

        object assistant = Activator.CreateInstance(messageType, true);
        messageType.GetProperty("Role").SetValue(
            assistant,
            "assistant",
            null);
        messageType.GetProperty("Content").SetValue(
            assistant,
            HistoryAssistant,
            null);
        history.Add(assistant);
        return history;
    }

    private static object CreateAttachments(Type itemType)
    {
        Type listType = typeof(List<>).MakeGenericType(itemType);
        IList attachments = (IList)Activator.CreateInstance(listType);
        object imageItem = Activator.CreateInstance(itemType, true);
        itemType.GetProperty("Name").SetValue(
            imageItem,
            "pasted-image.png",
            null);
        itemType.GetProperty("Kind").SetValue(
            imageItem,
            Enum.Parse(itemType.GetProperty("Kind").PropertyType, "Image"),
            null);
        itemType.GetProperty("MimeType").SetValue(
            imageItem,
            "image/png",
            null);
        itemType.GetProperty("BinaryData").SetValue(
            imageItem,
            new byte[] { 1, 2, 3, 4 },
            null);
        itemType.GetProperty("Note").SetValue(
            imageItem,
            SensitiveLocalPath,
            null);
        attachments.Add(imageItem);
        return attachments;
    }

    private static TestResults InspectRequest(
        string rawRequest,
        string result,
        string streamed)
    {
        int bodyOffset = rawRequest.IndexOf(
            "\r\n\r\n",
            StringComparison.Ordinal);
        string requestBody = bodyOffset < 0
            ? string.Empty
            : rawRequest.Substring(bodyOffset + 4);

        JavaScriptSerializer json = new JavaScriptSerializer();
        IDictionary<string, object> root =
            json.DeserializeObject(requestBody) as IDictionary<string, object>;
        IList messages = root == null ? null : root["messages"] as IList;

        TestResults results = new TestResults();
        results.ExactUrl = rawRequest.StartsWith(
            "POST /custom/full/request?tenant=abc&mode=chat HTTP/1.1",
            StringComparison.Ordinal);
        results.BearerKey = rawRequest.IndexOf(
            "Authorization: Bearer test-secret",
            StringComparison.OrdinalIgnoreCase) >= 0;
        results.Model = root != null &&
            Convert.ToString(root["model"]) == "custom-model";
        results.Stream = root != null &&
            root.ContainsKey("stream") &&
            Convert.ToBoolean(root["stream"]);
        results.HistoryOrder = HasHistoryInOrder(messages);
        results.CurrentContent = HasCurrentContent(messages);
        results.InlineImage = HasInlineImage(messages);
        results.NoLocalPath = requestBody.IndexOf(
            SensitiveLocalPath,
            StringComparison.OrdinalIgnoreCase) < 0 &&
            requestBody.IndexOf(
                "never-send",
                StringComparison.OrdinalIgnoreCase) < 0;
        results.SingleProtocol = root != null &&
            !root.ContainsKey("input");
        string expected = "\u4f60\u597d\uff0c\u529f\u80fd\u6b63\u5e38\u3002";
        results.SseOutput = result == expected && streamed == expected;
        return results;
    }

    private static ConnectionTestResults InspectConnectionRequest(
        string rawRequest)
    {
        int bodyOffset = rawRequest.IndexOf(
            "\r\n\r\n",
            StringComparison.Ordinal);
        string requestBody = bodyOffset < 0
            ? string.Empty
            : rawRequest.Substring(bodyOffset + 4);

        JavaScriptSerializer json = new JavaScriptSerializer();
        IDictionary<string, object> root =
            json.DeserializeObject(requestBody) as IDictionary<string, object>;
        IList messages = root == null ? null : root["messages"] as IList;
        IDictionary<string, object> message =
            messages != null && messages.Count == 1
                ? messages[0] as IDictionary<string, object>
                : null;

        ConnectionTestResults results = new ConnectionTestResults();
        results.ExactUrl = rawRequest.StartsWith(
            "POST /health/custom?tenant=abc&mode=connection-test HTTP/1.1",
            StringComparison.Ordinal);
        results.BearerKey = rawRequest.IndexOf(
            "Authorization: Bearer connection-secret",
            StringComparison.OrdinalIgnoreCase) >= 0;
        results.Model = root != null &&
            Convert.ToString(root["model"]) == "connection-model";
        results.StreamDisabled = root != null &&
            root.ContainsKey("stream") &&
            !Convert.ToBoolean(root["stream"]);
        results.MinimalMessage = root != null &&
            root.Count == 3 &&
            message != null &&
            message.Count == 2 &&
            Convert.ToString(message["role"]) == "user" &&
            Convert.ToString(message["content"]) == "ping";
        results.NoHistory = messages != null &&
            messages.Count == 1 &&
            requestBody.IndexOf(
                HistoryUser,
                StringComparison.Ordinal) < 0 &&
            requestBody.IndexOf(
                HistoryAssistant,
                StringComparison.Ordinal) < 0 &&
            requestBody.IndexOf(
                CurrentPrompt,
                StringComparison.Ordinal) < 0;
        results.NoAttachments = requestBody.IndexOf(
            "image_url",
            StringComparison.OrdinalIgnoreCase) < 0 &&
            requestBody.IndexOf(
                "file_data",
                StringComparison.OrdinalIgnoreCase) < 0 &&
            requestBody.IndexOf(
                "base64",
                StringComparison.OrdinalIgnoreCase) < 0 &&
            requestBody.IndexOf(
                SensitiveLocalPath,
                StringComparison.OrdinalIgnoreCase) < 0;
        return results;
    }

    private static bool HasHistoryInOrder(IList messages)
    {
        if (messages == null || messages.Count != 3)
        {
            return false;
        }

        IDictionary<string, object> first =
            messages[0] as IDictionary<string, object>;
        IDictionary<string, object> second =
            messages[1] as IDictionary<string, object>;
        return HasMessage(first, "user", HistoryUser) &&
            HasMessage(second, "assistant", HistoryAssistant);
    }

    private static bool HasMessage(
        IDictionary<string, object> message,
        string role,
        string content)
    {
        return message != null &&
            Convert.ToString(message["role"]) == role &&
            Convert.ToString(message["content"]) == content;
    }

    private static bool HasCurrentContent(IList messages)
    {
        IList parts = GetCurrentParts(messages);
        if (parts == null || parts.Count != 2)
        {
            return false;
        }

        IDictionary<string, object> current =
            messages[2] as IDictionary<string, object>;
        IDictionary<string, object> textPart =
            parts[0] as IDictionary<string, object>;
        return current != null &&
            Convert.ToString(current["role"]) == "user" &&
            textPart != null &&
            Convert.ToString(textPart["type"]) == "text" &&
            Convert.ToString(textPart["text"]) == CurrentPrompt;
    }

    private static bool HasInlineImage(IList messages)
    {
        IList parts = GetCurrentParts(messages);
        if (parts == null || parts.Count != 2)
        {
            return false;
        }

        IDictionary<string, object> imagePart =
            parts[1] as IDictionary<string, object>;
        if (imagePart == null ||
            Convert.ToString(imagePart["type"]) != "image_url")
        {
            return false;
        }

        IDictionary<string, object> image =
            imagePart["image_url"] as IDictionary<string, object>;
        return image != null &&
            Convert.ToString(image["url"]) ==
                "data:image/png;base64,AQIDBA==";
    }

    private static IList GetCurrentParts(IList messages)
    {
        if (messages == null || messages.Count < 3)
        {
            return null;
        }

        IDictionary<string, object> current =
            messages[2] as IDictionary<string, object>;
        return current == null ? null : current["content"] as IList;
    }

    private static string ResolveApplicationPath(string[] args)
    {
        string candidate = args != null && args.Length > 0
            ? args[0]
            : string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "FilePrompt.exe");
        }

        candidate = Path.GetFullPath(candidate);
        if (!File.Exists(candidate))
        {
            string projectCandidate = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..",
                "..",
                "dist",
                "FilePrompt.exe"));
            if (File.Exists(projectCandidate))
            {
                candidate = projectCandidate;
            }
        }

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                "FilePrompt.exe was not found. Pass its full path as argument 1.",
                candidate);
        }

        return candidate;
    }

    private static string HandleOneRequest(
        TcpListener listener,
        bool streamResponse)
    {
        using (TcpClient connection = listener.AcceptTcpClient())
        using (NetworkStream stream = connection.GetStream())
        {
            stream.ReadTimeout = 15000;
            stream.WriteTimeout = 15000;
            MemoryStream requestBytes = new MemoryStream();
            byte[] buffer = new byte[4096];
            int headerEnd = -1;
            int contentLength = 0;

            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                requestBytes.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(
                    requestBytes.GetBuffer(),
                    (int)requestBytes.Length);
            }

            string headers = Encoding.ASCII.GetString(
                requestBytes.GetBuffer(),
                0,
                headerEnd < 0 ? (int)requestBytes.Length : headerEnd);
            foreach (string line in headers.Split(
                new[] { "\r\n" },
                StringSplitOptions.None))
            {
                if (line.StartsWith(
                    "Content-Length:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring(15).Trim(), out contentLength);
                }
            }

            int bodyStart = headerEnd < 0
                ? (int)requestBytes.Length
                : headerEnd + 4;
            while (requestBytes.Length - bodyStart < contentLength)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                requestBytes.Write(buffer, 0, read);
            }

            string rawRequest = Encoding.UTF8.GetString(
                requestBytes.GetBuffer(),
                0,
                (int)requestBytes.Length);

            string body = streamResponse
                ? "data: {\"choices\":[{\"delta\":{\"content\":\"" +
                    "\\u4f60\\u597d\\uff0c\"}}]}\r\n\r\n" +
                    "data: {\"choices\":[{\"delta\":{\"content\":\"" +
                    "\\u529f\\u80fd\\u6b63\\u5e38\\u3002\"}}]}\r\n\r\n" +
                    "data: [DONE]\r\n\r\n"
                : "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string responseHeaders =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: " + (streamResponse
                    ? "text/event-stream"
                    : "application/json") + "; charset=utf-8\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Connection: close\r\n\r\n";
            byte[] responseHeaderBytes =
                Encoding.ASCII.GetBytes(responseHeaders);
            stream.Write(
                responseHeaderBytes,
                0,
                responseHeaderBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
            return rawRequest;
        }
    }

    private static int FindHeaderEnd(byte[] value, int length)
    {
        for (int index = 0; index <= length - 4; index++)
        {
            if (value[index] == 13 &&
                value[index + 1] == 10 &&
                value[index + 2] == 13 &&
                value[index + 3] == 10)
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class ConnectionTestResults
    {
        public bool ExactUrl { get; set; }
        public bool BearerKey { get; set; }
        public bool Model { get; set; }
        public bool StreamDisabled { get; set; }
        public bool MinimalMessage { get; set; }
        public bool NoHistory { get; set; }
        public bool NoAttachments { get; set; }

        public bool AllPassed
        {
            get
            {
                return ExactUrl && BearerKey && Model && StreamDisabled &&
                    MinimalMessage && NoHistory && NoAttachments;
            }
        }

        public void Print()
        {
            PrintResult("Connection exact full URL", ExactUrl);
            PrintResult("Connection Bearer key", BearerKey);
            PrintResult("Connection model field", Model);
            PrintResult("Connection stream disabled", StreamDisabled);
            PrintResult("Connection minimal message", MinimalMessage);
            PrintResult("Connection has no history", NoHistory);
            PrintResult("Connection has no attachments", NoAttachments);
            Console.WriteLine(AllPassed
                ? "PASS | connection smoke"
                : "FAIL | connection smoke");
        }

        private static void PrintResult(string name, bool passed)
        {
            Console.WriteLine(
                (passed ? "PASS" : "FAIL") + " | " + name);
        }
    }

    private sealed class TestResults
    {
        public bool ExactUrl { get; set; }
        public bool BearerKey { get; set; }
        public bool Model { get; set; }
        public bool Stream { get; set; }
        public bool HistoryOrder { get; set; }
        public bool CurrentContent { get; set; }
        public bool InlineImage { get; set; }
        public bool NoLocalPath { get; set; }
        public bool SingleProtocol { get; set; }
        public bool SseOutput { get; set; }

        public bool AllPassed
        {
            get
            {
                return ExactUrl && BearerKey && Model && Stream &&
                    HistoryOrder && CurrentContent && InlineImage &&
                    NoLocalPath && SingleProtocol && SseOutput;
            }
        }

        public void Print()
        {
            PrintResult("Exact full URL", ExactUrl);
            PrintResult("Bearer key", BearerKey);
            PrintResult("Model field", Model);
            PrintResult("Streaming request", Stream);
            PrintResult("History order", HistoryOrder);
            PrintResult("Current authorized content", CurrentContent);
            PrintResult("Inline pasted image", InlineImage);
            PrintResult("No local path disclosure", NoLocalPath);
            PrintResult("Chat protocol only", SingleProtocol);
            PrintResult("SSE output", SseOutput);
            Console.WriteLine(AllPassed ? "PASS | api smoke" : "FAIL | api smoke");
        }

        private static void PrintResult(string name, bool passed)
        {
            Console.WriteLine(
                (passed ? "PASS" : "FAIL") + " | " + name);
        }
    }
}
