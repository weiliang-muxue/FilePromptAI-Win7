using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class NetworkReliabilitySmokeTest
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string applicationPath = ResolveApplicationPath(args);
            Assembly application = Assembly.LoadFrom(applicationPath);
            Type clientType = application.GetType(
                "FilePromptAIWin7.ModelClient",
                true);
            Type requestType = application.GetType(
                "FilePromptAIWin7.ModelRequest",
                true);
            Type itemType = application.GetType(
                "FilePromptAIWin7.InputItem",
                true);

            TestModelEndpointDerivation(clientType);
            TestModelDiscoveryProtocol(clientType);
            TestAnonymousRequestsDoNotSendAuthorization(
                clientType,
                requestType);
            TestModelTrafficBypassesSystemProxy(clientType, requestType);
            TestUnknownModelEndpointIsRejected(clientType);
            TestModelDiscoveryDoesNotFollowRedirects(clientType);
            TestNonStandardModelResponsesAreRejected(clientType);
            TestAvailableModelsPreserveManualSelection(application);
            TestRetriableStatusCodes(clientType, requestType);
            TestAttachmentStatusCodesDoNotRetry(
                clientType,
                requestType,
                itemType);
            TestAttachmentStreamFallbackDoesNotRetry(
                clientType,
                requestType,
                itemType);
            TestTextStreamFallbackStillWorks(clientType, requestType);
            TestToolAttachmentStatusCodesDoNotRetry(
                clientType,
                requestType,
                itemType);
            TestRetryAfterIsHonored(clientType, requestType);
            TestLongRetryAfterIsNotIgnored(clientType, requestType);
            TestResponseHeadersTimeoutRetries(clientType, requestType);
            TestAttachmentHeadersTimeoutDoesNotRetry(
                clientType,
                requestType,
                itemType);
            TestConnectionEstablishmentFailureRetries(clientType, requestType);
            TestStreamIdleTimeout(clientType, requestType);
            TestIncompleteStreamFails(clientType, requestType);
            TestIncompleteStreamBeforeTextDoesNotRetry(
                clientType,
                requestType);
            TestStreamingProtocolMatrix(clientType, requestType);
            TestStreamingResponseLimits(clientType, requestType);
            TestReasoningAndToolStreamDataDoNotLeak(
                clientType,
                requestType);
            TestStreamingErrorEvents(clientType, requestType);
            TestEmptyCompletedTextStreamFallsBack(
                clientType,
                requestType);
            TestStructuredEmptyCompletedTextStreamFallsBack(
                clientType,
                requestType);
            TestSemanticEmptyCompletionDoesNotFallback(
                clientType,
                requestType);
            TestEmptyCompletedAttachmentStreamDoesNotRetry(
                clientType,
                requestType,
                itemType);
            TestFinishReasonCompletesStream(clientType, requestType);
            TestEventNameCompletesStream(clientType, requestType);
            TestCancellationWinsOverHeadersTimeout(clientType, requestType);
            Console.WriteLine("PASS | network reliability");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | network reliability");
            Console.Error.WriteLine(Unwrap(exception));
            return 1;
        }
    }

    private static void TestModelEndpointDerivation(Type clientType)
    {
        MethodInfo buildModelsEndpoint = clientType.GetMethod(
            "BuildModelsEndpoint",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (buildModelsEndpoint == null)
        {
            throw new MissingMethodException(
                clientType.FullName,
                "BuildModelsEndpoint");
        }

        string[] inputs =
        {
            "https://example.test/tenant/v1/chat/completions?tenant=abc#ignored",
            "https://example.test/tenant/v1/responses?tenant=abc#ignored",
            "https://example.test/tenant/v1/completions?tenant=abc#ignored",
            "https://example.test/tenant/v1/embeddings?tenant=abc#ignored",
            "https://example.test/tenant/v1/images/generations?tenant=abc#ignored",
            "https://example.test/tenant/v1/?tenant=abc#ignored",
            "https://example.test/tenant/v1/models/?tenant=abc#ignored"
        };
        foreach (string input in inputs)
        {
            Uri actual = (Uri)buildModelsEndpoint.Invoke(
                null,
                new object[] { new Uri(input) });
            AssertEqual(
                "/tenant/v1/models?tenant=abc",
                actual.PathAndQuery,
                "Model endpoint derivation " + new Uri(input).AbsolutePath);
            AssertEqual(
                string.Empty,
                actual.Fragment,
                "Model endpoint fragment removed " + new Uri(input).AbsolutePath);
        }
    }

    private static void TestModelDiscoveryProtocol(Type clientType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        string requestHeaders = null;
        string overlong = new string('x', 513);
        string responseBody =
            "{\"object\":\"list\",\"data\":[" +
            "{\"id\":\"zeta\"}," +
            "{\"id\":\" Alpha \"}," +
            "{\"id\":\"alpha\"}," +
            "{\"id\":\"zeta\"}," +
            "{\"id\":\"bad\\u000aidentifier\"}," +
            "{\"id\":\"" + overlong + "\"}," +
            "{\"id\":123}," +
            "{\"name\":\"not-an-id\"}," +
            "{\"id\":\"Beta\"}]}";
        Task<int> server = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                {
                    requestHeaders = ReadRequestText(connection.GetStream());
                    SendResponse(
                        connection.GetStream(),
                        200,
                        "OK",
                        "application/json",
                        responseBody,
                        null);
                }

                return 1;
            });
        object client = CreateClient(clientType, 2000, 1000, 1, 0, 2000);
        try
        {
            IList models = FetchModels(
                clientType,
                client,
                "http://127.0.0.1:" + port +
                    "/tenant/v1/chat/completions?tenant=abc#ignored",
                "  test-key  ",
                TimeSpan.FromSeconds(5));
            AssertEqual(1, Wait(server), "Model discovery request count");
            AssertEqual(4, models.Count, "Model discovery valid model count");
            AssertEqual("Alpha", Convert.ToString(models[0]), "Model sort first");
            AssertEqual("alpha", Convert.ToString(models[1]), "Model sort tie-break");
            AssertEqual("Beta", Convert.ToString(models[2]), "Model sort middle");
            AssertEqual("zeta", Convert.ToString(models[3]), "Model sort last");
            AssertContains(
                requestHeaders,
                "GET /tenant/v1/models?tenant=abc HTTP/1.1",
                "Model discovery path and query");
            AssertContains(
                requestHeaders,
                "Authorization: Bearer test-key",
                "Model discovery bearer authorization");
            AssertContains(
                requestHeaders,
                "Accept: application/json",
                "Model discovery accept header");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestAnonymousRequestsDoNotSendAuthorization(
        Type clientType,
        Type requestType)
    {
        TcpListener modelsListener = StartListener();
        int modelsPort = GetPort(modelsListener);
        string modelsRequest = null;
        Task<int> modelsServer = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = modelsListener.AcceptTcpClient())
                {
                    modelsRequest = ReadRequestText(connection.GetStream());
                    SendResponse(
                        connection.GetStream(),
                        200,
                        "OK",
                        "application/json",
                        "{\"data\":[{\"id\":\"anonymous-model\"}]}",
                        null);
                }

                return 1;
            });
        object modelsClient = CreateClient(
            clientType,
            2000,
            1000,
            1,
            0,
            2000);
        try
        {
            IList models = FetchModels(
                clientType,
                modelsClient,
                "http://127.0.0.1:" + modelsPort +
                    "/v1/chat/completions",
                string.Empty,
                TimeSpan.FromSeconds(5));
            AssertEqual(1, Wait(modelsServer), "Anonymous model discovery count");
            AssertEqual(1, models.Count, "Anonymous model discovery result");
            AssertNotContains(
                modelsRequest,
                "Authorization:",
                "Anonymous model discovery omits authorization");
        }
        finally
        {
            ((IDisposable)modelsClient).Dispose();
            modelsListener.Stop();
        }

        TcpListener chatListener = StartListener();
        int chatPort = GetPort(chatListener);
        string chatRequest = null;
        Task<int> chatServer = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = chatListener.AcceptTcpClient())
                {
                    chatRequest = ReadRequestText(connection.GetStream());
                    SendResponse(
                        connection.GetStream(),
                        200,
                        "OK",
                        "text/event-stream",
                        "data: {\"choices\":[{\"delta\":{\"content\":\"anonymous-ok\"}}]}\r\n\r\n" +
                            "data: [DONE]\r\n\r\n",
                        null);
                }

                return 1;
            });
        object chatClient = CreateClient(
            clientType,
            2000,
            1000,
            1,
            0,
            2000);
        try
        {
            object request = CreateRequest(
                requestType,
                "http://127.0.0.1:" + chatPort +
                    "/v1/chat/completions");
            requestType.GetProperty("ApiKey").SetValue(
                request,
                string.Empty,
                null);
            StringBuilder delta = new StringBuilder();
            string result = Generate(
                clientType,
                chatClient,
                request,
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertEqual(1, Wait(chatServer), "Anonymous chat request count");
            AssertEqual("anonymous-ok", result, "Anonymous chat result");
            AssertNotContains(
                chatRequest,
                "Authorization:",
                "Anonymous chat omits authorization");
        }
        finally
        {
            ((IDisposable)chatClient).Dispose();
            chatListener.Stop();
        }
    }

    private static void TestModelTrafficBypassesSystemProxy(
        Type clientType,
        Type requestType)
    {
        IWebProxy previousProxy = WebRequest.DefaultWebProxy;
        RejectingProxy proxy = new RejectingProxy();
        WebRequest.DefaultWebProxy = proxy;
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = Task.Factory.StartNew(
            delegate
            {
                int requests = 0;
                for (int index = 0; index < 2; index++)
                {
                    using (TcpClient connection = listener.AcceptTcpClient())
                    {
                        string request = ReadRequestText(
                            connection.GetStream());
                        requests++;
                        if (request.StartsWith(
                            "GET ",
                            StringComparison.Ordinal))
                        {
                            SendResponse(
                                connection.GetStream(),
                                200,
                                "OK",
                                "application/json",
                                "{\"data\":[{\"id\":\"direct-model\"}]}",
                                null);
                        }
                        else
                        {
                            SendResponse(
                                connection.GetStream(),
                                200,
                                "OK",
                                "text/event-stream",
                                "data: {\"choices\":[{\"delta\":{\"content\":\"direct\"}}]}\r\n\r\n" +
                                    "data: [DONE]\r\n\r\n",
                                null);
                        }
                    }
                }
                return requests;
            });
        object client = CreateClient(
            clientType,
            2000,
            1000,
            1,
            0,
            2000);
        try
        {
            string endpoint = "http://127.0.0.1:" + port +
                "/v1/chat/completions";
            IList models = FetchModels(
                clientType,
                client,
                endpoint,
                string.Empty,
                TimeSpan.FromSeconds(5));
            AssertEqual(1, models.Count, "Direct model discovery result");
            StringBuilder delta = new StringBuilder();
            string result = Generate(
                clientType,
                client,
                CreateRequest(requestType, endpoint),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertEqual("direct", result, "Direct model response");
            AssertEqual(2, Wait(server), "Direct model request count");
            AssertEqual(0, proxy.CallCount, "System proxy bypass count");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
            WebRequest.DefaultWebProxy = previousProxy;
        }
    }

    private static void TestUnknownModelEndpointIsRejected(Type clientType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        object client = CreateClient(clientType, 1000, 1000, 1, 0, 1000);
        try
        {
            Exception failure = FetchModelsFailure(
                clientType,
                client,
                "http://127.0.0.1:" + port + "/custom/generate",
                "test-key",
                TimeSpan.FromSeconds(2));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                "Unknown model endpoint failure type");
            AssertContains(
                failure.Message,
                "无法从当前请求 URL 推导",
                "Unknown model endpoint guidance");
            Thread.Sleep(100);
            AssertTrue(
                !listener.Pending(),
                "Unknown model endpoint rejected before network access");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestModelDiscoveryDoesNotFollowRedirects(Type clientType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                {
                    ReadRequest(connection.GetStream());
                    SendResponse(
                        connection.GetStream(),
                        302,
                        "Found",
                        "application/json",
                        "{\"error\":{\"message\":\"redirected\"}}",
                        "Location: http://127.0.0.1:" + port +
                            "/captured/v1/models\r\n");
                }

                Thread.Sleep(200);
                return listener.Pending() ? 2 : 1;
            });
        object client = CreateClient(clientType, 2000, 1000, 3, 0, 2000);
        try
        {
            Exception failure = FetchModelsFailure(
                clientType,
                client,
                "http://127.0.0.1:" + port + "/v1/chat/completions",
                "test-key",
                TimeSpan.FromSeconds(5));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                "Model redirect failure type");
            AssertContains(
                failure.Message,
                "没有跟随重定向",
                "Model redirect guidance");
            AssertEqual(1, Wait(server), "Model redirect request count");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestNonStandardModelResponsesAreRejected(Type clientType)
    {
        string[] bodies =
        {
            "[{\"id\":\"array-model\"}]",
            "{\"models\":[{\"id\":\"alternate-model\"}]}",
            "{\"data\":{\"id\":\"not-an-array\"}}"
        };
        foreach (string body in bodies)
        {
            TcpListener listener = StartListener();
            int port = GetPort(listener);
            Task<int> server = Task.Factory.StartNew(
                delegate
                {
                    using (TcpClient connection = listener.AcceptTcpClient())
                    {
                        ReadRequest(connection.GetStream());
                        SendResponse(
                            connection.GetStream(),
                            200,
                            "OK",
                            "application/json",
                            body,
                            null);
                    }

                    return 1;
                });
            object client = CreateClient(clientType, 2000, 1000, 1, 0, 2000);
            try
            {
                Exception failure = FetchModelsFailure(
                    clientType,
                    client,
                    "http://127.0.0.1:" + port + "/v1/responses",
                    "test-key",
                    TimeSpan.FromSeconds(5));
                AssertType(
                    failure,
                    "FilePromptAIWin7.ModelCallException",
                    "Non-standard model response failure type");
                AssertContains(
                    failure.Message,
                    "没有可用的模型 ID",
                    "Non-standard model response guidance");
                AssertEqual(1, Wait(server), "Non-standard model response request count");
            }
            finally
            {
                ((IDisposable)client).Dispose();
                listener.Stop();
            }
        }
    }

    private static void TestAvailableModelsPreserveManualSelection(
        Assembly application)
    {
        Type settingsType = application.GetType(
            "FilePromptAIWin7.SettingsDialog",
            true);
        object settings = Activator.CreateInstance(settingsType, true);
        try
        {
            object modelSelector = settingsType.GetProperty("ModelTextBox")
                .GetValue(settings, null);
            PropertyInfo textProperty = modelSelector.GetType().GetProperty("Text");
            textProperty.SetValue(modelSelector, "manual-model", null);
            SetAvailableModels(
                settingsType,
                settings,
                new[] { "alpha", "beta" });
            AssertEqual(
                "manual-model",
                Convert.ToString(textProperty.GetValue(modelSelector, null)),
                "Fetched models preserve manual model");

            textProperty.SetValue(modelSelector, string.Empty, null);
            SetAvailableModels(
                settingsType,
                settings,
                new[] { "alpha", "beta" });
            AssertEqual(
                string.Empty,
                Convert.ToString(textProperty.GetValue(modelSelector, null)),
                "Fetched models do not auto-select first model");
        }
        finally
        {
            ((IDisposable)settings).Dispose();
        }
    }

    private static void SetAvailableModels(
        Type settingsType,
        object settings,
        IList<string> models)
    {
        MethodInfo method = settingsType.GetMethod(
            "SetAvailableModels",
            BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
        {
            throw new MissingMethodException(
                settingsType.FullName,
                "SetAvailableModels");
        }

        method.Invoke(settings, new object[] { models });
    }

    private static void TestRetriableStatusCodes(
        Type clientType,
        Type requestType)
    {
        int[] statusCodes = { 429, 502, 503, 504 };
        foreach (int statusCode in statusCodes)
        {
            TcpListener listener = StartListener();
            int port = GetPort(listener);
            Task<int> server = Task.Factory.StartNew(
                delegate
                {
                    int requests = 0;
                    using (TcpClient first = listener.AcceptTcpClient())
                    {
                        requests++;
                        ReadRequest(first.GetStream());
                        SendResponse(
                            first.GetStream(),
                            statusCode,
                            "Retry",
                            "application/json",
                            "{\"error\":{\"message\":\"temporary\"}}",
                            null);
                    }

                    using (TcpClient second = listener.AcceptTcpClient())
                    {
                        requests++;
                        ReadRequest(second.GetStream());
                        SendResponse(
                            second.GetStream(),
                            200,
                            "OK",
                            "application/json",
                            "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                            null);
                    }

                    return requests;
                });
            object client = CreateClient(clientType, 2000, 1000, 2, 10, 2000);
            try
            {
                StringBuilder delta = new StringBuilder();
                string result = Generate(
                    clientType,
                    client,
                    CreateRequest(
                        requestType,
                        "http://127.0.0.1:" + port + "/retry-" + statusCode),
                    delta,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(5));
                AssertEqual("ok", result, "HTTP " + statusCode + " result");
                AssertEqual("ok", delta.ToString(), "HTTP " + statusCode + " delta once");
                AssertEqual(2, Wait(server), "HTTP " + statusCode + " retried once");
            }
            finally
            {
                ((IDisposable)client).Dispose();
                listener.Stop();
            }
        }
    }

    private static void TestAttachmentStatusCodesDoNotRetry(
        Type clientType,
        Type requestType,
        Type itemType)
    {
        int[] statusCodes = { 429, 502, 503, 504 };
        foreach (int statusCode in statusCodes)
        {
            TcpListener listener = StartListener();
            int port = GetPort(listener);
            Task<int> server = CountErrorRequests(listener, statusCode);
            object client = CreateClient(
                clientType,
                2000,
                1000,
                3,
                10,
                2000);
            try
            {
                object request = CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port +
                        "/attachment-status-" + statusCode);
                SetBinaryAttachment(requestType, request, itemType);
                Exception failure = GenerateFailure(
                    clientType,
                    client,
                    request,
                    new StringBuilder(),
                    CancellationToken.None,
                    TimeSpan.FromSeconds(5));
                AssertType(
                    failure,
                    "FilePromptAIWin7.ModelCallException",
                    "Attachment HTTP " + statusCode + " failure type");
                AssertEqual(
                    1,
                    Wait(server),
                    "Attachment HTTP " + statusCode +
                        " submitted once");
            }
            finally
            {
                ((IDisposable)client).Dispose();
                listener.Stop();
            }
        }
    }

    private static void TestAttachmentStreamFallbackDoesNotRetry(
        Type clientType,
        Type requestType,
        Type itemType)
    {
        int[] statusCodes = { 400, 422 };
        foreach (int statusCode in statusCodes)
        {
            TcpListener listener = StartListener();
            int port = GetPort(listener);
            Task<int> server = CountErrorRequests(
                listener,
                statusCode,
                "{\"error\":{\"message\":\"stream unsupported\"}}");
            object client = CreateClient(
                clientType,
                2000,
                1000,
                3,
                10,
                2000);
            try
            {
                object request = CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port +
                        "/attachment-stream-fallback-" + statusCode);
                SetBinaryAttachment(requestType, request, itemType);
                Exception failure = GenerateFailure(
                    clientType,
                    client,
                    request,
                    new StringBuilder(),
                    CancellationToken.None,
                    TimeSpan.FromSeconds(5));
                AssertType(
                    failure,
                    "FilePromptAIWin7.ModelCallException",
                    "Attachment stream fallback HTTP " + statusCode +
                        " failure type");
                AssertContains(
                    failure.Message,
                    "没有自动改用普通请求",
                    "Attachment stream fallback HTTP " + statusCode +
                        " guidance");
                AssertEqual(
                    1,
                    Wait(server),
                    "Attachment stream fallback HTTP " + statusCode +
                        " submitted once");
            }
            finally
            {
                ((IDisposable)client).Dispose();
                listener.Stop();
            }
        }
    }

    private static void TestTextStreamFallbackStillWorks(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = Task.Factory.StartNew(
            delegate
            {
                int requests = 0;
                using (TcpClient first = listener.AcceptTcpClient())
                {
                    requests++;
                    ReadRequest(first.GetStream());
                    SendResponse(
                        first.GetStream(),
                        400,
                        "Bad Request",
                        "application/json",
                        "{\"error\":{\"message\":\"stream unsupported\"}}",
                        null);
                }

                using (TcpClient second = listener.AcceptTcpClient())
                {
                    requests++;
                    ReadRequest(second.GetStream());
                    SendResponse(
                        second.GetStream(),
                        200,
                        "OK",
                        "application/json",
                        "{\"choices\":[{\"message\":{\"content\":\"fallback-ok\"}}]}",
                        null);
                }

                return requests;
            });
        object client = CreateClient(clientType, 2000, 1000, 3, 10, 2000);
        try
        {
            StringBuilder delta = new StringBuilder();
            string result = Generate(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/text-stream-fallback"),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertEqual(
                "fallback-ok",
                result,
                "Text stream fallback result");
            AssertEqual(
                "fallback-ok",
                delta.ToString(),
                "Text stream fallback delta");
            AssertEqual(2, Wait(server), "Text stream fallback request count");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestToolAttachmentStatusCodesDoNotRetry(
        Type clientType,
        Type requestType,
        Type itemType)
    {
        int[] statusCodes = { 429, 502, 503, 504 };
        foreach (int statusCode in statusCodes)
        {
            TcpListener listener = StartListener();
            int port = GetPort(listener);
            Task<int> server = CountErrorRequests(listener, statusCode);
            object client = CreateClient(
                clientType,
                2000,
                1000,
                3,
                10,
                2000);
            try
            {
                string endpoint = "http://127.0.0.1:" + port +
                    "/tool-attachment-status-" + statusCode;
                object request = CreateRequest(requestType, endpoint);
                SetBinaryAttachment(requestType, request, itemType);
                Exception failure = InvokeToolAttemptFailure(
                    clientType,
                    client,
                    request,
                    endpoint,
                    TimeSpan.FromSeconds(5));
                AssertType(
                    failure,
                    clientType.FullName + "+AttemptException",
                    "Tool attachment HTTP " + statusCode +
                        " failure type");
                AssertEqual(
                    1,
                    Wait(server),
                    "Tool attachment HTTP " + statusCode +
                        " submitted once");
            }
            finally
            {
                ((IDisposable)client).Dispose();
                listener.Stop();
            }
        }
    }

    private static Task<int> CountErrorRequests(
        TcpListener listener,
        int statusCode)
    {
        return CountErrorRequests(
            listener,
            statusCode,
            "{\"error\":{\"message\":\"temporary\"}}");
    }

    private static Task<int> CountErrorRequests(
        TcpListener listener,
        int statusCode,
        string errorBody)
    {
        return Task.Factory.StartNew(
            delegate
            {
                int requests = 0;
                using (TcpClient first = listener.AcceptTcpClient())
                {
                    requests++;
                    ReadRequest(first.GetStream());
                    SendResponse(
                        first.GetStream(),
                        statusCode,
                        "Retry",
                        "application/json",
                        errorBody,
                        null);
                }

                Stopwatch observation = Stopwatch.StartNew();
                while (observation.Elapsed < TimeSpan.FromMilliseconds(350))
                {
                    if (!listener.Pending())
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    using (TcpClient duplicate = listener.AcceptTcpClient())
                    {
                        requests++;
                        ReadRequest(duplicate.GetStream());
                        SendResponse(
                            duplicate.GetStream(),
                            statusCode,
                            "Retry",
                            "application/json",
                            errorBody,
                            null);
                    }
                }

                return requests;
            });
    }

    private static void TestRetryAfterIsHonored(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = Task.Factory.StartNew(
            delegate
            {
                int requests = 0;
                using (TcpClient first = listener.AcceptTcpClient())
                {
                    requests++;
                    ReadRequest(first.GetStream());
                    SendResponse(
                        first.GetStream(),
                        429,
                        "Too Many Requests",
                        "application/json",
                        "{\"error\":{\"message\":\"wait\"}}",
                        "Retry-After: 1\r\n");
                }

                using (TcpClient second = listener.AcceptTcpClient())
                {
                    requests++;
                    ReadRequest(second.GetStream());
                    SendResponse(
                        second.GetStream(),
                        200,
                        "OK",
                        "application/json",
                        "{\"choices\":[{\"message\":{\"content\":\"after-wait\"}}]}",
                        null);
                }

                return requests;
            });
        object client = CreateClient(clientType, 2000, 1000, 2, 10, 2000);
        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string result = Generate(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/retry-after"),
                new StringBuilder(),
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            stopwatch.Stop();
            AssertEqual("after-wait", result, "Retry-After result");
            AssertTrue(
                stopwatch.Elapsed >= TimeSpan.FromMilliseconds(850),
                "Retry-After delay honored");
            AssertEqual(2, Wait(server), "Retry-After request count");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestLongRetryAfterIsNotIgnored(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task server = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                {
                    ReadRequest(connection.GetStream());
                    SendResponse(
                        connection.GetStream(),
                        503,
                        "Unavailable",
                        "application/json",
                        "{\"error\":{\"message\":\"come back later\"}}",
                        "Retry-After: 5\r\n");
                }
            });
        object client = CreateClient(clientType, 2000, 1000, 2, 10, 250);
        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Exception failure = GenerateFailure(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/long-retry-after"),
                new StringBuilder(),
                CancellationToken.None,
                TimeSpan.FromSeconds(3));
            stopwatch.Stop();
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                "Long Retry-After failure type");
            AssertTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                "Long Retry-After does not retry early");
            server.Wait(TimeSpan.FromSeconds(2));
            Thread.Sleep(200);
            AssertTrue(!listener.Pending(), "Long Retry-After request not duplicated");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestResponseHeadersTimeoutRetries(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = Task.Factory.StartNew(
            delegate
            {
                int requests = 0;
                TcpClient stalled = listener.AcceptTcpClient();
                requests++;
                ReadRequest(stalled.GetStream());
                Task stalledConnection = Task.Factory.StartNew(
                    delegate
                    {
                        using (stalled)
                        {
                            Thread.Sleep(1500);
                        }
                    });

                using (TcpClient second = listener.AcceptTcpClient())
                {
                    requests++;
                    ReadRequest(second.GetStream());
                    SendResponse(
                        second.GetStream(),
                        200,
                        "OK",
                        "application/json",
                        "{\"choices\":[{\"message\":{\"content\":\"recovered\"}}]}",
                        null);
                }

                stalledConnection.Wait(TimeSpan.FromSeconds(2));
                return requests;
            });
        object client = CreateClient(clientType, 500, 1000, 2, 10, 1000);
        try
        {
            string result = Generate(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/headers-timeout"),
                new StringBuilder(),
                CancellationToken.None,
                TimeSpan.FromSeconds(4));
            AssertEqual("recovered", result, "Response headers timeout recovery");
            AssertEqual(2, Wait(server), "Response headers timeout retried once");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestAttachmentHeadersTimeoutDoesNotRetry(
        Type clientType,
        Type requestType,
        Type itemType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task server = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                {
                    ReadRequest(connection.GetStream());
                    Thread.Sleep(800);
                }
            });
        object client = CreateClientWithAttachmentTimeout(
            clientType,
            500,
            1000,
            3,
            10,
            1000,
            150);
        try
        {
            object request = CreateRequest(
                requestType,
                "http://127.0.0.1:" + port +
                    "/attachment-headers-timeout");
            SetBinaryAttachment(requestType, request, itemType);
            Exception failure = GenerateFailure(
                clientType,
                client,
                request,
                new StringBuilder(),
                CancellationToken.None,
                TimeSpan.FromSeconds(3));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                "Attachment headers timeout type");
            AssertContains(
                failure.Message,
                "超时",
                "Attachment headers timeout guidance");
            AssertContains(
                failure.Message,
                "没有自动重新上传附件",
                "Attachment timeout explains no automatic retry");
            AssertContains(
                failure.Message,
                "timeout-image.png",
                "Attachment timeout names the affected file");
            Thread.Sleep(250);
            AssertTrue(
                !listener.Pending(),
                "Attachment headers timeout not retried");
            server.Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestStreamIdleTimeout(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        ManualResetEventSlim release = new ManualResetEventSlim(false);
        Task server = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                {
                    NetworkStream stream = connection.GetStream();
                    ReadRequest(stream);
                    SendOpenStream(
                        stream,
                        "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\r\n\r\n");
                    release.Wait(TimeSpan.FromSeconds(3));
                }
            });
        object client = CreateClient(clientType, 2000, 150, 2, 10, 1000);
        try
        {
            StringBuilder delta = new StringBuilder();
            Exception failure = GenerateFailure(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/idle-stream"),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(3));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                "Stream idle timeout type");
            AssertContains(failure.Message, "读取超时", "Stream idle timeout guidance");
            AssertEqual("partial", delta.ToString(), "Stream idle partial delta observed");
            Thread.Sleep(200);
            AssertTrue(!listener.Pending(), "Stream idle timeout not retried after delta");
        }
        finally
        {
            release.Set();
            server.Wait(TimeSpan.FromSeconds(3));
            release.Dispose();
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestConnectionEstablishmentFailureRetries(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = Task.Factory.StartNew(
            delegate
            {
                int requests = 0;
                for (int index = 0; index < 3; index++)
                {
                    using (TcpClient connection = listener.AcceptTcpClient())
                    {
                        requests++;
                        connection.Client.LingerState = new LingerOption(true, 0);
                    }
                }

                return requests;
            });
        object client = CreateClient(clientType, 2000, 1000, 3, 20, 1000);
        try
        {
            Exception failure = GenerateFailure(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/connection-reset"),
                new StringBuilder(),
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                "Connection establishment failure type");
            AssertEqual(3, Wait(server), "Connection establishment retries");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestIncompleteStreamFails(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = StartCountingStreamingServer(
            listener,
            "text/event-stream",
            "data: {\"choices\":[{\"delta\":{\"content\":\"cut-off\"}}]}\r\n\r\n",
            2,
            null);
        object client = CreateClient(clientType, 2000, 1000, 2, 10, 1000);
        try
        {
            StringBuilder delta = new StringBuilder();
            Exception failure = GenerateFailure(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/incomplete-stream"),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(3));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                "Incomplete stream failure type");
            AssertContains(failure.Message, "未完整结束", "Incomplete stream guidance");
            AssertEqual("cut-off", delta.ToString(), "Incomplete stream partial delta observed");
            AssertEqual(
                1,
                Wait(server),
                "Incomplete stream not retried after delta");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestIncompleteStreamBeforeTextDoesNotRetry(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = StartCountingStreamingServer(
            listener,
            "text/event-stream",
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\r\n\r\n",
            3,
            null);
        object client = CreateClient(clientType, 2000, 1000, 3, 10, 1000);
        try
        {
            StringBuilder delta = new StringBuilder();
            Exception failure = GenerateFailure(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port +
                        "/incomplete-stream-before-text"),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                "Incomplete pre-text stream failure type");
            AssertContains(
                failure.Message,
                "未完整结束",
                "Incomplete pre-text stream guidance");
            AssertEqual(
                string.Empty,
                delta.ToString(),
                "Incomplete pre-text stream emits no delta");
            AssertEqual(
                1,
                Wait(server),
                "Incomplete pre-text stream submitted once");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestStreamingProtocolMatrix(
        Type clientType,
        Type requestType)
    {
        RunSseSuccessCase(
            clientType,
            requestType,
            "Standard string content",
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"REASONING_SECRET\"}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"standard-\"}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"text\"}}]}\r\n\r\n" +
                "data: [DONE]\r\n\r\n",
            "standard-text");

        RunSseSuccessCase(
            clientType,
            requestType,
            "Responses event-name-only deltas",
            "event: response.output_text.delta\r\n" +
                "data: {\"delta\":\"responses-\"}\r\n\r\n" +
                "event: response.output_text.delta\r\n" +
                "data: {\"delta\":\"events\"}\r\n\r\n" +
                "event: response.completed\r\n" +
                "data: {\"response\":{\"status\":\"completed\",\"output\":[]}}\r\n\r\n",
            "responses-events");

        RunSseSuccessCase(
            clientType,
            requestType,
            "Choice delta.text fragments",
            "data: {\"choices\":[{\"delta\":{\"text\":\"choice-\"}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{\"text\":\"delta-text\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "choice-delta-text");

        RunSseSuccessCase(
            clientType,
            requestType,
            "Root delta.text filters non-text deltas",
            "event: content_block_delta\r\n" +
                "data: {\"delta\":{\"type\":\"thinking_delta\"," +
                "\"thinking\":\"REASONING_SECRET\"}}\r\n\r\n" +
                "event: content_block_delta\r\n" +
                "data: {\"delta\":{\"type\":\"input_json_delta\"," +
                "\"partial_json\":\"TOOL_SECRET\"}}\r\n\r\n" +
                "event: content_block_delta\r\n" +
                "data: {\"delta\":{\"type\":\"text_delta\"," +
                "\"text\":\"delta-\"}}\r\n\r\n" +
                "event: content_block_delta\r\n" +
                "data: {\"delta\":{\"type\":\"text_delta\"," +
                "\"text\":\"text\"}}\r\n\r\n" +
                "event: message_stop\r\n" +
                "data: {}\r\n\r\n",
            "delta-text");

        RunSseSuccessCase(
            clientType,
            requestType,
            "Array content filters non-text parts",
            "data: {\"choices\":[{\"delta\":{\"content\":[" +
                "{\"type\":\"reasoning\",\"text\":\"REASONING_SECRET\"}," +
                "{\"type\":\"tool_use\",\"text\":\"TOOL_SECRET\"}," +
                "{\"type\":\"input_text\",\"text\":\"INPUT_SECRET\"}," +
                "{\"type\":\"text\",\"text\":\"array-\"}," +
                "{\"type\":\"output_text\",\"text\":\"content\"}]}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "array-content");

        RunSseSuccessCase(
            clientType,
            requestType,
            "Completion snapshot does not duplicate prior delta",
            "data: {\"choices\":[{\"delta\":{\"content\":\"hel\"}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{}," +
                "\"message\":{\"content\":\"hello\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "hello");

        RunSseSuccessCase(
            clientType,
            requestType,
            "Completion delta wins over full snapshot",
            "data: {\"choices\":[{\"delta\":{\"content\":\"hel\"}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}," +
                "\"message\":{\"content\":\"hello\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "hello");

        RunSseSuccessCase(
            clientType,
            requestType,
            "Snapshot-only completion remains visible",
            "data: {\"choices\":[{\"delta\":{}," +
                "\"message\":{\"content\":\"snapshot-only\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "snapshot-only");

        RunNdjsonSuccessCase(
            clientType,
            requestType,
            "Ollama root message NDJSON fragments",
            "{\"model\":\"local\",\"message\":{\"role\":\"assistant\"," +
                "\"thinking\":\"REASONING_SECRET\",\"content\":\"ollama-\"," +
                "\"tool_calls\":[{\"function\":{\"name\":\"lookup\"," +
                "\"arguments\":{\"secret\":\"TOOL_SECRET\"}}}]}," +
                "\"done\":false}\n" +
                "{\"model\":\"local\",\"message\":{\"role\":\"assistant\"," +
                "\"content\":\"done\"},\"done\":false}\n" +
                "{\"model\":\"local\",\"message\":{\"role\":\"assistant\"," +
                "\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n",
            "ollama-done");

        RunNdjsonSuccessCase(
            clientType,
            requestType,
            "Ollama root response NDJSON fragments",
            "{\"model\":\"local\",\"response\":\"root-\"," +
                "\"thinking\":\"REASONING_SECRET\",\"done\":false}\n" +
                "{\"model\":\"local\",\"response\":\"response\"," +
                "\"done\":false}\n" +
                "{\"model\":\"local\",\"response\":\"\"," +
                "\"done\":true,\"done_reason\":\"stop\"}\n",
            "root-response");

        RunNdjsonSuccessCase(
            clientType,
            requestType,
            "Ollama repeated and prefixed message fragments append",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"ha\"}," +
                "\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"ha\"}," +
                "\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"a\"}," +
                "\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"ab\"}," +
                "\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\"}," +
                "\"done\":true}\n",
            "hahaaab");

        RunNdjsonSuccessCase(
            clientType,
            requestType,
            "Ollama cumulative completion snapshot is ignored",
            "{\"message\":{\"content\":\"ollama-\"},\"done\":false}\n" +
                "{\"message\":{\"content\":\"done\"},\"done\":false}\n" +
                "{\"message\":{\"content\":\"ollama-done\"}," +
                "\"done\":true,\"done_reason\":\"stop\"}\n",
            "ollama-done");

        RunNdjsonSuccessCase(
            clientType,
            requestType,
            "Single completed snapshot remains visible",
            "{\"response\":\"single-snapshot\",\"done\":true}\n",
            "single-snapshot");
    }

    private static void TestReasoningAndToolStreamDataDoNotLeak(
        Type clientType,
        Type requestType)
    {
        RunSseFailureCase(
            clientType,
            requestType,
            "Typed reasoning delta.text",
            "data: {\"choices\":[{\"delta\":{" +
                "\"type\":\"reasoning\",\"text\":\"REASONING_SECRET\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Typed tool delta.text",
            "data: {\"choices\":[{\"delta\":{" +
                "\"type\":\"input_json_delta\",\"text\":\"TOOL_SECRET\"}," +
                "\"finish_reason\":\"tool_calls\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Reasoning-only completed stream",
            "data: {\"choices\":[{\"delta\":{" +
                "\"reasoning_content\":\"REASONING_FIELD_SECRET\"}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":[" +
                "{\"type\":\"reasoning\"," +
                "\"text\":\"REASONING_PART_SECRET\"}]}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Tool-only completed stream",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{" +
                "\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"lookup\"," +
                "\"arguments\":\"{\\\"secret\\\":\\\"TOOL_SECRET\\\"}\"}}]}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{}," +
                "\"finish_reason\":\"tool_calls\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Hidden typed part blocks fallback",
            "data: {\"choices\":[{\"delta\":{\"content\":[{" +
                "\"type\":\"thinking\",\"text\":\"REASONING_SECRET\"}]}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Analysis typed part blocks fallback",
            "data: {\"choices\":[{\"delta\":{\"content\":[{" +
                "\"type\":\"analysis\",\"data\":\"REASONING_SECRET\"}]}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Encrypted typed part blocks fallback",
            "data: {\"choices\":[{\"delta\":{\"content\":[{" +
                "\"type\":\"encrypted_content\"," +
                "\"encrypted_content\":\"REASONING_SECRET\"}]}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Tool typed part blocks fallback",
            "data: {\"choices\":[{\"delta\":{\"content\":[{" +
                "\"type\":\"tool_use\",\"name\":\"lookup\"," +
                "\"input\":{\"value\":\"TOOL_SECRET\"}}]}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Function typed part blocks fallback",
            "data: {\"choices\":[{\"delta\":{\"content\":[{" +
                "\"type\":\"function\",\"arguments\":{" +
                "\"type\":\"TOOL_SECRET\"}}]}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Input JSON typed delta blocks fallback",
            "data: {\"choices\":[{\"delta\":{" +
                "\"type\":\"input_json_delta\"," +
                "\"partial_json\":\"TOOL_SECRET\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Reasoning completion snapshot does not leak",
            "data: {\"type\":\"response.reasoning.done\"," +
                "\"completed\":true," +
                "\"output_text\":\"REASONING_SECRET\"}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Tool completion snapshot does not leak or retry",
            "event: response.function_call_arguments.done\r\n" +
                "data: {\"done\":true,\"done_reason\":\"stop\"," +
                "\"message\":{\"content\":\"TOOL_SECRET\"}}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Untyped root reasoning delta blocks fallback",
            "data: {\"delta\":{\"thinking\":\"REASONING_SECRET\"}," +
                "\"done\":true,\"done_reason\":\"stop\"}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Untyped root tool delta blocks fallback",
            "data: {\"delta\":{\"tool_calls\":[{" +
                "\"function\":{\"arguments\":\"TOOL_SECRET\"}}]}," +
                "\"done\":true,\"done_reason\":\"stop\"}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Root refusal blocks fallback",
            "data: {\"refusal\":\"REFUSAL_SECRET\"," +
                "\"done\":true,\"done_reason\":\"stop\"}\r\n\r\n",
            "拒绝",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Root message refusal blocks fallback",
            "data: {\"message\":{\"content\":\"\"," +
                "\"refusal\":\"REFUSAL_SECRET\"}," +
                "\"done\":true,\"done_reason\":\"stop\"}\r\n\r\n",
            "拒绝",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Tool event-name payload blocks fallback",
            "event: response.function_call_arguments.delta\r\n" +
                "data: {\"delta\":\"TOOL_SECRET\"}\r\n\r\n" +
                "data: [DONE]\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Reasoning event name filters choice content",
            "event: response.reasoning.delta\r\n" +
                "data: {\"choices\":[{\"delta\":{" +
                "\"content\":\"REASONING_SECRET\"}}]}\r\n\r\n" +
                "data: [DONE]\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Tool event name filters root delta text",
            "event: content_block_delta.function_call\r\n" +
                "data: {\"delta\":{\"text\":\"TOOL_SECRET\"}}\r\n\r\n" +
                "data: [DONE]\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Root message typed tool blocks fallback",
            "data: {\"message\":{\"content\":[{" +
                "\"type\":\"tool_use\",\"name\":\"lookup\"," +
                "\"input\":{\"value\":\"TOOL_SECRET\"}}]}," +
                "\"done\":true,\"done_reason\":\"stop\"}\r\n\r\n",
            "没有返回",
            string.Empty);

        RunSseSuccessCase(
            clientType,
            requestType,
            "Chat reasoning fields do not leak",
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"REASONING_FIELD_SECRET\"}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"visible-text\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "visible-text");

        RunSseSuccessCase(
            clientType,
            requestType,
            "Chat tool fragments do not leak",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{" +
                "\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"lookup\",\"arguments\":\"{\\\"secret\\\":\\\"TOOL_SECRET\\\"}\"}}]}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"tool-safe-text\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "tool-safe-text");
    }

    private static void TestStreamingResponseLimits(
        Type clientType,
        Type requestType)
    {
        TestOversizedStreamingContentLength(
            clientType,
            requestType,
            "text/event-stream");
        TestOversizedStreamingContentLength(
            clientType,
            requestType,
            "application/x-ndjson");
    }

    private static void TestOversizedStreamingContentLength(
        Type clientType,
        Type requestType,
        string contentType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = Task.Factory.StartNew(
            delegate
            {
                int requests = 0;
                while (requests < 2)
                {
                    if (requests > 0 && !WaitForPendingConnection(listener, 500))
                    {
                        break;
                    }
                    using (TcpClient connection = listener.AcceptTcpClient())
                    {
                        requests++;
                        NetworkStream stream = connection.GetStream();
                        ReadRequest(stream);
                        string headers =
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: " + contentType + "; charset=utf-8\r\n" +
                            "Content-Length: 16777217\r\n" +
                            "Connection: close\r\n\r\n";
                        byte[] bytes = Encoding.ASCII.GetBytes(headers);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                }
                return requests;
            });
        object client = CreateClient(clientType, 2000, 1000, 2, 10, 1000);
        try
        {
            StringBuilder delta = new StringBuilder();
            Exception failure = GenerateFailure(
                clientType,
                client,
                CreateRequest(requestType, "http://127.0.0.1:" + port +
                    "/oversized-stream"),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                contentType + " oversized content length type");
            AssertContains(
                failure.Message,
                "超过 8 MB",
                contentType + " oversized content length guidance");
            AssertEqual(
                string.Empty,
                delta.ToString(),
                contentType + " oversized content length delta");
            AssertEqual(
                1,
                Wait(server),
                contentType + " oversized content length request count");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestStreamingErrorEvents(
        Type clientType,
        Type requestType)
    {
        RunSseFailureCase(
            clientType,
            requestType,
            "Standard SSE error event",
            "event: error\r\n" +
                "data: {\"type\":\"error\",\"error\":{\"message\":\"standard-stream-error\"}}\r\n\r\n",
            "standard-stream-error",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Empty Responses failure event",
            "event: response.failed\r\n" +
                "data: {}\r\n\r\n",
            "流式错误事件",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Responses nested error event",
            "event: response.failed\r\n" +
                "data: {\"response\":{\"error\":{" +
                "\"message\":\"responses-nested-error\"}}}\r\n\r\n",
            "responses-nested-error",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Error after visible text",
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial-visible\"}}]}\r\n\r\n" +
                "event: error\r\n" +
                "data: {\"type\":\"error\",\"error\":{\"message\":\"late-stream-error\"}}\r\n\r\n",
            "late-stream-error",
            "partial-visible");
    }

    private static void TestEmptyCompletedTextStreamFallsBack(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        string firstRequest = null;
        string secondRequest = null;
        Task<int> server = Task.Factory.StartNew(
            delegate
            {
                int requests = 0;
                using (TcpClient first = listener.AcceptTcpClient())
                {
                    requests++;
                    firstRequest = ReadRequestText(first.GetStream());
                    SendResponse(
                        first.GetStream(),
                        200,
                        "OK",
                        "text/event-stream",
                        "data: [DONE]\r\n\r\n",
                        null);
                }

                while (requests < 3 &&
                    WaitForPendingConnection(
                        listener,
                        requests == 1 ? 2000 : 500))
                {
                    using (TcpClient fallback = listener.AcceptTcpClient())
                    {
                        requests++;
                        string request = ReadRequestText(
                            fallback.GetStream());
                        if (requests == 2)
                        {
                            secondRequest = request;
                        }

                        SendResponse(
                            fallback.GetStream(),
                            200,
                            "OK",
                            "application/json",
                            "{\"choices\":[{\"message\":{\"content\":\"empty-stream-fallback\"}}]}",
                            null);
                    }
                }

                return requests;
            });
        object client = CreateClient(clientType, 2000, 1000, 2, 10, 1000);
        try
        {
            StringBuilder delta = new StringBuilder();
            string result = Generate(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/empty-stream-fallback"),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertEqual(
                "empty-stream-fallback",
                result,
                "Empty completed text stream fallback result");
            AssertEqual(
                "empty-stream-fallback",
                delta.ToString(),
                "Empty completed text stream fallback delta");
            AssertEqual(
                2,
                Wait(server),
                "Empty completed text stream request count");
            AssertContains(
                firstRequest,
                "\"stream\":true",
                "Empty completed text stream first request streams");
            AssertContains(
                secondRequest,
                "\"stream\":false",
                "Empty completed text stream fallback disables streaming");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestEmptyCompletedAttachmentStreamDoesNotRetry(
        Type clientType,
        Type requestType,
        Type itemType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        string requestText = null;
        Task<int> server = StartCountingSseServer(
            listener,
            "data: [DONE]\r\n\r\n",
            2,
            delegate(string value, int requestNumber)
            {
                if (requestNumber == 1)
                {
                    requestText = value;
                }
            });
        object client = CreateClient(clientType, 2000, 1000, 3, 10, 1000);
        try
        {
            object request = CreateRequest(
                requestType,
                "http://127.0.0.1:" + port +
                    "/empty-attachment-stream");
            SetBinaryAttachment(requestType, request, itemType);
            StringBuilder delta = new StringBuilder();
            Exception failure = GenerateFailure(
                clientType,
                client,
                request,
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                "Empty completed attachment stream failure type");
            AssertContains(
                failure.Message,
                "为避免重复上传",
                "Empty completed attachment stream guidance");
            AssertEqual(
                string.Empty,
                delta.ToString(),
                "Empty completed attachment stream emits no delta");
            AssertEqual(
                1,
                Wait(server),
                "Empty completed attachment stream submitted once");
            AssertContains(
                requestText,
                "\"stream\":true",
                "Empty completed attachment request streams once");
            AssertContains(
                requestText,
                "data:image/png;base64,AQIDBA==",
                "Empty completed attachment was sent on first request");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestStructuredEmptyCompletedTextStreamFallsBack(
        Type clientType,
        Type requestType)
    {
        string[] emptyBodies =
        {
            "data: {\"choices\":[{\"delta\":{}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "data: {\"message\":{\"role\":\"assistant\"," +
                "\"content\":\"\"},\"done\":true}\r\n\r\n",
            "data: {\"choices\":[{\"delta\":{" +
                "\"reasoning_content\":\"\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "data: {\"choices\":[{\"delta\":{" +
                "\"tool_calls\":[]}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "data: {\"choices\":[{\"delta\":{" +
                "\"refusal\":\"\"}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "data: {\"choices\":[{\"delta\":{\"content\":[{" +
                "\"type\":\"reasoning\",\"text\":\"\"},{" +
                "\"type\":\"thinking\",\"content\":\"\"},{" +
                "\"type\":\"analysis\",\"data\":\"\"},{" +
                "\"type\":\"encrypted_content\"," +
                "\"encrypted_content\":\"\"},{" +
                "\"type\":\"tool_use\",\"name\":\"\"," +
                "\"input\":{}},{\"type\":\"function\"," +
                "\"arguments\":{}},{\"type\":\"input_json_delta\"," +
                "\"partial_json\":\"\"}]}," +
                "\"finish_reason\":\"stop\"}]}\r\n\r\n",
            "event: response.reasoning.delta\r\n" +
                "data: {\"delta\":\"\"}\r\n\r\n" +
                "data: [DONE]\r\n\r\n",
            "event: response.function_call_arguments.delta\r\n" +
                "data: {\"delta\":{\"type\":\"input_json_delta\"," +
                "\"partial_json\":\"\"}}\r\n\r\n" +
                "data: [DONE]\r\n\r\n",
            "data: {\"delta\":{\"reasoning_content\":\"\"}," +
                "\"done\":true,\"done_reason\":\"stop\"}\r\n\r\n",
            "data: {\"delta\":{\"tool_calls\":[]}," +
                "\"done\":true,\"done_reason\":\"stop\"}\r\n\r\n"
        };
        foreach (string emptyBody in emptyBodies)
        {
            TcpListener listener = StartListener();
            int port = GetPort(listener);
            Task<int> server = Task.Factory.StartNew(
                delegate
                {
                    int requests = 0;
                    using (TcpClient first = listener.AcceptTcpClient())
                    {
                        requests++;
                        ReadRequest(first.GetStream());
                        SendResponse(
                            first.GetStream(),
                            200,
                            "OK",
                            "text/event-stream",
                            emptyBody,
                            null);
                    }

                    if (WaitForPendingConnection(listener, 2000))
                    {
                        using (TcpClient second = listener.AcceptTcpClient())
                        {
                            requests++;
                            string request = ReadRequestText(second.GetStream());
                            AssertContains(
                                request,
                                "\"stream\":false",
                                "Structured empty fallback disables streaming");
                            SendResponse(
                                second.GetStream(),
                                200,
                                "OK",
                                "application/json",
                                "{\"choices\":[{\"message\":{" +
                                    "\"content\":\"structured-empty-fallback\"}}]}",
                                null);
                        }
                    }

                    return requests;
                });
            object client = CreateClient(
                clientType,
                2000,
                1000,
                2,
                10,
                1000);
            try
            {
                StringBuilder delta = new StringBuilder();
                string result = Generate(
                    clientType,
                    client,
                    CreateRequest(
                        requestType,
                        "http://127.0.0.1:" + port +
                            "/structured-empty-fallback"),
                    delta,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(5));
                AssertEqual(
                    "structured-empty-fallback",
                    result,
                    "Structured empty fallback result");
                AssertEqual(
                    "structured-empty-fallback",
                    delta.ToString(),
                    "Structured empty fallback delta");
                AssertEqual(
                    2,
                    Wait(server),
                    "Structured empty fallback request count");
            }
            finally
            {
                ((IDisposable)client).Dispose();
                listener.Stop();
            }
        }
    }

    private static void TestSemanticEmptyCompletionDoesNotFallback(
        Type clientType,
        Type requestType)
    {
        RunSseFailureCase(
            clientType,
            requestType,
            "Content-filtered empty completion",
            "data: {\"choices\":[{\"delta\":{}," +
                "\"finish_reason\":\"content_filter\"}]}\r\n\r\n",
            "非普通状态",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Length-limited empty completion",
            "data: {\"choices\":[{\"delta\":{}," +
                "\"finish_reason\":\"length\"}]}\r\n\r\n",
            "非普通状态",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Tool-call empty completion",
            "data: {\"choices\":[{\"delta\":{}," +
                "\"finish_reason\":\"tool_calls\"}]}\r\n\r\n",
            "非普通状态",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Legacy function-call empty completion",
            "data: {\"choices\":[{\"delta\":{}," +
                "\"finish_reason\":\"function_call\"}]}\r\n\r\n",
            "非普通状态",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Non-stop done reason",
            "data: {\"message\":{\"content\":\"\"}," +
                "\"done\":true,\"done_reason\":\"length\"}\r\n\r\n",
            "非普通状态",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Unknown termination reason is redacted",
            "data: {\"choices\":[{\"delta\":{}," +
                "\"finish_reason\":\"REASONING_SECRET\\r\\n伪造诊断\"}]}\r\n\r\n",
            "结束原因：other",
            string.Empty);

        RunSseFailureCase(
            clientType,
            requestType,
            "Unnamed Responses completion",
            "event: response.completed\r\n" +
                "data: {\"response\":{\"status\":\"completed\"," +
                "\"output\":[]}}\r\n\r\n",
            "没有返回",
            string.Empty);
    }

    private static void RunSseSuccessCase(
        Type clientType,
        Type requestType,
        string name,
        string body,
        string expected)
    {
        RunStreamingSuccessCase(
            clientType,
            requestType,
            name,
            "text/event-stream",
            body,
            expected);
    }

    private static void RunNdjsonSuccessCase(
        Type clientType,
        Type requestType,
        string name,
        string body,
        string expected)
    {
        RunStreamingSuccessCase(
            clientType,
            requestType,
            name,
            "application/x-ndjson",
            body,
            expected);
    }

    private static void RunStreamingSuccessCase(
        Type clientType,
        Type requestType,
        string name,
        string contentType,
        string body,
        string expected)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = StartCountingStreamingServer(
            listener,
            contentType,
            body,
            2,
            null);
        object client = CreateClient(clientType, 2000, 1000, 2, 10, 1000);
        try
        {
            StringBuilder delta = new StringBuilder();
            string result = Generate(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port +
                        (contentType == "text/event-stream"
                            ? "/sse-success"
                            : "/ndjson-success")),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertEqual(expected, result, name + " result");
            AssertEqual(expected, delta.ToString(), name + " delta");
            AssertNotContains(result, "SECRET", name + " hides non-text data");
            AssertEqual(1, Wait(server), name + " request count");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void RunSseFailureCase(
        Type clientType,
        Type requestType,
        string name,
        string body,
        string expectedMessage,
        string expectedDelta)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task<int> server = StartCountingSseServer(listener, body, 2, null);
        object client = CreateClient(clientType, 2000, 1000, 2, 10, 1000);
        try
        {
            StringBuilder delta = new StringBuilder();
            Exception failure = GenerateFailure(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/sse-failure"),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
            AssertType(
                failure,
                "FilePromptAIWin7.ModelCallException",
                name + " failure type");
            AssertContains(failure.Message, expectedMessage, name + " guidance");
            AssertNotContains(failure.Message, "SECRET", name + " hides secret in error");
            AssertEqual(expectedDelta, delta.ToString(), name + " delta");
            AssertEqual(1, Wait(server), name + " request count");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static Task<int> StartCountingSseServer(
        TcpListener listener,
        string body,
        int maximumConnections,
        Action<string, int> onRequest)
    {
        return StartCountingStreamingServer(
            listener,
            "text/event-stream",
            body,
            maximumConnections,
            onRequest);
    }

    private static Task<int> StartCountingStreamingServer(
        TcpListener listener,
        string contentType,
        string body,
        int maximumConnections,
        Action<string, int> onRequest)
    {
        return Task.Factory.StartNew(
            delegate
            {
                int requests = 0;
                while (requests < maximumConnections)
                {
                    if (requests > 0 &&
                        !WaitForPendingConnection(listener, 500))
                    {
                        break;
                    }

                    using (TcpClient connection = listener.AcceptTcpClient())
                    {
                        requests++;
                        string request = ReadRequestText(
                            connection.GetStream());
                        if (onRequest != null)
                        {
                            onRequest(request, requests);
                        }

                        SendResponse(
                            connection.GetStream(),
                            200,
                            "OK",
                            contentType,
                            body,
                            null);
                    }
                }

                return requests;
            });
    }

    private static bool WaitForPendingConnection(
        TcpListener listener,
        int timeoutMilliseconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (listener.Pending())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return listener.Pending();
    }

    private static void TestFinishReasonCompletesStream(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task server = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                {
                    ReadRequest(connection.GetStream());
                    string body =
                        "data: {\"choices\":[{\"delta\":{\"content\":\"complete\"}}]}\r\n\r\n" +
                        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\r\n\r\n";
                    SendResponse(
                        connection.GetStream(),
                        200,
                        "OK",
                        "text/event-stream",
                        body,
                        null);
                }
            });
        object client = CreateClient(clientType, 2000, 1000, 1, 10, 1000);
        try
        {
            StringBuilder delta = new StringBuilder();
            string result = Generate(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/finish-reason"),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(3));
            AssertEqual("complete", result, "finish_reason stream result");
            AssertEqual("complete", delta.ToString(), "finish_reason delta");
            server.Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestCancellationWinsOverHeadersTimeout(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        ManualResetEventSlim requestReceived = new ManualResetEventSlim(false);
        ManualResetEventSlim release = new ManualResetEventSlim(false);
        Task server = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                {
                    ReadRequest(connection.GetStream());
                    requestReceived.Set();
                    release.Wait(TimeSpan.FromSeconds(3));
                }
            });
        object client = CreateClient(clientType, 2000, 1000, 2, 10, 1000);
        CancellationTokenSource cancellation = new CancellationTokenSource();
        try
        {
            Task requestTask = InvokeGenerate(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/cancel-headers"),
                new StringBuilder(),
                cancellation.Token);
            AssertTrue(
                requestReceived.Wait(TimeSpan.FromSeconds(2)),
                "Cancellation test request received");
            Stopwatch stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();
            Exception failure = WaitForFailure(
                requestTask,
                TimeSpan.FromSeconds(2));
            stopwatch.Stop();
            AssertTrue(
                failure is OperationCanceledException,
                "User cancellation type");
            AssertTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                "User cancellation latency");
            Thread.Sleep(200);
            AssertTrue(!listener.Pending(), "User cancellation not retried");
        }
        finally
        {
            release.Set();
            server.Wait(TimeSpan.FromSeconds(3));
            cancellation.Dispose();
            requestReceived.Dispose();
            release.Dispose();
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestEventNameCompletesStream(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = StartListener();
        int port = GetPort(listener);
        Task server = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                {
                    ReadRequest(connection.GetStream());
                    string body =
                        "data: {\"choices\":[{\"delta\":{\"content\":\"event-complete\"}}]}\r\n\r\n" +
                        "event: done\r\n" +
                        "data: {}\r\n\r\n";
                    SendResponse(
                        connection.GetStream(),
                        200,
                        "OK",
                        "text/event-stream",
                        body,
                        null);
                }
            });
        object client = CreateClient(clientType, 2000, 1000, 1, 10, 1000);
        try
        {
            StringBuilder delta = new StringBuilder();
            string result = Generate(
                clientType,
                client,
                CreateRequest(
                    requestType,
                    "http://127.0.0.1:" + port + "/event-complete"),
                delta,
                CancellationToken.None,
                TimeSpan.FromSeconds(3));
            AssertEqual("event-complete", result, "event name stream result");
            AssertEqual("event-complete", delta.ToString(), "event name stream delta");
            server.Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static object CreateClient(
        Type clientType,
        int headersTimeoutMilliseconds,
        int readIdleTimeoutMilliseconds,
        int maximumAttempts,
        int retryBaseDelayMilliseconds,
        int maximumRetryAfterMilliseconds)
    {
        Type[] parameterTypes =
        {
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int)
        };
        ConstructorInfo constructor = clientType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            parameterTypes,
            null);
        if (constructor == null)
        {
            throw new MissingMethodException(
                clientType.FullName,
                ".ctor(Int32, Int32, Int32, Int32, Int32)");
        }

        return constructor.Invoke(new object[]
        {
            headersTimeoutMilliseconds,
            readIdleTimeoutMilliseconds,
            maximumAttempts,
            retryBaseDelayMilliseconds,
            maximumRetryAfterMilliseconds
        });
    }

    private static object CreateClientWithAttachmentTimeout(
        Type clientType,
        int headersTimeoutMilliseconds,
        int readIdleTimeoutMilliseconds,
        int maximumAttempts,
        int retryBaseDelayMilliseconds,
        int maximumRetryAfterMilliseconds,
        int attachmentHeadersTimeoutMilliseconds)
    {
        Type[] parameterTypes =
        {
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int)
        };
        ConstructorInfo constructor = clientType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            parameterTypes,
            null);
        if (constructor == null)
        {
            throw new MissingMethodException(
                clientType.FullName,
                ".ctor(Int32, Int32, Int32, Int32, Int32, Int32)");
        }

        return constructor.Invoke(new object[]
        {
            headersTimeoutMilliseconds,
            readIdleTimeoutMilliseconds,
            maximumAttempts,
            retryBaseDelayMilliseconds,
            maximumRetryAfterMilliseconds,
            attachmentHeadersTimeoutMilliseconds
        });
    }

    private static object CreateRequest(Type requestType, string endpoint)
    {
        object request = Activator.CreateInstance(requestType, true);
        requestType.GetProperty("EndpointUrl").SetValue(request, endpoint, null);
        requestType.GetProperty("ApiKey").SetValue(request, "test-key", null);
        requestType.GetProperty("ModelName").SetValue(request, "test-model", null);
        requestType.GetProperty("Prompt").SetValue(
            request,
            "authorized network reliability test",
            null);
        return request;
    }

    private static void SetBinaryAttachment(
        Type requestType,
        object request,
        Type itemType)
    {
        object item = Activator.CreateInstance(itemType, true);
        itemType.GetProperty("Name").SetValue(
            item,
            "timeout-image.png",
            null);
        itemType.GetProperty("Kind").SetValue(
            item,
            Enum.Parse(itemType.GetProperty("Kind").PropertyType, "Image"),
            null);
        itemType.GetProperty("MimeType").SetValue(
            item,
            "image/png",
            null);
        itemType.GetProperty("BinaryData").SetValue(
            item,
            new byte[] { 1, 2, 3, 4 },
            null);
        Array attachments = Array.CreateInstance(itemType, 1);
        attachments.SetValue(item, 0);
        requestType.GetProperty("Attachments").SetValue(
            request,
            attachments,
            null);
    }

    private static Task InvokeGenerate(
        Type clientType,
        object client,
        object request,
        StringBuilder delta,
        CancellationToken cancellationToken)
    {
        MethodInfo generate = clientType.GetMethod(
            "GenerateAsync",
            BindingFlags.Instance | BindingFlags.Public);
        return (Task)generate.Invoke(
            client,
            new object[]
            {
                request,
                new Action<string>(delegate(string value) { delta.Append(value); }),
                new Action<string>(delegate { }),
                cancellationToken
            });
    }

    private static Exception InvokeToolAttemptFailure(
        Type clientType,
        object client,
        object request,
        string endpoint,
        TimeSpan timeout)
    {
        Type attemptType = clientType.GetNestedType(
            "EndpointAttempt",
            BindingFlags.NonPublic);
        if (attemptType == null)
        {
            throw new MissingMemberException(
                clientType.FullName,
                "EndpointAttempt");
        }

        object attempt = Activator.CreateInstance(attemptType, true);
        attemptType.GetProperty("Url").SetValue(
            attempt,
            new Uri(endpoint),
            null);
        MethodInfo sendToolAttempt = clientType.GetMethod(
            "SendToolAttemptAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (sendToolAttempt == null)
        {
            throw new MissingMethodException(
                clientType.FullName,
                "SendToolAttemptAsync");
        }

        Task task = (Task)sendToolAttempt.Invoke(
            client,
            new object[]
            {
                attempt,
                request,
                "{\"model\":\"test-model\",\"messages\":[],\"tools\":[]}",
                CancellationToken.None
            });
        Exception failure = WaitForFailure(task, timeout);
        if (failure == null)
        {
            throw new InvalidOperationException(
                "Tool attachment request unexpectedly succeeded.");
        }

        return failure;
    }

    private static string Generate(
        Type clientType,
        object client,
        object request,
        StringBuilder delta,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        Task task = InvokeGenerate(
            clientType,
            client,
            request,
            delta,
            cancellationToken);
        if (!task.Wait(timeout))
        {
            throw new TimeoutException("Generation did not complete in time.");
        }

        return Convert.ToString(
            task.GetType().GetProperty("Result").GetValue(task, null));
    }

    private static Exception GenerateFailure(
        Type clientType,
        object client,
        object request,
        StringBuilder delta,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        Task task = InvokeGenerate(
            clientType,
            client,
            request,
            delta,
            cancellationToken);
        Exception failure = WaitForFailure(task, timeout);
        if (failure == null)
        {
            throw new InvalidOperationException("Generation unexpectedly succeeded.");
        }

        return failure;
    }

    private static IList FetchModels(
        Type clientType,
        object client,
        string endpoint,
        string apiKey,
        TimeSpan timeout)
    {
        Task task = InvokeFetchModels(
            clientType,
            client,
            endpoint,
            apiKey);
        if (!task.Wait(timeout))
        {
            throw new TimeoutException(
                "Model discovery did not complete in time.");
        }

        return (IList)task.GetType().GetProperty("Result")
            .GetValue(task, null);
    }

    private static Exception FetchModelsFailure(
        Type clientType,
        object client,
        string endpoint,
        string apiKey,
        TimeSpan timeout)
    {
        Task task = InvokeFetchModels(
            clientType,
            client,
            endpoint,
            apiKey);
        Exception failure = WaitForFailure(task, timeout);
        if (failure == null)
        {
            throw new InvalidOperationException(
                "Model discovery unexpectedly succeeded.");
        }

        return failure;
    }

    private static Task InvokeFetchModels(
        Type clientType,
        object client,
        string endpoint,
        string apiKey)
    {
        MethodInfo fetchModels = clientType.GetMethod(
            "FetchModelsAsync",
            BindingFlags.Instance | BindingFlags.Public);
        if (fetchModels == null)
        {
            throw new MissingMethodException(
                clientType.FullName,
                "FetchModelsAsync");
        }

        return (Task)fetchModels.Invoke(
            client,
            new object[]
            {
                endpoint,
                apiKey,
                CancellationToken.None
            });
    }

    private static Exception WaitForFailure(Task task, TimeSpan timeout)
    {
        try
        {
            if (!task.Wait(timeout))
            {
                throw new TimeoutException("Request did not complete in time.");
            }

            return null;
        }
        catch (AggregateException exception)
        {
            return Unwrap(exception);
        }
    }

    private static TcpListener StartListener()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int GetPort(TcpListener listener)
    {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void SendOpenStream(NetworkStream stream, string firstData)
    {
        string headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(firstData);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    private static void SendResponse(
        NetworkStream stream,
        int statusCode,
        string reason,
        string contentType,
        string body,
        string extraHeaders)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string headers =
            "HTTP/1.1 " + statusCode + " " + reason + "\r\n" +
            "Content-Type: " + contentType + "; charset=utf-8\r\n" +
            (extraHeaders ?? string.Empty) +
            "Content-Length: " + bodyBytes.Length + "\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    private static void ReadRequest(NetworkStream stream)
    {
        ReadRequestText(stream);
    }

    private static string ReadRequestText(NetworkStream stream)
    {
        stream.ReadTimeout = 5000;
        MemoryStream bytes = new MemoryStream();
        byte[] buffer = new byte[4096];
        int headerEnd = -1;
        int contentLength = 0;
        while (headerEnd < 0)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return Encoding.ASCII.GetString(
                    bytes.GetBuffer(),
                    0,
                    (int)bytes.Length);
            }

            bytes.Write(buffer, 0, read);
            headerEnd = FindHeaderEnd(bytes.GetBuffer(), (int)bytes.Length);
        }

        string headers = Encoding.ASCII.GetString(
            bytes.GetBuffer(),
            0,
            headerEnd);
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

        int bodyStart = headerEnd + 4;
        while (bytes.Length - bodyStart < contentLength)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            bytes.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(
            bytes.GetBuffer(),
            0,
            (int)bytes.Length);
    }

    private static int FindHeaderEnd(byte[] value, int length)
    {
        for (int index = 0; index <= length - 4; index++)
        {
            if (value[index] == 13 && value[index + 1] == 10 &&
                value[index + 2] == 13 && value[index + 3] == 10)
            {
                return index;
            }
        }

        return -1;
    }

    private static int Wait(Task<int> task)
    {
        if (!task.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Test server did not complete in time.");
        }

        return task.Result;
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
                "FilePromptAI.exe");
        }

        candidate = Path.GetFullPath(candidate);
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                "FilePromptAI.exe was not found.",
                candidate);
        }

        return candidate;
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while ((current is AggregateException ||
            current is TargetInvocationException) &&
            current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private sealed class RejectingProxy : IWebProxy
    {
        private int callCount;

        public int CallCount
        {
            get { return Interlocked.CompareExchange(ref callCount, 0, 0); }
        }

        public ICredentials Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            Interlocked.Increment(ref callCount);
            return new Uri("http://127.0.0.1:1/");
        }

        public bool IsBypassed(Uri host)
        {
            Interlocked.Increment(ref callCount);
            return false;
        }
    }

    private static void AssertContains(
        string actual,
        string expected,
        string name)
    {
        AssertTrue(
            actual != null && actual.IndexOf(
                expected,
                StringComparison.Ordinal) >= 0,
            name);
    }

    private static void AssertNotContains(
        string actual,
        string unexpected,
        string name)
    {
        AssertTrue(
            actual != null && actual.IndexOf(
                unexpected,
                StringComparison.OrdinalIgnoreCase) < 0,
            name);
    }

    private static void AssertType(
        Exception actual,
        string expectedType,
        string name)
    {
        AssertTrue(
            actual != null && actual.GetType().FullName == expectedType,
            name);
    }

    private static void AssertEqual(
        string expected,
        string actual,
        string name)
    {
        AssertTrue(string.Equals(expected, actual, StringComparison.Ordinal), name);
    }

    private static void AssertEqual(int expected, int actual, string name)
    {
        AssertTrue(expected == actual, name);
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " failed.");
        }

        Console.WriteLine("PASS | " + name);
    }
}
