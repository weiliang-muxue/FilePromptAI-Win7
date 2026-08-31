using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FilePromptAIWin7
{
    internal static class ModelToolRegistrySmokeTest
    {
        private static int Main()
        {
            try
            {
                TestMultipleSourcesAndRouting();
                TestDuplicateNamesAreRejectedAtomically();
                TestUnknownToolIsRejected();
                Console.WriteLine("PASS | model tool registry");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL | model tool registry");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void TestMultipleSourcesAndRouting()
        {
            ModelToolRegistry registry = new ModelToolRegistry();
            int mcpCalls = 0;
            int workspaceCalls = 0;
            registry.AddSource(
                "MCP",
                CreateTools("mcp__docs__search"),
                delegate(
                    ModelToolCall call,
                    CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    mcpCalls++;
                    return Task.FromResult(new McpToolResult
                    {
                        Content = "mcp:" + call.ArgumentsJson
                    });
                });
            registry.AddSource(
                "代码工作区",
                CreateTools("workspace_read_file", "workspace_propose_edit"),
                delegate(
                    ModelToolCall call,
                    CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    workspaceCalls++;
                    return Task.FromResult(new McpToolResult
                    {
                        Content = "workspace:" + call.Name
                    });
                });

            Assert(registry.Tools.Count == 3, "merged tool count");
            Assert(
                registry.GetTool("WORKSPACE_READ_FILE") != null,
                "case-insensitive lookup");
            McpToolResult mcpResult = registry.ExecuteAsync(
                CreateCall("mcp__docs__search"),
                CancellationToken.None).GetAwaiter().GetResult();
            McpToolResult workspaceResult = registry.ExecuteAsync(
                CreateCall("workspace_read_file"),
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(mcpResult.Content == "mcp:{}", "MCP routing");
            Assert(
                workspaceResult.Content == "workspace:workspace_read_file",
                "workspace routing");
            Assert(mcpCalls == 1 && workspaceCalls == 1, "isolated executors");
        }

        private static void TestDuplicateNamesAreRejectedAtomically()
        {
            ModelToolRegistry registry = new ModelToolRegistry();
            registry.AddSource(
                "MCP",
                CreateTools("shared_tool"),
                CreateExecutor("mcp"));

            Exception failure = null;
            try
            {
                registry.AddSource(
                    "代码工作区",
                    CreateTools("workspace_unique", "SHARED_TOOL"),
                    CreateExecutor("workspace"));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert(failure is InvalidOperationException, "duplicate rejected");
            Assert(
                failure.Message.IndexOf("MCP", StringComparison.Ordinal) >= 0 &&
                failure.Message.IndexOf("代码工作区", StringComparison.Ordinal) >= 0,
                "duplicate source diagnosis");
            Assert(registry.Tools.Count == 1, "duplicate add is atomic");
            Assert(
                registry.GetTool("workspace_unique") == null,
                "failed source not partially registered");
        }

        private static void TestUnknownToolIsRejected()
        {
            ModelToolRegistry registry = new ModelToolRegistry();
            registry.AddSource(
                "代码工作区",
                CreateTools("workspace_read_file"),
                CreateExecutor("workspace"));

            Exception failure = null;
            try
            {
                registry.ExecuteAsync(
                    CreateCall("workspace_delete_everything"),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert(failure is McpException, "unknown tool exception type");
            Assert(
                failure.Message.IndexOf("未授权", StringComparison.Ordinal) >= 0,
                "unknown tool diagnosis");
        }

        private static Func<ModelToolCall, CancellationToken, Task<McpToolResult>>
            CreateExecutor(string content)
        {
            return delegate(
                ModelToolCall call,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new McpToolResult { Content = content });
            };
        }

        private static IList<McpToolDefinition> CreateTools(params string[] names)
        {
            List<McpToolDefinition> tools = new List<McpToolDefinition>();
            foreach (string name in names)
            {
                tools.Add(new McpToolDefinition
                {
                    PublicName = name,
                    Description = name,
                    InputSchema = new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() }
                    }
                });
            }

            return tools;
        }

        private static ModelToolCall CreateCall(string name)
        {
            return new ModelToolCall
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                ArgumentsJson = "{}"
            };
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
