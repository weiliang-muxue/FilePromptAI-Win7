using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class ApiHardeningSmokeTest
{
    private static int Main(string[] args)
    {
        try
        {
            string applicationPath = ResolveApplicationPath(args);
            Assembly application = Assembly.LoadFrom(applicationPath);
            Type clientType = application.GetType(
                "FilePromptWin7.ModelClient",
                true);
            Type requestType = application.GetType(
                "FilePromptWin7.ModelRequest",
                true);

            TestRedirectIsRejected(clientType, requestType);
            TestBodyCancellation(clientType, 200, "OK");
            TestBodyCancellation(
                clientType,
                500,
                "Internal Server Error");
            TestMalformedStreamIsRejected(clientType, requestType);
            Console.WriteLine("PASS | API hardening");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | API hardening");
            Console.Error.WriteLine(Unwrap(exception));
            return 1;
        }
    }

    private static void TestRedirectIsRejected(
        Type clientType,
        Type requestType)
    {
        TcpListener origin = new TcpListener(IPAddress.Loopback, 0);
        TcpListener redirectTarget = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        redirectTarget.Start();
        int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        int targetPort = ((IPEndPoint)redirectTarget.LocalEndpoint).Port;
        Task originServer = Task.Factory.StartNew(
            delegate { HandleRedirect(origin, targetPort); });
        object client = Activator.CreateInstance(clientType, true);
        try
        {
            object request = CreateRequest(
                requestType,
                "http://127.0.0.1:" + originPort + "/exact-only");
            Task task = InvokeGenerate(
                clientType,
                client,
                request,
                CancellationToken.None);
            if (!originServer.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Redirect origin was not reached.");
            }

            Stopwatch waitForRedirect = Stopwatch.StartNew();
            while (!redirectTarget.Pending() &&
                waitForRedirect.Elapsed < TimeSpan.FromMilliseconds(750))
            {
                Thread.Sleep(15);
            }

            bool followedRedirect = redirectTarget.Pending();
            if (followedRedirect)
            {
                Task targetServer = Task.Factory.StartNew(
                    delegate
                    {
                        HandleResponse(
                            redirectTarget,
                            200,
                            "OK",
                            "application/json",
                            "{\"choices\":[{\"message\":{\"content\":\"redirected\"}}]}");
                    });
                targetServer.Wait(TimeSpan.FromSeconds(5));
            }

            Exception failure = WaitForFailure(
                task,
                TimeSpan.FromSeconds(5));
            AssertTrue(!followedRedirect, "Redirect target was not contacted");
            AssertTrue(
                failure != null &&
                failure.GetType().FullName == "FilePromptWin7.ModelCallException",
                "Redirect returned ModelCallException");
            AssertContains(
                failure == null ? string.Empty : failure.Message,
                "重定向",
                "Redirect guidance");
        }
        finally
        {
            ((IDisposable)client).Dispose();
            origin.Stop();
            redirectTarget.Stop();
        }
    }

    private static void TestBodyCancellation(
        Type clientType,
        int statusCode,
        string reason)
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ManualResetEventSlim headersSent = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseServer = new ManualResetEventSlim(false);
        Task server = Task.Factory.StartNew(
            delegate
            {
                HandleStalledResponse(
                    listener,
                    statusCode,
                    reason,
                    headersSent,
                    releaseServer);
            });
        object client = Activator.CreateInstance(clientType, true);
        CancellationTokenSource cancellation = new CancellationTokenSource();
        try
        {
            MethodInfo testConnection = clientType.GetMethod(
                "TestConnectionAsync",
                BindingFlags.Instance | BindingFlags.Public);
            Task task = (Task)testConnection.Invoke(
                client,
                new object[]
                {
                    "http://127.0.0.1:" + port + "/stalled-body",
                    "cancel-secret",
                    "cancel-model",
                    cancellation.Token
                });
            if (!headersSent.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Stalled response headers were not sent.");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();
            Exception failure = WaitForFailure(
                task,
                TimeSpan.FromSeconds(2));
            stopwatch.Stop();
            AssertTrue(
                failure is OperationCanceledException,
                "HTTP " + statusCode + " body cancellation type");
            AssertTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                "HTTP " + statusCode + " body cancellation latency");
        }
        finally
        {
            releaseServer.Set();
            server.Wait(TimeSpan.FromSeconds(5));
            cancellation.Dispose();
            headersSent.Dispose();
            releaseServer.Dispose();
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static void TestMalformedStreamIsRejected(
        Type clientType,
        Type requestType)
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Factory.StartNew(
            delegate
            {
                string body =
                    "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\r\n\r\n" +
                    "data: definitely-not-json\r\n\r\n" +
                    "data: [DONE]\r\n\r\n";
                HandleResponse(
                    listener,
                    200,
                    "OK",
                    "text/event-stream",
                    body);
            });
        object client = Activator.CreateInstance(clientType, true);
        try
        {
            object request = CreateRequest(
                requestType,
                "http://127.0.0.1:" + port + "/malformed-stream");
            Task task = InvokeGenerate(
                clientType,
                client,
                request,
                CancellationToken.None);
            Exception failure = WaitForFailure(
                task,
                TimeSpan.FromSeconds(5));
            AssertTrue(
                failure != null &&
                failure.GetType().FullName == "FilePromptWin7.ModelCallException",
                "Malformed stream returned ModelCallException");
            AssertContains(
                failure == null ? string.Empty : failure.Message,
                "无法解析",
                "Malformed stream guidance");
            server.Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            ((IDisposable)client).Dispose();
            listener.Stop();
        }
    }

    private static object CreateRequest(Type requestType, string endpoint)
    {
        object request = Activator.CreateInstance(requestType, true);
        requestType.GetProperty("EndpointUrl").SetValue(
            request,
            endpoint,
            null);
        requestType.GetProperty("ApiKey").SetValue(
            request,
            "hardening-secret",
            null);
        requestType.GetProperty("ModelName").SetValue(
            request,
            "hardening-model",
            null);
        requestType.GetProperty("Prompt").SetValue(
            request,
            "authorized hardening prompt",
            null);
        return request;
    }

    private static Task InvokeGenerate(
        Type clientType,
        object client,
        object request,
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
                new Action<string>(delegate { }),
                new Action<string>(delegate { }),
                cancellationToken
            });
    }

    private static Exception WaitForFailure(Task task, TimeSpan timeout)
    {
        try
        {
            if (!task.Wait(timeout))
            {
                throw new TimeoutException(
                    "The request did not complete within the test timeout.");
            }

            return null;
        }
        catch (AggregateException exception)
        {
            return Unwrap(exception);
        }
    }

    private static void HandleRedirect(
        TcpListener listener,
        int targetPort)
    {
        using (TcpClient connection = listener.AcceptTcpClient())
        using (NetworkStream stream = connection.GetStream())
        {
            ReadRequest(stream);
            string response =
                "HTTP/1.1 307 Temporary Redirect\r\n" +
                "Location: http://127.0.0.1:" + targetPort +
                    "/redirect-target\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(response);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }

    private static void HandleStalledResponse(
        TcpListener listener,
        int statusCode,
        string reason,
        ManualResetEventSlim headersSent,
        ManualResetEventSlim releaseServer)
    {
        using (TcpClient connection = listener.AcceptTcpClient())
        using (NetworkStream stream = connection.GetStream())
        {
            ReadRequest(stream);
            string response =
                "HTTP/1.1 " + statusCode + " " + reason + "\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: 100\r\n" +
                "Connection: close\r\n\r\n{";
            byte[] bytes = Encoding.ASCII.GetBytes(response);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            headersSent.Set();
            releaseServer.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private static void HandleResponse(
        TcpListener listener,
        int statusCode,
        string reason,
        string contentType,
        string body)
    {
        using (TcpClient connection = listener.AcceptTcpClient())
        using (NetworkStream stream = connection.GetStream())
        {
            ReadRequest(stream);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers =
                "HTTP/1.1 " + statusCode + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "; charset=utf-8\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }
    }

    private static void ReadRequest(NetworkStream stream)
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
                return;
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
            throw new FileNotFoundException(
                "FilePrompt.exe was not found.",
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

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " failed.");
        }

        Console.WriteLine("PASS | " + name);
    }
}
