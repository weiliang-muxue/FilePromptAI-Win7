using System;
using System.IO;
using System.Reflection;

internal static class ExtractorSmokeTest
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: ExtractorSmokeTest <FilePromptAI.exe> <files...>");
            return 2;
        }

        string applicationPath = Path.GetFullPath(args[0]);
        if (!File.Exists(applicationPath))
        {
            Console.Error.WriteLine(
                "Application was not found: " + applicationPath);
            return 2;
        }

        string applicationDirectory = Path.GetDirectoryName(applicationPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            string name = new AssemblyName(eventArgs.Name).Name + ".dll";
            string candidate = Path.Combine(applicationDirectory, name);
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
        foreach (string library in Directory.GetFiles(
            applicationDirectory,
            "*.dll"))
        {
            try
            {
                Assembly.LoadFrom(library);
            }
            catch
            {
                // Optional components are loaded again when their feature is used.
            }
        }

        Assembly application = Assembly.LoadFrom(applicationPath);
        Type extractorType = application.GetType(
            "FilePromptAIWin7.FileContentExtractor",
            true);
        object extractor = Activator.CreateInstance(extractorType, true);
        MethodInfo extractFile = extractorType.GetMethod(
            "ExtractFile",
            BindingFlags.Instance | BindingFlags.Public);
        if (extractFile == null)
        {
            Console.Error.WriteLine("ExtractFile method was not found.");
            return 3;
        }

        int failures = 0;
        for (int argumentIndex = 1;
            argumentIndex < args.Length;
            argumentIndex++)
        {
            string path = args[argumentIndex];
            try
            {
                object item = extractFile.Invoke(extractor, new object[] { path });
                Type itemType = item.GetType();
                string name = Convert.ToString(
                    itemType.GetProperty("Name").GetValue(item, null));
                object kind = itemType.GetProperty("Kind").GetValue(item, null);
                string text = Convert.ToString(
                    itemType.GetProperty("TextContent").GetValue(item, null));
                byte[] binary = itemType.GetProperty("BinaryData")
                    .GetValue(item, null) as byte[];
                Console.WriteLine(
                    "PASS | {0} | {1} | text={2} | bytes={3}",
                    name,
                    kind,
                    text == null ? 0 : text.Length,
                    binary == null ? 0 : binary.Length);
                if (!string.IsNullOrEmpty(text))
                {
                    string preview = text.Length > 120
                        ? text.Substring(0, 120)
                        : text;
                    Console.WriteLine(preview.Replace("\r", " ").Replace("\n", " "));
                }
            }
            catch (TargetInvocationException exception)
            {
                failures++;
                Exception inner = exception.InnerException ?? exception;
                Console.Error.WriteLine(
                    "FAIL | {0} | {1}",
                    Path.GetFileName(path),
                    inner);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine(
                    "FAIL | {0} | {1}",
                    Path.GetFileName(path),
                    exception);
            }
        }

        return failures == 0 ? 0 : 1;
    }
}
