using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class FakeMcpServer
{
    private static int Main(string[] args)
    {
        if (args != null && args.Contains("--child-loop"))
        {
            string marker = GetArgumentValue(args, "--child-loop");
            File.WriteAllText(
                marker,
                Process.GetCurrentProcess().Id.ToString(),
                new UTF8Encoding(false));
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        JavaScriptSerializer json = new JavaScriptSerializer();
        json.MaxJsonLength = 16 * 1024 * 1024;
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        string argumentSummary = string.Join("|", args ?? new string[0]);
        bool hang = args != null && args.Contains("--hang");
        try
        {
            if (args != null && args.Contains("--spawn-child"))
            {
                string marker = GetArgumentValue(args, "--spawn-child");
                ProcessStartInfo child = new ProcessStartInfo();
                child.FileName = Assembly.GetExecutingAssembly().Location;
                child.Arguments = "--child-loop " + QuoteArgument(marker);
                child.UseShellExecute = false;
                child.CreateNoWindow = true;
                Process.Start(child);
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(marker) && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(20);
                }
            }

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                IDictionary<string, object> request =
                    json.DeserializeObject(line) as IDictionary<string, object>;
                if (request == null)
                {
                    return 2;
                }

                string method = GetString(request, "method");
                if (method == "notifications/initialized")
                {
                    continue;
                }

                if (hang)
                {
                    System.Threading.Thread.Sleep(
                        System.Threading.Timeout.Infinite);
                }

                object result;
                if (method == "initialize")
                {
                    result = new Dictionary<string, object>
                    {
                        { "protocolVersion", "2024-11-05" },
                        { "capabilities", new Dictionary<string, object>() },
                        {
                            "serverInfo",
                            new Dictionary<string, object>
                            {
                                { "name", "fake-stdio" },
                                { "version", "1.0" }
                            }
                        }
                    };
                }
                else if (method == "tools/list")
                {
                    result = new Dictionary<string, object>
                    {
                        {
                            "tools",
                            new object[]
                            {
                                CreateTool("lookup")
                            }
                        }
                    };
                }
                else if (method == "tools/call")
                {
                    IDictionary<string, object> parameters =
                        GetDictionary(request, "params");
                    IDictionary<string, object> arguments =
                        GetDictionary(parameters, "arguments");
                    string query = GetString(arguments, "query");
                    string environment = Environment.GetEnvironmentVariable(
                        "FILEPROMPT_MCP_TEST") ?? string.Empty;
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
                                        "stdio:" + query + ":" + environment +
                                        ":" + argumentSummary + ":" +
                                        Directory.GetCurrentDirectory()
                                    }
                                },
                                new Dictionary<string, object>
                                {
                                    { "type", "image" },
                                    { "data", "not-real-binary" },
                                    { "mimeType", "image/png" }
                                }
                            }
                        },
                        { "isError", false }
                    };
                }
                else
                {
                    Write(json, new Dictionary<string, object>
                    {
                        { "jsonrpc", "2.0" },
                        { "id", GetValue(request, "id") },
                        {
                            "error",
                            new Dictionary<string, object>
                            {
                                { "code", -32601 },
                                { "message", "unknown method" }
                            }
                        }
                    });
                    continue;
                }

                Write(json, new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", GetValue(request, "id") },
                    { "result", result }
                });
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string GetArgumentValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length ||
            string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException("Missing argument for " + name + ".");
        }

        return args[index + 1];
    }

    private static string QuoteArgument(string argument)
    {
        string value = argument ?? string.Empty;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static IDictionary<string, object> CreateTool(string name)
    {
        return new Dictionary<string, object>
        {
            { "name", name },
            { "description", "Looks up explicitly supplied text." },
            {
                "inputSchema",
                new Dictionary<string, object>
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
                    { "required", new object[] { "query" } }
                }
            }
        };
    }

    private static void Write(JavaScriptSerializer json, object value)
    {
        Console.WriteLine(json.Serialize(value));
        Console.Out.Flush();
    }

    private static IDictionary<string, object> GetDictionary(
        IDictionary<string, object> value,
        string key)
    {
        return GetValue(value, key) as IDictionary<string, object>;
    }

    private static object GetValue(
        IDictionary<string, object> value,
        string key)
    {
        object result;
        return value != null && value.TryGetValue(key, out result)
            ? result
            : null;
    }

    private static string GetString(
        IDictionary<string, object> value,
        string key)
    {
        object result = GetValue(value, key);
        return result == null ? string.Empty : Convert.ToString(result);
    }
}
