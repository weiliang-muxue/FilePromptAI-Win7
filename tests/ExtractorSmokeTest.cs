using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

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
        string generatedRoot = Path.Combine(
            Path.GetTempPath(),
            "FilePromptAIExtractorSmoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(generatedRoot);
            TestPptx(extractor, extractFile, generatedRoot);
            TestXMind(extractor, extractFile, generatedRoot);
            TestModernXMind(extractor, extractFile, generatedRoot);
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine(
                "FAIL | generated PPTX/XMind | {0}",
                Unwrap(exception));
        }
        finally
        {
            try
            {
                if (Directory.Exists(generatedRoot))
                {
                    Directory.Delete(generatedRoot, true);
                }
            }
            catch
            {
                // Temporary fixtures can be removed on the next run.
            }
        }

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

    private static void TestPptx(
        object extractor,
        MethodInfo extractFile,
        string root)
    {
        string path = Path.Combine(root, "generated.pptx");
        using (FileStream stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        using (ZipArchive archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            false,
            Encoding.UTF8))
        {
            AddEntry(
                archive,
                "ppt/slides/slide10.xml",
                CreateSlideXml("第十页标题", "第十页正文", false));
            AddEntry(
                archive,
                "ppt/slides/slide2.xml",
                CreateSlideXml("第二页标题", "第二页正文", true));
            AddEntry(
                archive,
                "ppt/slides/_rels/slide2.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rIdNotes\" " +
                "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide\" " +
                "Target=\"../notesSlides/notesSlide7.xml\"/>" +
                "</Relationships>");
            AddEntry(
                archive,
                "ppt/notesSlides/notesSlide7.xml",
                "<p:notes xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
                "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                "<p:cSld><p:spTree><p:sp><p:nvSpPr><p:nvPr><p:ph type=\"body\"/>" +
                "</p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>第二页演讲备注</a:t>" +
                "</a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:notes>");
        }

        string text = GetExtractedText(extractor, extractFile, path);
        AssertContains(text, "[标题]\r\n第二页标题", "PPTX title");
        AssertContains(text, "[正文]\r\n第二页正文", "PPTX body");
        AssertContains(text, "[表格 1]\r\n表头A\t表头B", "PPTX table");
        AssertContains(text, "[备注]\r\n第二页演讲备注", "PPTX notes");
        AssertBefore(text, "===== 第 2 页 =====", "===== 第 10 页 =====",
            "PPTX numeric slide order");
        Console.WriteLine("PASS | generated PPTX extraction");
    }

    private static string CreateSlideXml(
        string title,
        string body,
        bool includeTable)
    {
        string table = includeTable
            ? "<p:graphicFrame><a:graphic><a:graphicData><a:tbl>" +
              "<a:tr><a:tc><a:txBody><a:p><a:r><a:t>表头A</a:t></a:r></a:p>" +
              "</a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>表头B</a:t>" +
              "</a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></a:graphicData>" +
              "</a:graphic></p:graphicFrame>"
            : string.Empty;
        return "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
            "<p:cSld><p:spTree>" +
            "<p:sp><p:nvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr>" +
            "<p:txBody><a:p><a:r><a:t>" + title + "</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:nvPr><p:ph type=\"body\"/></p:nvPr></p:nvSpPr>" +
            "<p:txBody><a:p><a:r><a:t>" + body + "</a:t></a:r></a:p></p:txBody></p:sp>" +
            table + "</p:spTree></p:cSld></p:sld>";
    }

    private static void TestXMind(
        object extractor,
        MethodInfo extractFile,
        string root)
    {
        string path = Path.Combine(root, "generated.xmind");
        using (FileStream stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        using (ZipArchive archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            false,
            Encoding.UTF8))
        {
            AddEntry(
                archive,
                "content.xml",
                "<xmap-content xmlns=\"urn:xmind:xmap:xmlns:content:2.0\">" +
                "<sheet><title>项目规划</title><topic><title>根主题</title>" +
                "<notes><plain>根主题备注</plain></notes><children><topics type=\"attached\">" +
                "<topic><title>阶段一</title><children><topics type=\"attached\">" +
                "<topic><title>任务一</title><notes><plain>任务备注</plain></notes></topic>" +
                "</topics></children></topic><topic><title>阶段二</title></topic>" +
                "</topics></children></topic></sheet>" +
                "<sheet><title>风险清单</title><topic><title>风险根节点</title></topic></sheet>" +
                "</xmap-content>");
        }

        string text = GetExtractedText(extractor, extractFile, path);
        AssertContains(text, "===== 画布：项目规划 =====", "XMind first sheet");
        AssertContains(text, "- 根主题\r\n  [备注] 根主题备注", "XMind root notes");
        AssertContains(text, "  - 阶段一\r\n    - 任务一\r\n      [备注] 任务备注",
            "XMind hierarchy and nested notes");
        AssertBefore(text, "===== 画布：项目规划 =====", "===== 画布：风险清单 =====",
            "XMind sheet order");
        AssertBefore(text, "  - 阶段一", "  - 阶段二", "XMind topic order");
        Console.WriteLine("PASS | generated XMind extraction");
    }

    private static void TestModernXMind(
        object extractor,
        MethodInfo extractFile,
        string root)
    {
        string path = Path.Combine(root, "generated-modern.xmind");
        using (FileStream stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        using (ZipArchive archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            false,
            Encoding.UTF8))
        {
            AddEntry(
                archive,
                "content.json",
                "[{\"id\":\"sheet-1\",\"class\":\"sheet\",\"title\":\"现代项目\"," +
                "\"rootTopic\":{\"id\":\"root\",\"class\":\"topic\"," +
                "\"title\":\"中心主题\",\"notes\":{\"plain\":{\"content\":\"中心备注\"}}," +
                "\"children\":{\"attached\":[{\"id\":\"phase\",\"class\":\"topic\"," +
                "\"title\":\"实施阶段\",\"children\":{\"attached\":[{" +
                "\"id\":\"task\",\"class\":\"topic\",\"title\":\"离线验收\"," +
                "\"notes\":{\"plain\":{\"content\":\"Win7 目标机\"}}}]}}]}}}," +
                "{\"id\":\"sheet-2\",\"class\":\"sheet\",\"title\":\"交付\"," +
                "\"rootTopic\":{\"id\":\"delivery\",\"class\":\"topic\"," +
                "\"title\":\"最终包\"}}]");
        }

        string text = GetExtractedText(extractor, extractFile, path);
        AssertContains(text, "===== 画布：现代项目 =====", "modern XMind sheet");
        AssertContains(text, "- 中心主题\r\n  [备注] 中心备注",
            "modern XMind root notes");
        AssertContains(text,
            "  - 实施阶段\r\n    - 离线验收\r\n      [备注] Win7 目标机",
            "modern XMind hierarchy");
        AssertBefore(text, "===== 画布：现代项目 =====", "===== 画布：交付 =====",
            "modern XMind sheet order");
        Console.WriteLine("PASS | modern XMind content.json extraction");
    }

    private static string GetExtractedText(
        object extractor,
        MethodInfo extractFile,
        string path)
    {
        object item = extractFile.Invoke(extractor, new object[] { path });
        return Convert.ToString(item.GetType().GetProperty("TextContent")
            .GetValue(item, null));
    }

    private static void AddEntry(
        ZipArchive archive,
        string name,
        string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using (Stream stream = entry.Open())
        using (StreamWriter writer = new StreamWriter(
            stream,
            new UTF8Encoding(false)))
        {
            writer.Write(content);
        }
    }

    private static void AssertContains(string text, string value, string name)
    {
        if (text.IndexOf(value, StringComparison.Ordinal) < 0)
        {
            throw new InvalidDataException(name + " was not extracted.\r\n" + text);
        }
    }

    private static void AssertBefore(
        string text,
        string first,
        string second,
        string name)
    {
        int firstIndex = text.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = text.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
        {
            throw new InvalidDataException(name + " is incorrect.\r\n" + text);
        }
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while (current is TargetInvocationException &&
            current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current;
    }
}
