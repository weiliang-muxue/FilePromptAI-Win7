using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FilePromptWin7
{
    internal static class ToolLoopSmokeTest
    {
        private const string ToolName = "mcp__test__lookup";

        private static int Main()
        {
            try
            {
                ServicePointManager.Expect100Continue = false;
                TestSuccessfulToolLoop();
                TestToolRoundLimit();
                TestToolCancellation();
                Console.WriteLine("PASS | model tool loop");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL | model tool loop");
                Console.Error.WriteLine(Unwrap(exception));
                return 1;
            }
        }

        private static void TestSuccessfulToolLoop()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<IList<string>> server = Task.Factory.StartNew(
                delegate
                {
                    List<string> requests = new List<string>();
                    requests.Add(HandleResponse(
                        listener,
                        CreateToolCallResponse("call_success")));
                    requests.Add(HandleResponse(
                        listener,
                        CreateFinalResponse("工具调用完成")));
                    return (IList<string>)requests;
                });

            int callbackCount = 0;
            StringBuilder delta = new StringBuilder();
            using (ModelClient client = new ModelClient())
            {
                ModelRequest request = CreateRequest(port, "/tools-success");
                string result = client.GenerateWithToolsAsync(
                    request,
                    CreateTools(),
                    delegate(
                        ModelToolCall call,
                        CancellationToken cancellationToken)
                    {
                        callbackCount++;
                        Assert(call.Id == "call_success", "tool call id");
                        Assert(call.Name == ToolName, "tool call name");
                        Assert(
                            call.ArgumentsJson.IndexOf(
                                "needle",
                                StringComparison.Ordinal) >= 0,
                            "tool arguments");
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult(new McpToolResult
                        {
                            Content = "lookup-result",
                            IsError = false
                        });
                    },
                    delegate(string value) { delta.Append(value); },
                    delegate { },
                    CancellationToken.None).GetAwaiter().GetResult();

                Assert(result == "工具调用完成", "final tool text");
                Assert(delta.ToString() == result, "final delta callback");
            }

            Assert(callbackCount == 1, "single tool callback");
            Assert(server.Wait(TimeSpan.FromSeconds(5)), "tool server completed");
            InspectFirstRequest(server.Result[0]);
            InspectSecondRequest(server.Result[1]);
        }

        private static void TestToolRoundLimit()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = Task.Factory.StartNew(
                delegate
                {
                    for (int index = 0; index < 9; index++)
                    {
                        HandleResponse(
                            listener,
                            CreateToolCallResponse("call_limit_" + index));
                    }
                });

            int callbackCount = 0;
            Exception failure;
            using (ModelClient client = new ModelClient())
            {
                Task<string> task = client.GenerateWithToolsAsync(
                    CreateRequest(port, "/tools-limit"),
                    CreateTools(),
                    delegate(
                        ModelToolCall call,
                        CancellationToken cancellationToken)
                    {
                        callbackCount++;
                        return Task.FromResult(new McpToolResult
                        {
                            Content = "ok"
                        });
                    },
                    null,
                    null,
                    CancellationToken.None);
                failure = WaitForFailure(task, TimeSpan.FromSeconds(10));
            }

            listener.Stop();
            Assert(server.Wait(TimeSpan.FromSeconds(5)), "limit server completed");
            Assert(failure is ModelCallException, "round limit exception type");
            Assert(
                failure.Message.IndexOf("8 轮", StringComparison.Ordinal) >= 0,
                "round limit guidance");
            Assert(callbackCount == 8, "round limit callback count");
        }

        private static void TestToolCancellation()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = Task.Factory.StartNew(
                delegate
                {
                    HandleResponse(
                        listener,
                        CreateToolCallResponse("call_cancel"));
                });
            ManualResetEventSlim callbackStarted =
                new ManualResetEventSlim(false);
            CancellationTokenSource cancellation =
                new CancellationTokenSource();
            Exception failure;
            using (ModelClient client = new ModelClient())
            {
                Task<string> task = client.GenerateWithToolsAsync(
                    CreateRequest(port, "/tools-cancel"),
                    CreateTools(),
                    delegate(
                        ModelToolCall call,
                        CancellationToken cancellationToken)
                    {
                        callbackStarted.Set();
                        return WaitForCancellation(cancellationToken);
                    },
                    null,
                    null,
                    cancellation.Token);
                Assert(
                    callbackStarted.Wait(TimeSpan.FromSeconds(5)),
                    "cancellable callback started");
                cancellation.Cancel();
                failure = WaitForFailure(task, TimeSpan.FromSeconds(5));
            }

            listener.Stop();
            server.Wait(TimeSpan.FromSeconds(5));
            callbackStarted.Dispose();
            cancellation.Dispose();
            Assert(
                failure is OperationCanceledException,
                "tool cancellation exception type");
        }

        private static async Task<McpToolResult> WaitForCancellation(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken)
                .ConfigureAwait(false);
            return new McpToolResult();
        }

        private static ModelRequest CreateRequest(int port, string path)
        {
            return new ModelRequest
            {
                EndpointUrl = "http://127.0.0.1:" + port + path,
                ApiKey = "tool-secret",
                ModelName = "tool-model",
                SystemPrompt = "system instructions",
                Prompt = "current prompt",
                ConversationMessages = new List<ConversationMessage>
                {
                    new ConversationMessage("user", "history user"),
                    new ConversationMessage("assistant", "history assistant")
                },
                Attachments = new List<InputItem>
                {
                    new InputItem
                    {
                        Name = "pasted.png",
                        Kind = InputKind.Image,
                        MimeType = "image/png",
                        BinaryData = new byte[] { 1, 2, 3 }
                    }
                }
            };
        }

        private static IList<McpToolDefinition> CreateTools()
        {
            return new List<McpToolDefinition>
            {
                new McpToolDefinition
                {
                    PublicName = ToolName,
                    Description = "Looks up a value.",
                    InputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        {
                            "properties",
                            new Dictionary<string, object>
                            {
                                {
                                    "query",
                                    new Dictionary<string, object>
                                    {
                                        { "type", "string" }
                                    }
                                }
                            }
                        },
                        { "required", new string[] { "query" } }
                    }
                }
            };
        }

        private static string CreateToolCallResponse(string id)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return json.Serialize(new Dictionary<string, object>
            {
                {
                    "choices",
                    new object[]
                    {
                        new Dictionary<string, object>
                        {
                            {
                                "message",
                                new Dictionary<string, object>
                                {
                                    { "role", "assistant" },
                                    { "content", null },
                                    {
                                        "tool_calls",
                                        new object[]
                                        {
                                            new Dictionary<string, object>
                                            {
                                                { "id", id },
                                                { "type", "function" },
                                                {
                                                    "function",
                                                    new Dictionary<string, object>
                                                    {
                                                        { "name", ToolName },
                                                        {
                                                            "arguments",
                                                            "{\"query\":\"needle\"}"
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            },
                            { "finish_reason", "tool_calls" }
                        }
                    }
                }
            });
        }

        private static string CreateFinalResponse(string text)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return json.Serialize(new Dictionary<string, object>
            {
                {
                    "choices",
                    new object[]
                    {
                        new Dictionary<string, object>
                        {
                            {
                                "message",
                                new Dictionary<string, object>
                                {
                                    { "role", "assistant" },
                                    { "content", text }
                                }
                            },
                            { "finish_reason", "stop" }
                        }
                    }
                }
            });
        }

        private static string HandleResponse(
            TcpListener listener,
            string body)
        {
            using (TcpClient connection = listener.AcceptTcpClient())
            using (NetworkStream stream = connection.GetStream())
            {
                string request = ReadRequest(stream);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                byte[] headers = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    "Content-Length: " + bodyBytes.Length + "\r\n" +
                    "Connection: close\r\n\r\n");
                stream.Write(headers, 0, headers.Length);
                stream.Write(bodyBytes, 0, bodyBytes.Length);
                stream.Flush();
                return request;
            }
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

        private static void InspectFirstRequest(string request)
        {
            IDictionary<string, object> root = ParseBody(request);
            IList messages = root["messages"] as IList;
            IList tools = root["tools"] as IList;
            Assert(Convert.ToBoolean(root["stream"]) == false, "tools non-stream");
            Assert(Convert.ToString(root["tool_choice"]) == "auto", "tool choice auto");
            Assert(tools != null && tools.Count == 1, "tool definition sent");
            Assert(messages != null && messages.Count == 4, "initial message count");
            Assert(GetRole(messages[0]) == "system", "system prompt ordering");
            Assert(GetRole(messages[3]) == "user", "current user ordering");
            IDictionary<string, object> current =
                messages[3] as IDictionary<string, object>;
            Assert(current["content"] is IList, "current multimodal content");
        }

        private static void InspectSecondRequest(string request)
        {
            IDictionary<string, object> root = ParseBody(request);
            IList messages = root["messages"] as IList;
            Assert(messages != null && messages.Count == 6, "tool transcript count");
            IDictionary<string, object> assistant =
                messages[4] as IDictionary<string, object>;
            IDictionary<string, object> tool =
                messages[5] as IDictionary<string, object>;
            Assert(GetRole(assistant) == "assistant", "assistant tool message");
            Assert(assistant["tool_calls"] is IList, "assistant tool_calls preserved");
            Assert(GetRole(tool) == "tool", "tool result role");
            Assert(
                Convert.ToString(tool["tool_call_id"]) == "call_success",
                "tool result id");
            Assert(
                Convert.ToString(tool["content"]) == "lookup-result",
                "tool result content");
        }

        private static string GetRole(object message)
        {
            IDictionary<string, object> value =
                message as IDictionary<string, object>;
            return value == null ? string.Empty : Convert.ToString(value["role"]);
        }

        private static Exception WaitForFailure(Task task, TimeSpan timeout)
        {
            try
            {
                if (!task.Wait(timeout))
                {
                    throw new TimeoutException("Tool loop test timed out.");
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
