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

namespace FilePromptAIWin7
{
    internal static class WorkspaceToolLoopEndToEndSmokeTest
    {
        private const string RelativePath = "selected.cs";
        private const string OriginalText =
            "internal sealed class Before\r\n{\r\n}\r\n";
        private const string EditedText =
            "internal sealed class After\r\n{\r\n}\r\n";

        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };

        private static int Main()
        {
            string testRoot = Path.Combine(
                Path.GetTempPath(),
                "FilePromptAI-WorkspaceToolLoop-" +
                    Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                Path.Combine(testRoot, "data"));
            Directory.CreateDirectory(testRoot);
            try
            {
                ServicePointManager.Expect100Continue = false;
                TestApprovedEdit(testRoot);
                TestRejectedEdit(testRoot);
                Console.WriteLine("PASS | workspace tool loop end to end");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "FAIL | workspace tool loop end to end");
                Console.Error.WriteLine(Unwrap(exception));
                return 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "FILEPROMPTAI_DATA_ROOT",
                    null);
                TryDeleteTree(testRoot);
            }
        }

        private static void TestApprovedEdit(string testRoot)
        {
            string directory = Path.Combine(testRoot, "approved");
            Directory.CreateDirectory(directory);
            string selectedFile = Path.Combine(directory, RelativePath);
            File.WriteAllText(
                selectedFile,
                OriginalText,
                new UTF8Encoding(false));

            int confirmations = 0;
            WorkspaceEditProposal displayedProposal = null;
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selectedFile))
            {
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate(
                            WorkspaceEditProposal proposal,
                            CancellationToken cancellationToken)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            confirmations++;
                            displayedProposal = proposal;
                            return Task.FromResult(true);
                        });
                LoopbackRun run = RunToolLoop(
                    provider,
                    directory,
                    "approved",
                    false);
                Assert(run.FinalText == "approved complete", "approved final text");
                Assert(run.ReadSha256.Length == 64, "approved model used returned SHA");
                Assert(run.EditToolSucceeded, "approved tool result succeeded");
            }

            Assert(confirmations == 1, "approved confirmation invoked once");
            Assert(
                displayedProposal != null &&
                displayedProposal.ExpectedSha256.Length == 64 &&
                displayedProposal.UnifiedDiff.IndexOf(
                    "+internal sealed class After",
                    StringComparison.Ordinal) >= 0,
                "approved diff was shown");
            Assert(
                File.ReadAllText(selectedFile).IndexOf(
                    "class After",
                    StringComparison.Ordinal) >= 0,
                "approved edit changed real file");
        }

        private static void TestRejectedEdit(string testRoot)
        {
            string directory = Path.Combine(testRoot, "rejected");
            Directory.CreateDirectory(directory);
            string selectedFile = Path.Combine(directory, RelativePath);
            File.WriteAllText(
                selectedFile,
                OriginalText,
                new UTF8Encoding(false));

            int confirmations = 0;
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(selectedFile))
            {
                CodeWorkspaceToolProvider provider =
                    new CodeWorkspaceToolProvider(
                        workspace,
                        delegate(
                            WorkspaceEditProposal proposal,
                            CancellationToken cancellationToken)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            confirmations++;
                            return Task.FromResult(false);
                        });
                LoopbackRun run = RunToolLoop(
                    provider,
                    directory,
                    "rejected",
                    true);
                Assert(run.FinalText == "rejected complete", "rejected final text");
                Assert(run.ReadSha256.Length == 64, "rejected model used returned SHA");
                Assert(!run.EditToolSucceeded, "rejected tool result is error");
            }

            Assert(confirmations == 1, "rejected confirmation invoked once");
            Assert(
                string.Equals(
                    File.ReadAllText(selectedFile),
                    OriginalText,
                    StringComparison.Ordinal),
                "rejected confirmation leaves real file unchanged");
        }

        private static LoopbackRun RunToolLoop(
            CodeWorkspaceToolProvider provider,
            string workspaceDirectory,
            string scenario,
            bool expectRejected)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<LoopbackRun> server = Task.Factory.StartNew(
                delegate
                {
                    LoopbackRun run = new LoopbackRun();
                    string firstRequest = HandleResponse(
                        listener,
                        CreateToolCallResponse(
                            "read_" + scenario,
                            CodeWorkspaceToolProvider.ReadFileToolName,
                            Serialize(new Dictionary<string, object>
                            {
                                { "relative_path", RelativePath }
                            })));
                    InspectInitialRequest(
                        firstRequest,
                        workspaceDirectory);

                    PendingRequest secondRequest =
                        ReadRequestFromNextConnection(listener);
                    string readToolContent = GetLastToolContent(
                        secondRequest.RequestText);
                    AssertRequestHidesWorkspacePath(
                        secondRequest.RequestText,
                        workspaceDirectory,
                        scenario + " read tool transcript");
                    IDictionary<string, object> readResult =
                        DeserializeObject(readToolContent);
                    run.ReadSha256 = Convert.ToString(readResult["sha256"]);
                    Assert(
                        Convert.ToString(readResult["relative_path"]) ==
                            RelativePath,
                        scenario + " read relative path");
                    Assert(
                        Convert.ToString(readResult["text"]).IndexOf(
                            "class Before",
                            StringComparison.Ordinal) >= 0,
                        scenario + " read real file text");
                    WriteResponse(
                        secondRequest.Connection,
                        CreateToolCallResponse(
                            "edit_" + scenario,
                            CodeWorkspaceToolProvider.ProposeEditToolName,
                            Serialize(new Dictionary<string, object>
                            {
                                { "relative_path", RelativePath },
                                { "base_sha256", run.ReadSha256 },
                                { "new_text", EditedText }
                            })));

                    string thirdRequest = HandleResponse(
                        listener,
                        CreateFinalResponse(scenario + " complete"));
                    AssertRequestHidesWorkspacePath(
                        thirdRequest,
                        workspaceDirectory,
                        scenario + " edit tool transcript");
                    string editToolContent = GetLastToolContent(thirdRequest);
                    run.EditToolSucceeded = editToolContent.IndexOf(
                        "[工具执行失败]",
                        StringComparison.Ordinal) < 0;
                    if (expectRejected)
                    {
                        Assert(
                            editToolContent.IndexOf(
                                "用户拒绝",
                                StringComparison.Ordinal) >= 0,
                            "rejected error returned to model");
                    }
                    else
                    {
                        IDictionary<string, object> editResult =
                            DeserializeObject(editToolContent);
                        Assert(
                            Convert.ToString(editResult["status"]) == "applied",
                            "approved applied status returned to model");
                    }

                    return run;
                });

            string finalText;
            try
            {
                ModelRequest request = new ModelRequest
                {
                    EndpointUrl = "http://127.0.0.1:" + port +
                        "/v1/chat/completions",
                    ApiKey = "local-loopback-key",
                    ModelName = "local-workspace-test",
                    SystemPrompt = "Use only the provided workspace tools.",
                    Prompt = "Read selected.cs and replace Before with After.",
                    ConversationMessages = new List<ConversationMessage>(),
                    Attachments = new List<InputItem>()
                };
                using (ModelClient client = new ModelClient())
                {
                    finalText = client.GenerateWithToolsAsync(
                        request,
                        provider.Tools,
                        provider.ExecuteAsync,
                        null,
                        null,
                        CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            finally
            {
                listener.Stop();
            }

            Assert(
                server.Wait(TimeSpan.FromSeconds(10)),
                scenario + " loopback server completed");
            LoopbackRun result = server.Result;
            result.FinalText = finalText;
            return result;
        }

        private static void InspectInitialRequest(
            string request,
            string workspaceDirectory)
        {
            IDictionary<string, object> root = ParseRequestBody(request);
            IList tools = root["tools"] as IList;
            Assert(tools != null && tools.Count == 4, "workspace tools sent to model");
            Assert(
                request.IndexOf(
                    CodeWorkspaceToolProvider.ReadFileToolName,
                    StringComparison.Ordinal) >= 0 &&
                request.IndexOf(
                    CodeWorkspaceToolProvider.ProposeEditToolName,
                    StringComparison.Ordinal) >= 0,
                "read and edit definitions sent");
            AssertRequestHidesWorkspacePath(
                request,
                workspaceDirectory,
                "initial request");
        }

        private static void AssertRequestHidesWorkspacePath(
            string request,
            string workspaceDirectory,
            string phase)
        {
            Assert(
                request.IndexOf(
                    workspaceDirectory,
                    StringComparison.OrdinalIgnoreCase) < 0 &&
                request.IndexOf(
                    workspaceDirectory.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase) < 0,
                phase + " hides absolute workspace path");
        }

        private static string GetLastToolContent(string request)
        {
            IDictionary<string, object> root = ParseRequestBody(request);
            IList messages = root["messages"] as IList;
            Assert(messages != null && messages.Count > 0, "tool transcript present");
            IDictionary<string, object> message =
                messages[messages.Count - 1] as IDictionary<string, object>;
            Assert(
                message != null &&
                Convert.ToString(message["role"]) == "tool",
                "last transcript message is tool result");
            return Convert.ToString(message["content"]);
        }

        private static string CreateToolCallResponse(
            string id,
            string toolName,
            string arguments)
        {
            return Serialize(new Dictionary<string, object>
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
                                                        { "name", toolName },
                                                        { "arguments", arguments }
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
            return Serialize(new Dictionary<string, object>
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

        private static string HandleResponse(TcpListener listener, string body)
        {
            PendingRequest pending = ReadRequestFromNextConnection(listener);
            try
            {
                WriteResponse(pending.Connection, body);
                return pending.RequestText;
            }
            finally
            {
                pending.Connection.Dispose();
            }
        }

        private static PendingRequest ReadRequestFromNextConnection(
            TcpListener listener)
        {
            TcpClient connection = listener.AcceptTcpClient();
            try
            {
                return new PendingRequest
                {
                    Connection = connection,
                    RequestText = ReadRequest(connection.GetStream())
                };
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static void WriteResponse(TcpClient connection, string body)
        {
            using (connection)
            {
                NetworkStream stream = connection.GetStream();
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                byte[] headers = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    "Content-Length: " + bodyBytes.Length + "\r\n" +
                    "Connection: close\r\n\r\n");
                stream.Write(headers, 0, headers.Length);
                stream.Write(bodyBytes, 0, bodyBytes.Length);
                stream.Flush();
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

        private static IDictionary<string, object> ParseRequestBody(
            string request)
        {
            int offset = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert(offset >= 0, "HTTP request contains body");
            return DeserializeObject(request.Substring(offset + 4));
        }

        private static IDictionary<string, object> DeserializeObject(
            string value)
        {
            IDictionary<string, object> result =
                Json.DeserializeObject(value) as IDictionary<string, object>;
            Assert(result != null, "JSON object parsed");
            return result;
        }

        private static string Serialize(object value)
        {
            return Json.Serialize(value);
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

        private static void TryDeleteTree(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
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

        private sealed class PendingRequest
        {
            public TcpClient Connection { get; set; }
            public string RequestText { get; set; }
        }

        private sealed class LoopbackRun
        {
            public string ReadSha256 { get; set; }
            public bool EditToolSucceeded { get; set; }
            public string FinalText { get; set; }

            public LoopbackRun()
            {
                ReadSha256 = string.Empty;
                FinalText = string.Empty;
            }
        }
    }
}
