using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FilePromptAIWin7
{
    internal static class McpRuntimeSmokeTest
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1)
                {
                    throw new ArgumentException(
                        "Usage: McpRuntimeSmokeTest <FakeMcpServer.exe>");
                }

                ServicePointManager.Expect100Continue = false;
                string serverExecutable = Path.GetFullPath(args[0]);
                TestStdio(serverExecutable);
                TestBatchConstructionWithoutLaunch(serverExecutable);
                string batchSkipReason;
                if (TryGetBatchTestSkipReason(out batchSkipReason))
                {
                    Console.WriteLine(
                        "SKIP | batch MCP integration | " + batchSkipReason);
                }
                else
                {
                    TestBatchPathResolution(serverExecutable);
                    TestBatchArgumentSafety(serverExecutable);
                }
                TestStdioCancellationTree(serverExecutable);
                TestStdioDisposeTree(serverExecutable);
                TestServerFailureIsolation(serverExecutable);
                TestStreamableHttp();
                TestUnsupportedProtocolRejected();
                TestHttpBodyCancellation(false);
                TestHttpBodyCancellation(true);
                TestHttpRedirectRejected();
                Console.WriteLine("PASS | MCP runtime");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL | MCP runtime");
                Console.Error.WriteLine(Unwrap(exception));
                return 1;
            }
        }

        private static void TestStdio(string serverExecutable)
        {
            string workingDirectory = Path.Combine(
                Path.GetDirectoryName(serverExecutable),
                "working directory");
            Directory.CreateDirectory(workingDirectory);
            McpServerDefinition server = new McpServerDefinition
            {
                Name = "stdio smoke",
                Transport = "stdio",
                Command = serverExecutable,
                Arguments = new List<string>
                {
                    "--label",
                    "value with spaces",
                    "json",
                    "prod",
                    "true"
                },
                WorkingDirectory = workingDirectory,
                Environment = new Dictionary<string, string>
                {
                    { "FILEPROMPT_MCP_TEST", "offline-env" }
                },
                Enabled = true,
                RequireConfirmation = true
            };

            using (McpRuntime runtime = McpRuntime.ConnectAsync(
                new[] { server },
                null,
                CancellationToken.None).GetAwaiter().GetResult())
            {
                Assert(runtime.Tools.Count == 1, "stdio tool discovery");
                McpToolDefinition tool = runtime.Tools[0];
                Assert(tool.RequireConfirmation, "stdio confirmation metadata");
                Assert(
                    tool.PublicName.StartsWith(
                        "mcp__stdio_smoke__lookup",
                        StringComparison.Ordinal),
                    "stdio public name");
                McpToolResult result = runtime.CallToolAsync(
                    tool.PublicName,
                    "{\"query\":\"needle\"}",
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(!result.IsError, "stdio tool success");
                Assert(
                    result.Content.IndexOf(
                        "stdio:needle:",
                        StringComparison.Ordinal) >= 0,
                    "stdio tool content");
                Assert(
                    result.Content.IndexOf(
                        "offline-env",
                        StringComparison.Ordinal) < 0 &&
                    result.Content.IndexOf(
                        "value with spaces",
                        StringComparison.Ordinal) < 0 &&
                    result.Content.IndexOf(
                        workingDirectory,
                        StringComparison.OrdinalIgnoreCase) < 0,
                    "stdio configuration redacted");
                Assert(
                    result.Content.IndexOf(
                        "已隐藏 MCP 配置",
                        StringComparison.Ordinal) >= 0,
                    "stdio redaction marker");
                Assert(
                    result.Content.IndexOf(
                        "json|prod|true",
                        StringComparison.Ordinal) >= 0,
                    "short ordinary configuration text preserved");
                Assert(
                    result.Content.IndexOf(
                        "当前模型工具消息仅传递文本说明",
                        StringComparison.Ordinal) >= 0,
                    "binary MCP result withheld");
            }
        }

        private static void TestBatchPathResolution(string serverExecutable)
        {
            string commandDirectory = Path.Combine(
                Path.GetDirectoryName(serverExecutable),
                "path command directory");
            Directory.CreateDirectory(commandDirectory);
            string wrapper = Path.Combine(commandDirectory, "fake-npx.cmd");
            string relativeServer = "%~dp0..\\" +
                Path.GetFileName(serverExecutable);
            File.WriteAllText(
                wrapper,
                "@echo off\r\n\"" + relativeServer + "\" %*\r\n",
                Encoding.ASCII);

            McpServerDefinition server = new McpServerDefinition
            {
                Name = "batch path",
                Transport = "stdio",
                Command = "fake-npx",
                Arguments = new List<string>
                {
                    "--label",
                    "batch value with spaces"
                },
                Environment = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    { "pAtH", commandDirectory },
                    { "PathExt", ".CMD;.EXE" }
                },
                Enabled = true
            };

            using (McpRuntime runtime = McpRuntime.ConnectAsync(
                new[] { server },
                null,
                CancellationToken.None).GetAwaiter().GetResult())
            {
                McpToolResult result = runtime.CallToolAsync(
                    runtime.Tools[0].PublicName,
                    "{\"query\":\"batch\"}",
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(
                    result.Content.IndexOf(
                        "stdio:batch:",
                        StringComparison.Ordinal) >= 0,
                    "batch command resolved through PATH/PATHEXT");
            }
        }

        private static void TestBatchConstructionWithoutLaunch(
            string serverExecutable)
        {
            string commandDirectory = Path.Combine(
                Path.GetDirectoryName(serverExecutable),
                "batch construction");
            Directory.CreateDirectory(commandDirectory);
            string wrapper = Path.Combine(commandDirectory, "fake-npx.cmd");
            File.WriteAllText(wrapper, "@echo off\r\n", Encoding.ASCII);
            Type connectionType = typeof(McpRuntime).Assembly.GetType(
                "FilePromptAIWin7.StdioMcpConnection",
                true);
            MethodInfo resolve = connectionType.GetMethod(
                "ResolveCommand",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo build = connectionType.GetMethod(
                "BuildBatchArguments",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert(resolve != null && build != null,
                "batch command helpers are present");

            Dictionary<string, string> environment =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    { "pAtH", commandDirectory },
                    { "PathExt", ".CMD;.EXE" }
                };
            string resolved = (string)resolve.Invoke(
                null,
                new object[]
                {
                    "fake-npx",
                    Path.GetDirectoryName(serverExecutable),
                    environment
                });
            Assert(string.Equals(
                    resolved,
                    wrapper,
                    StringComparison.OrdinalIgnoreCase),
                "batch command resolves through PATH/PATHEXT without launch");

            string safeValue = "safe & echo remains-an-argument";
            string arguments = (string)build.Invoke(
                null,
                new object[]
                {
                    wrapper,
                    new[] { "--label", safeValue }
                });
            Assert(arguments.IndexOf(
                    "\"" + safeValue + "\"",
                    StringComparison.Ordinal) >= 0,
                "batch metacharacters are quoted as one argument");

            AssertPrivateFailure<McpException>(
                build,
                new object[] { wrapper, new[] { "%PATH%" } },
                "batch percent expansion is rejected without launch");
            AssertPrivateFailure<McpException>(
                build,
                new object[] { wrapper, new[] { "line1\r\nline2" } },
                "batch line injection is rejected without launch");
        }

        private static bool TryGetBatchTestSkipReason(out string reason)
        {
            reason = string.Empty;
            string commandInterpreter = Path.Combine(
                Environment.SystemDirectory,
                "cmd.exe");
            string layer = GetCompatibilityLayer(
                Registry.CurrentUser,
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                commandInterpreter);
            if (ContainsRunAsAdministratorLayer(layer))
            {
                reason = "current-user compatibility settings force cmd.exe " +
                    "to require elevation";
                return true;
            }

            layer = GetCompatibilityLayer(
                Registry.LocalMachine,
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                commandInterpreter);
            if (ContainsRunAsAdministratorLayer(layer))
            {
                reason = "machine compatibility settings force cmd.exe " +
                    "to require elevation";
                return true;
            }

            return false;
        }

        private static string GetCompatibilityLayer(
            RegistryKey root,
            string subkey,
            string executable)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(subkey, false))
                {
                    return key == null
                        ? string.Empty
                        : Convert.ToString(key.GetValue(executable));
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool ContainsRunAsAdministratorLayer(string value)
        {
            return (value ?? string.Empty).Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries).Any(
                    item => string.Equals(
                        item,
                        "RUNASADMIN",
                        StringComparison.OrdinalIgnoreCase));
        }

        private static void AssertPrivateFailure<TException>(
            MethodInfo method,
            object[] arguments,
            string name)
            where TException : Exception
        {
            Exception failure = null;
            try
            {
                method.Invoke(null, arguments);
            }
            catch (TargetInvocationException exception)
            {
                failure = exception.InnerException;
            }

            Assert(failure is TException, name);
        }

        private static void TestBatchArgumentSafety(string serverExecutable)
        {
            string directory = Path.Combine(
                Path.GetDirectoryName(serverExecutable),
                "batch safety");
            Directory.CreateDirectory(directory);
            string wrapper = Path.Combine(directory, "safe-wrapper.bat");
            string injected = Path.Combine(directory, "injected.txt");
            string started = Path.Combine(directory, "started.txt");
            string relativeServer = "%~dp0..\\" +
                Path.GetFileName(serverExecutable);
            File.WriteAllText(
                wrapper,
                "@echo off\r\n" +
                "echo started>\"%~dp0started.txt\"\r\n" +
                "\"" + relativeServer + "\" %*\r\n",
                Encoding.ASCII);
            DeleteIfExists(injected);
            DeleteIfExists(started);

            McpServerDefinition safe = new McpServerDefinition
            {
                Name = "batch safe args",
                Transport = "stdio",
                Command = wrapper,
                Arguments = new List<string>
                {
                    "--label",
                    "safe & echo injected>" + injected
                },
                Enabled = true
            };
            using (McpRuntime runtime = McpRuntime.ConnectAsync(
                new[] { safe },
                null,
                CancellationToken.None).GetAwaiter().GetResult())
            {
                Assert(runtime.Tools.Count == 1,
                    "batch metacharacters remain a single argument");
            }

            Assert(!File.Exists(injected),
                "batch arguments do not inject shell commands");
            DeleteIfExists(started);

            McpServerDefinition rejected = safe.Clone();
            rejected.Name = "batch rejected args";
            rejected.Arguments = new List<string>
            {
                "--label",
                "%PATH%"
            };
            Exception failure = WaitForFailure(
                McpRuntime.ConnectAsync(
                    new[] { rejected },
                    null,
                    CancellationToken.None),
                TimeSpan.FromSeconds(5));
            Assert(failure is McpException,
                "unsafe batch expansion is rejected");
            Assert(!File.Exists(started),
                "rejected batch arguments are never executed");
        }

        private static void TestStdioCancellationTree(string serverExecutable)
        {
            string childMarker = Path.Combine(
                Path.GetDirectoryName(serverExecutable),
                "mcp-child.pid");
            DeleteIfExists(childMarker);
            McpServerDefinition server = new McpServerDefinition
            {
                Name = "stdio hang",
                Transport = "stdio",
                Command = serverExecutable,
                Arguments = new List<string>
                {
                    "--spawn-child",
                    childMarker,
                    "--hang"
                },
                Enabled = true
            };
            CancellationTokenSource cancellation =
                new CancellationTokenSource();
            cancellation.CancelAfter(800);
            Exception failure = WaitForFailure(
                McpRuntime.ConnectAsync(
                    new[] { server },
                    null,
                    cancellation.Token),
                TimeSpan.FromSeconds(5));
            cancellation.Dispose();
            Assert(
                failure is OperationCanceledException,
                "stdio cancellation terminates server");
            Assert(WaitForFile(childMarker, TimeSpan.FromSeconds(3)),
                "stdio child process started");
            int childProcessId = int.Parse(File.ReadAllText(childMarker));
            Assert(WaitForProcessExit(
                childProcessId,
                TimeSpan.FromSeconds(5)),
                "stdio cancellation terminates child process tree");
            DeleteIfExists(childMarker);
        }

        private static void TestServerFailureIsolation(string serverExecutable)
        {
            McpServerDefinition broken = new McpServerDefinition
            {
                Name = "broken server",
                Transport = "stdio",
                Command = string.Empty,
                Enabled = true
            };
            McpServerDefinition healthy = new McpServerDefinition
            {
                Name = "healthy server",
                Transport = "stdio",
                Command = serverExecutable,
                Enabled = true
            };
            List<string> statuses = new List<string>();
            using (McpRuntime runtime = McpRuntime.ConnectAsync(
                new[] { broken, healthy },
                statuses.Add,
                CancellationToken.None).GetAwaiter().GetResult())
            {
                Assert(runtime.Tools.Count == 1,
                    "healthy MCP remains available after peer failure");
                Assert(statuses.Any(value => value.IndexOf(
                    "broken server",
                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                    value.IndexOf("已跳过", StringComparison.Ordinal) >= 0),
                    "failed MCP is reported as skipped");
            }
        }

        private static void TestStdioDisposeTree(string serverExecutable)
        {
            string childMarker = Path.Combine(
                Path.GetDirectoryName(serverExecutable),
                "mcp-dispose-child.pid");
            DeleteIfExists(childMarker);
            McpServerDefinition server = new McpServerDefinition
            {
                Name = "stdio dispose tree",
                Transport = "stdio",
                Command = serverExecutable,
                Arguments = new List<string>
                {
                    "--spawn-child",
                    childMarker
                },
                Enabled = true
            };
            int childProcessId;
            using (McpRuntime runtime = McpRuntime.ConnectAsync(
                new[] { server },
                null,
                CancellationToken.None).GetAwaiter().GetResult())
            {
                Assert(WaitForFile(childMarker, TimeSpan.FromSeconds(3)),
                    "stdio dispose child process started");
                childProcessId = int.Parse(File.ReadAllText(childMarker));
            }

            Assert(WaitForProcessExit(
                childProcessId,
                TimeSpan.FromSeconds(5)),
                "stdio dispose terminates child process tree");
            DeleteIfExists(childMarker);
        }

        private static void TestStreamableHttp()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serverTask = Task.Factory.StartNew(delegate
            {
                for (int index = 0; index < 4; index++)
                {
                    using (TcpClient connection = listener.AcceptTcpClient())
                    using (NetworkStream stream = connection.GetStream())
                    {
                        string request = ReadRequest(stream);
                        InspectHttpRequest(request, index);
                        WriteHttpResponse(stream, index);
                    }
                }
            });

            try
            {
                McpServerDefinition server = new McpServerDefinition
                {
                    Name = "HTTP smoke",
                    Transport = "http",
                    Url = "http://127.0.0.1:" + port + "/mcp",
                    Headers = new Dictionary<string, string>
                    {
                        { "Authorization", "Bearer mcp-http-secret" }
                    },
                    Enabled = true,
                    RequireConfirmation = true
                };
                using (McpRuntime runtime = McpRuntime.ConnectAsync(
                    new[] { server },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    Assert(runtime.Tools.Count == 1, "HTTP SSE tool discovery");
                    McpToolResult result = runtime.CallToolAsync(
                        runtime.Tools[0].PublicName,
                        "{\"query\":\"http-needle\"}",
                        CancellationToken.None).GetAwaiter().GetResult();
                    Assert(
                        result.Content.IndexOf(
                            "http:http-needle",
                            StringComparison.Ordinal) >= 0,
                        "HTTP tool result");
                    Assert(
                        result.Content.IndexOf(
                            "mcp-http-secret",
                            StringComparison.Ordinal) < 0 &&
                        result.Content.IndexOf(
                            "已隐藏 MCP 配置",
                            StringComparison.Ordinal) >= 0,
                        "HTTP header value redacted");
                }

                Assert(
                    serverTask.Wait(TimeSpan.FromSeconds(5)),
                    "HTTP server completed");
            }
            finally
            {
                listener.Stop();
            }
        }

        private static void InspectHttpRequest(string request, int index)
        {
            Assert(
                request.IndexOf(
                    "Authorization: Bearer mcp-http-secret",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                "HTTP custom header " + index);
            Assert(
                request.IndexOf(
                    "MCP-Protocol-Version: " + (index == 0
                        ? "2025-06-18"
                        : "2024-11-05"),
                    StringComparison.OrdinalIgnoreCase) >= 0,
                "HTTP protocol header " + index);
            if (index > 0)
            {
                Assert(
                    request.IndexOf(
                        "Mcp-Session-Id: smoke-session",
                        StringComparison.OrdinalIgnoreCase) >= 0,
                    "HTTP session header " + index);
            }

            IDictionary<string, object> body = ParseBody(request);
            string method = Convert.ToString(body["method"]);
            string[] expected =
            {
                "initialize",
                "notifications/initialized",
                "tools/list",
                "tools/call"
            };
            Assert(method == expected[index], "HTTP method " + index);
            if (index == 0)
            {
                IDictionary<string, object> parameters =
                    body["params"] as IDictionary<string, object>;
                Assert(
                    Convert.ToString(parameters["protocolVersion"]) ==
                        "2025-06-18",
                    "HTTP initialize advertises latest supported protocol");
            }

            if (index == 3)
            {
                IDictionary<string, object> parameters =
                    body["params"] as IDictionary<string, object>;
                IDictionary<string, object> arguments =
                    parameters["arguments"] as IDictionary<string, object>;
                Assert(
                    Convert.ToString(arguments["query"]) == "http-needle",
                    "HTTP tool arguments");
            }
        }

        private static void TestUnsupportedProtocolRejected()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serverTask = Task.Factory.StartNew(delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                using (NetworkStream stream = connection.GetStream())
                {
                    ReadRequest(stream);
                    WriteJsonResult(
                        stream,
                        1,
                        new Dictionary<string, object>
                        {
                            { "protocolVersion", "2099-01-01" },
                            { "capabilities", new Dictionary<string, object>() }
                        });
                }
            });

            try
            {
                McpServerDefinition server = new McpServerDefinition
                {
                    Name = "unsupported protocol",
                    Transport = "http",
                    Url = "http://127.0.0.1:" + port + "/mcp",
                    Enabled = true
                };
                Exception failure = WaitForFailure(
                    McpRuntime.ConnectAsync(
                        new[] { server },
                        null,
                        CancellationToken.None),
                    TimeSpan.FromSeconds(5));
                Assert(failure is McpException,
                    "unsupported MCP protocol rejected");
                Assert(failure.Message.IndexOf(
                    "2099-01-01",
                    StringComparison.Ordinal) >= 0,
                    "unsupported MCP protocol reported");
                Assert(serverTask.Wait(TimeSpan.FromSeconds(5)),
                    "unsupported protocol server completed");
                Thread.Sleep(100);
                Assert(!listener.Pending(),
                    "initialized notification not sent after protocol rejection");
            }
            finally
            {
                listener.Stop();
            }
        }

        private static void TestHttpBodyCancellation(bool useSse)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serverTask = Task.Factory.StartNew(delegate
            {
                using (TcpClient connection = listener.AcceptTcpClient())
                using (NetworkStream stream = connection.GetStream())
                {
                    ReadRequest(stream);
                    byte[] prefix = Encoding.UTF8.GetBytes(useSse
                        ? "data: {\"jsonrpc\":\"2.0\""
                        : "{\"jsonrpc\":\"2.0\"");
                    WriteResponse(
                        stream,
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: " + (useSse
                            ? "text/event-stream"
                            : "application/json") + "; charset=utf-8\r\n" +
                        "Content-Length: 100000\r\n" +
                        "Connection: close\r\n\r\n",
                        prefix);
                    Thread.Sleep(2000);
                }
            });

            try
            {
                McpServerDefinition server = new McpServerDefinition
                {
                    Name = "HTTP body cancellation",
                    Transport = "http",
                    Url = "http://127.0.0.1:" + port + "/mcp",
                    Enabled = true
                };
                CancellationTokenSource cancellation =
                    new CancellationTokenSource();
                cancellation.CancelAfter(300);
                Exception failure = WaitForFailure(
                    McpRuntime.ConnectAsync(
                        new[] { server },
                        null,
                        cancellation.Token),
                    TimeSpan.FromSeconds(5));
                cancellation.Dispose();
                Assert(failure is OperationCanceledException,
                    "HTTP " + (useSse ? "SSE" : "JSON") +
                    " body cancellation normalized");
                Assert(serverTask.Wait(TimeSpan.FromSeconds(5)),
                    "HTTP cancellation server completed");
            }
            finally
            {
                listener.Stop();
            }
        }

        private static void TestHttpRedirectRejected()
        {
            TcpListener target = new TcpListener(IPAddress.Loopback, 0);
            TcpListener source = new TcpListener(IPAddress.Loopback, 0);
            target.Start();
            source.Start();
            int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            int sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
            Task sourceTask = Task.Factory.StartNew(delegate
            {
                using (TcpClient connection = source.AcceptTcpClient())
                using (NetworkStream stream = connection.GetStream())
                {
                    ReadRequest(stream);
                    WriteResponse(
                        stream,
                        "HTTP/1.1 307 Temporary Redirect\r\n" +
                        "Location: http://127.0.0.1:" + targetPort +
                        "/redirected\r\nContent-Length: 0\r\n" +
                        "Connection: close\r\n\r\n",
                        new byte[0]);
                }
            });

            try
            {
                McpServerDefinition server = new McpServerDefinition
                {
                    Name = "redirect smoke",
                    Transport = "http",
                    Url = "http://127.0.0.1:" + sourcePort + "/mcp",
                    Enabled = true
                };
                Exception failure = WaitForFailure(
                    McpRuntime.ConnectAsync(
                        new[] { server },
                        null,
                        CancellationToken.None),
                    TimeSpan.FromSeconds(5));
                Assert(sourceTask.Wait(TimeSpan.FromSeconds(5)),
                    "MCP redirect source completed");
                Thread.Sleep(200);
                Assert(failure is McpException,
                    "MCP redirect returns failure");
                Assert(!target.Pending(),
                    "MCP redirect target not contacted");
            }
            finally
            {
                source.Stop();
                target.Stop();
            }
        }

        private static void WriteHttpResponse(NetworkStream stream, int index)
        {
            if (index == 1)
            {
                WriteResponse(
                    stream,
                    "HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\n" +
                    "Connection: close\r\n\r\n",
                    new byte[0]);
                return;
            }

            object result;
            int id;
            if (index == 0)
            {
                id = 1;
                result = new Dictionary<string, object>
                {
                    { "protocolVersion", "2024-11-05" },
                    { "capabilities", new Dictionary<string, object>() },
                    {
                        "serverInfo",
                        new Dictionary<string, object>
                        {
                            { "name", "fake-http" },
                            { "version", "1.0" }
                        }
                    }
                };
            }
            else if (index == 2)
            {
                id = 2;
                result = new Dictionary<string, object>
                {
                    {
                        "tools",
                        new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "name", "lookup" },
                                { "description", "HTTP lookup" },
                                {
                                    "inputSchema",
                                    new Dictionary<string, object>
                                    {
                                        { "type", "object" },
                                        {
                                            "properties",
                                            new Dictionary<string, object>()
                                        }
                                    }
                                }
                            }
                        }
                    }
                };
            }
            else
            {
                id = 3;
                result = new Dictionary<string, object>
                {
                    {
                        "content",
                        new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "type", "text" },
                                {
                                    "text",
                                    "http:http-needle:Bearer mcp-http-secret"
                                }
                            }
                        }
                    },
                    { "isError", false }
                };
            }

            string json = new JavaScriptSerializer().Serialize(
                new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", id },
                    { "result", result }
                });
            if (index == 2)
            {
                string sse = "event: message\r\ndata: " + json + "\r\n\r\n";
                byte[] bytes = Encoding.UTF8.GetBytes(sse);
                WriteResponse(
                    stream,
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/event-stream; charset=utf-8\r\n" +
                    "Content-Length: " + bytes.Length + "\r\n" +
                    "Connection: close\r\n\r\n",
                    bytes);
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                string session = index == 0
                    ? "Mcp-Session-Id: smoke-session\r\n"
                    : string.Empty;
                WriteResponse(
                    stream,
                    "HTTP/1.1 200 OK\r\n" + session +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    "Content-Length: " + bytes.Length + "\r\n" +
                    "Connection: close\r\n\r\n",
                    bytes);
            }
        }

        private static void WriteResponse(
            NetworkStream stream,
            string headers,
            byte[] body)
        {
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0)
            {
                stream.Write(body, 0, body.Length);
            }

            stream.Flush();
        }

        private static void WriteJsonResult(
            NetworkStream stream,
            int id,
            object result)
        {
            string json = new JavaScriptSerializer().Serialize(
                new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", id },
                    { "result", result }
                });
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            WriteResponse(
                stream,
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + bytes.Length + "\r\n" +
                "Connection: close\r\n\r\n",
                bytes);
        }

        private static string ReadRequest(NetworkStream stream)
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
                    break;
                }

                bytes.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(bytes.GetBuffer(), (int)bytes.Length);
            }

            string headers = Encoding.ASCII.GetString(
                bytes.GetBuffer(),
                0,
                headerEnd < 0 ? (int)bytes.Length : headerEnd);
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
                ? (int)bytes.Length
                : headerEnd + 4;
            while (bytes.Length - bodyStart < contentLength)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                bytes.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(
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

        private static IDictionary<string, object> ParseBody(string request)
        {
            int offset = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string body = offset < 0 ? string.Empty : request.Substring(offset + 4);
            return new JavaScriptSerializer().DeserializeObject(body) as
                IDictionary<string, object>;
        }

        private static Exception WaitForFailure(Task task, TimeSpan timeout)
        {
            try
            {
                if (!task.Wait(timeout))
                {
                    return new TimeoutException("MCP test timed out.");
                }

                return null;
            }
            catch (AggregateException exception)
            {
                return Unwrap(exception);
            }
        }

        private static Exception Unwrap(Exception exception)
        {
            Exception current = exception;
            while (current is AggregateException && current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current;
        }

        private static bool WaitForFile(string path, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            return File.Exists(path);
        }

        private static bool WaitForProcessExit(
            int processId,
            TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (System.Diagnostics.Process process =
                        System.Diagnostics.Process.GetProcessById(processId))
                    {
                        if (process.HasExited)
                        {
                            return true;
                        }
                    }
                }
                catch (ArgumentException)
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            try
            {
                using (System.Diagnostics.Process process =
                    System.Diagnostics.Process.GetProcessById(processId))
                {
                    return process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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
    }
}
