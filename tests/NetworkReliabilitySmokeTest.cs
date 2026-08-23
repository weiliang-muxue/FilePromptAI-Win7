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
        Task server = Task.Factory.StartNew(
            delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                {
                    ReadRequest(connection.GetStream());
                    SendResponse(
                        connection.GetStream(),
                        200,
                        "OK",
                        "text/event-stream",
                        "data: {\"choices\":[{\"delta\":{\"content\":\"cut-off\"}}]}\r\n\r\n",
                        null);
                }
            });
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
            server.Wait(TimeSpan.FromSeconds(2));
            Thread.Sleep(200);
            AssertTrue(!listener.Pending(), "Incomplete stream not retried after delta");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
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
