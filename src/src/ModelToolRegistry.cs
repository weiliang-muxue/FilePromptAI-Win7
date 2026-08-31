using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace FilePromptAIWin7
{
    internal sealed class ModelToolRegistry
    {
        private readonly List<McpToolDefinition> tools;
        private readonly Dictionary<string, ToolRegistration> registrations;

        public ModelToolRegistry()
        {
            tools = new List<McpToolDefinition>();
            registrations = new Dictionary<string, ToolRegistration>(
                StringComparer.OrdinalIgnoreCase);
        }

        public IList<McpToolDefinition> Tools
        {
            get
            {
                return new ReadOnlyCollection<McpToolDefinition>(
                    new List<McpToolDefinition>(tools));
            }
        }

        public void AddSource(
            string sourceName,
            IList<McpToolDefinition> sourceTools,
            Func<ModelToolCall, CancellationToken, Task<McpToolResult>>
                executeTool)
        {
            string normalizedSource = string.IsNullOrWhiteSpace(sourceName)
                ? "未命名工具源"
                : sourceName.Trim();
            if (sourceTools == null || sourceTools.Count == 0)
            {
                return;
            }

            if (executeTool == null)
            {
                throw new ArgumentNullException("executeTool");
            }

            HashSet<string> pendingNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (McpToolDefinition tool in sourceTools)
            {
                string name = tool == null
                    ? string.Empty
                    : (tool.PublicName ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(name))
                {
                    throw new InvalidOperationException(
                        "工具源“" + normalizedSource + "”包含未命名工具。");
                }

                ToolRegistration existing;
                if (registrations.TryGetValue(name, out existing))
                {
                    throw new InvalidOperationException(
                        "工具名称“" + name + "”同时来自“" +
                        existing.SourceName + "”和“" + normalizedSource +
                        "”，无法安全合并。");
                }

                if (!pendingNames.Add(name))
                {
                    throw new InvalidOperationException(
                        "工具源“" + normalizedSource + "”重复定义了“" +
                        name + "”。");
                }
            }

            foreach (McpToolDefinition tool in sourceTools)
            {
                string name = tool.PublicName.Trim();
                tools.Add(tool);
                registrations.Add(
                    name,
                    new ToolRegistration
                    {
                        SourceName = normalizedSource,
                        Tool = tool,
                        ExecuteTool = executeTool
                    });
            }
        }

        public McpToolDefinition GetTool(string publicName)
        {
            ToolRegistration registration;
            return !string.IsNullOrWhiteSpace(publicName) &&
                registrations.TryGetValue(publicName, out registration)
                ? registration.Tool
                : null;
        }

        public Task<McpToolResult> ExecuteAsync(
            ModelToolCall call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (call == null || string.IsNullOrWhiteSpace(call.Name))
            {
                throw new McpException("模型请求了无效的工具调用。");
            }

            ToolRegistration registration;
            if (!registrations.TryGetValue(call.Name, out registration))
            {
                throw new McpException(
                    "模型请求了未授权工具：“" + call.Name + "”。");
            }

            return registration.ExecuteTool(call, cancellationToken);
        }

        private sealed class ToolRegistration
        {
            public string SourceName { get; set; }
            public McpToolDefinition Tool { get; set; }
            public Func<ModelToolCall, CancellationToken, Task<McpToolResult>>
                ExecuteTool { get; set; }
        }
    }
}
