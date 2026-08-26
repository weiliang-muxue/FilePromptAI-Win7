using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

using FilePromptAIWin7;

internal static class PresentationMindMapSmokeTest
{
    private static int Main()
    {
        try
        {
            string markdown =
                "# 离线导出测试\n\n" +
                "## 目标\n\n" +
                "- 生成 PowerPoint\n" +
                "- 生成 XMind\n\n" +
                "| 名称 | 状态 |\n| --- | --- |\n| Word | 已有 |\n| Excel | 已有 |";

            TestPowerPoint(markdown);
            TestPowerPointLongContentAndPagination();
            TestPowerPointTwentyItemListPagination();
            TestPowerPointLongTitleAndTextElements();
            TestPowerPointPackageValidityAndInvalidCharacters();
            TestXMind(markdown);
            Console.WriteLine("PASS | presentation and mind-map export");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | presentation and mind-map export");
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
    }

    private static void TestPowerPoint(string markdown)
    {
        byte[] content = PptxExporter.Create(markdown);
        AssertTrue(content.Length > 1000, "pptx has content");
        using (MemoryStream memory = new MemoryStream(content))
        using (ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Read))
        {
            AssertTrue(archive.GetEntry("[Content_Types].xml") != null, "pptx content types");
            AssertTrue(archive.GetEntry("ppt/presentation.xml") != null, "pptx presentation");
            AssertTrue(archive.GetEntry("ppt/slides/slide1.xml") != null, "pptx first slide");
            string slide = ReadEntry(archive.GetEntry("ppt/slides/slide1.xml"));
            AssertContains(slide, "PowerPoint", "pptx slide XML");
            ParseXml(ReadEntry(archive.GetEntry("ppt/presentation.xml")), "pptx presentation XML");
            ParseXml(slide, "pptx slide XML");
        }
    }

    private static void TestXMind(string markdown)
    {
        byte[] content = XMindExporter.Create(markdown);
        AssertTrue(content.Length > 500, "xmind has content");
        using (MemoryStream memory = new MemoryStream(content))
        using (ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Read))
        {
            ZipArchiveEntry mime = archive.GetEntry("mimetype");
            AssertTrue(mime != null, "xmind mimetype");
            AssertEqual("application/xmind", ReadEntry(mime), "xmind mimetype value");
            ZipArchiveEntry contentEntry = archive.GetEntry("content.xml");
            AssertTrue(contentEntry != null, "xmind content");
            string xml = ReadEntry(contentEntry);
            AssertContains(xml, "离线导出测试", "xmind root title");
            AssertContains(xml, "生成 PowerPoint", "xmind list topic");
            ParseXml(xml, "xmind content XML");
            AssertTrue(archive.GetEntry("META-INF/manifest.xml") != null, "xmind manifest");
        }
    }

    private static void TestPowerPointLongContentAndPagination()
    {
        string longLine =
            "LONG-LINE-BEGIN<" +
            new string('A', 80) +
            "中文段落😀&<标签>" +
            new string('B', 100) +
            ">&LONG-LINE-END";
        AssertTrue(longLine.Length > 155, "long line regression input");

        StringBuilder markdown = new StringBuilder();
        markdown.Append("# 长文本分页\n\n");
        markdown.Append(longLine).Append("\n\n");
        markdown.Append("## 长列表\n\n");
        for (int index = 1; index <= 50; index++)
        {
            markdown.Append("- 列表项-");
            markdown.Append(index.ToString("00"));
            markdown.Append("-UNIQUE\n");
        }

        string outputPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "pptx-pagination-regression.pptx");
        PptxExporter.Export(markdown.ToString(), outputPath);
        byte[] content = File.ReadAllBytes(outputPath);
        AssertTrue(content.Length > 1000, "pptx pagination file export");
        using (MemoryStream memory = new MemoryStream(content))
        using (ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Read))
        {
            int slideCount = CountSlides(archive);
            AssertTrue(slideCount >= 6, "pptx paginates long content");

            StringBuilder firstBody = new StringBuilder();
            StringBuilder allBody = new StringBuilder();
            int listSlideCount = 0;
            bool foundContinuationTitle = false;
            for (int slideNumber = 1;
                slideNumber <= slideCount;
                slideNumber++)
            {
                string slideXml = ReadEntry(
                    archive.GetEntry(
                        "ppt/slides/slide" +
                        slideNumber.ToString() +
                        ".xml"));
                ParseXml(slideXml, "pptx paginated slide XML");

                IList<string> bodyParagraphs =
                    ReadShapeParagraphs(slideXml, "正文");
                AssertTrue(
                    bodyParagraphs.Count <= 10,
                    "pptx body line capacity on slide " +
                    slideNumber.ToString());

                StringBuilder slideBody = new StringBuilder();
                foreach (string paragraph in bodyParagraphs)
                {
                    slideBody.Append(paragraph);
                    allBody.Append(paragraph);
                    if (slideNumber == 1)
                    {
                        firstBody.Append(paragraph);
                    }
                }

                if (slideBody.ToString().IndexOf(
                    "列表项-",
                    StringComparison.Ordinal) >= 0)
                {
                    listSlideCount++;
                }

                IList<string> titleParagraphs =
                    ReadShapeParagraphs(slideXml, "标题");
                foreach (string title in titleParagraphs)
                {
                    if (title.IndexOf(
                        "长列表（续 2）",
                        StringComparison.Ordinal) >= 0)
                    {
                        foundContinuationTitle = true;
                    }
                }
            }

            AssertContains(
                firstBody.ToString(),
                longLine,
                "pptx preserves a line longer than 155 characters");
            AssertTrue(
                listSlideCount >= 5,
                "pptx spreads a 50-item list across slides");
            AssertTrue(
                foundContinuationTitle,
                "pptx labels continuation slides");

            string bodyText = allBody.ToString();
            for (int index = 1; index <= 50; index++)
            {
                string token = "列表项-" +
                    index.ToString("00") +
                    "-UNIQUE";
                AssertEqual(
                    "1",
                    CountOccurrences(bodyText, token).ToString(),
                    "pptx list item occurrence " + index.ToString("00"));
            }
        }
    }

    private static void TestPowerPointTwentyItemListPagination()
    {
        StringBuilder markdown = new StringBuilder("# 二十项列表\n\n");
        for (int index = 1; index <= 20; index++)
        {
            markdown.Append("- 边界列表项-");
            markdown.Append(index.ToString("00"));
            markdown.Append("-UNIQUE\n");
        }

        byte[] content = PptxExporter.Create(markdown.ToString());
        using (MemoryStream memory = new MemoryStream(content))
        using (ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Read))
        {
            AssertEqual(
                "2",
                CountSlides(archive).ToString(),
                "pptx paginates a 20-item list into two slides");

            StringBuilder bodyText = new StringBuilder();
            for (int slideNumber = 1; slideNumber <= 2; slideNumber++)
            {
                string slideXml = ReadEntry(
                    archive.GetEntry(
                        "ppt/slides/slide" +
                        slideNumber.ToString() +
                        ".xml"));
                IList<string> bodyParagraphs =
                    ReadShapeParagraphs(slideXml, "正文");
                AssertEqual(
                    "10",
                    bodyParagraphs.Count.ToString(),
                    "pptx 20-item list page capacity");
                foreach (string paragraph in bodyParagraphs)
                {
                    bodyText.Append(paragraph);
                }
            }

            for (int index = 1; index <= 20; index++)
            {
                string token = "边界列表项-" +
                    index.ToString("00") +
                    "-UNIQUE";
                AssertEqual(
                    "1",
                    CountOccurrences(bodyText.ToString(), token).ToString(),
                    "pptx 20-item list occurrence " +
                    index.ToString("00"));
            }
        }
    }

    private static void TestPowerPointPackageValidityAndInvalidCharacters()
    {
        string markdown =
            "# XML 安全\n\n" +
            "before\u0000middle\u000Bafter\uD800 emoji 😀 end\uFFFF";
        byte[] content = PptxExporter.Create(markdown);
        using (MemoryStream memory = new MemoryStream(content))
        using (ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(
                        ".xml",
                        StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.EndsWith(
                        ".rels",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ParseXml(
                    ReadEntry(entry),
                    "pptx package XML " + entry.FullName);
            }

            string slideXml = ReadEntry(
                archive.GetEntry("ppt/slides/slide1.xml"));
            IList<string> body = ReadShapeParagraphs(slideXml, "正文");
            AssertEqual(
                "before�middle�after� emoji 😀 end�",
                string.Join(string.Empty, body),
                "pptx invalid XML character replacement");

            string masterXml = ReadEntry(
                archive.GetEntry("ppt/slideMasters/slideMaster1.xml"));
            XmlDocument master = ParseXmlDocument(
                masterXml,
                "pptx slide master XML");
            XmlNamespaceManager masterNamespaces =
                CreatePresentationNamespaces(master);
            XmlNode layout = master.SelectSingleNode(
                "//p:sldLayoutId",
                masterNamespaces);
            AssertTrue(layout != null, "pptx master layout ID");
            ulong layoutId;
            AssertTrue(
                ulong.TryParse(
                    layout.Attributes["id"].Value,
                    out layoutId) &&
                layoutId >= 2147483648UL,
                "pptx master layout ID range");

            string themeXml = ReadEntry(
                archive.GetEntry("ppt/theme/theme1.xml"));
            XmlDocument theme = ParseXmlDocument(
                themeXml,
                "pptx theme XML");
            XmlNamespaceManager themeNamespaces =
                CreatePresentationNamespaces(theme);
            AssertEqual(
                "3",
                theme.SelectNodes(
                    "//a:fmtScheme/a:fillStyleLst/*",
                    themeNamespaces).Count.ToString(),
                "pptx theme fill styles");
            AssertEqual(
                "3",
                theme.SelectNodes(
                    "//a:fmtScheme/a:lnStyleLst/*",
                    themeNamespaces).Count.ToString(),
                "pptx theme line styles");
            AssertEqual(
                "3",
                theme.SelectNodes(
                    "//a:fmtScheme/a:effectStyleLst/*",
                    themeNamespaces).Count.ToString(),
                "pptx theme effect styles");
            AssertEqual(
                "3",
                theme.SelectNodes(
                    "//a:fmtScheme/a:bgFillStyleLst/*",
                    themeNamespaces).Count.ToString(),
                "pptx theme background fill styles");
        }
    }

    private static void TestPowerPointLongTitleAndTextElements()
    {
        string combining = "e\u0301";
        string variationSequence = "\u2764\uFE0F";
        string joinedEmoji =
            "\uD83D\uDC69\u200D\uD83D\uDCBB";
        string modifiedEmoji =
            "\uD83D\uDC69\uD83C\uDFFD";
        string regionalFlag =
            "\uD83C\uDDE8\uD83C\uDDF3";
        string title =
            "LONG-TITLE-BEGIN-" +
            new string('T', 42) +
            combining + variationSequence + joinedEmoji +
            modifiedEmoji + regionalFlag +
            new string('U', 42) +
            "-LONG-TITLE-END";
        string bodyElement = combining + variationSequence + joinedEmoji;
        string body = new string('A', 86) + modifiedEmoji +
            new string('B', 86) + regionalFlag +
            new string('C', 86) + bodyElement +
            new string('D', 86);

        byte[] content = PptxExporter.Create(
            "# " + title + "\n\n" + body);
        using (MemoryStream memory = new MemoryStream(content))
        using (ZipArchive archive = new ZipArchive(
            memory,
            ZipArchiveMode.Read))
        {
            int slideCount = CountSlides(archive);
            AssertTrue(slideCount >= 1, "pptx long title slide");
            StringBuilder allTitles = new StringBuilder();
            StringBuilder allBody = new StringBuilder();
            List<string> titleParagraphs = new List<string>();
            List<string> bodyParagraphs = new List<string>();
            for (int slideNumber = 1;
                slideNumber <= slideCount;
                slideNumber++)
            {
                string slideXml = ReadEntry(
                    archive.GetEntry(
                        "ppt/slides/slide" +
                        slideNumber.ToString() +
                        ".xml"));
                AssertContains(
                    slideXml,
                    "<a:normAutofit/>",
                    "pptx title autofit");
                foreach (string paragraph in ReadShapeParagraphs(
                    slideXml,
                    "标题"))
                {
                    titleParagraphs.Add(paragraph);
                    allTitles.Append(paragraph);
                }
                foreach (string paragraph in ReadShapeParagraphs(
                    slideXml,
                    "正文"))
                {
                    AssertTrue(
                        !StartsOrEndsInsideTextElement(paragraph),
                        "pptx body paragraph keeps text elements intact");
                    bodyParagraphs.Add(paragraph);
                    allBody.Append(paragraph);
                }
            }

            AssertSequenceNotSplitAcrossParagraphs(
                titleParagraphs,
                modifiedEmoji,
                "pptx title keeps emoji modifier sequence intact");
            AssertSequenceNotSplitAcrossParagraphs(
                titleParagraphs,
                regionalFlag,
                "pptx title keeps regional-indicator flag intact");
            AssertSequenceNotSplitAcrossParagraphs(
                bodyParagraphs,
                modifiedEmoji,
                "pptx body keeps emoji modifier sequence intact");
            AssertSequenceNotSplitAcrossParagraphs(
                bodyParagraphs,
                regionalFlag,
                "pptx body keeps regional-indicator flag intact");

            AssertEqual(
                title,
                allTitles.ToString(),
                "pptx preserves wrapped long title");
            AssertEqual(
                body,
                allBody.ToString(),
                "pptx preserves combining, variation and ZWJ sequences");
        }
    }

    private static void AssertSequenceNotSplitAcrossParagraphs(
        IList<string> paragraphs,
        string sequence,
        string name)
    {
        StringBuilder combined = new StringBuilder();
        HashSet<int> boundaries = new HashSet<int>();
        for (int index = 0; index < paragraphs.Count; index++)
        {
            combined.Append(paragraphs[index] ?? string.Empty);
            if (index + 1 < paragraphs.Count)
            {
                boundaries.Add(combined.Length);
            }
        }

        string value = combined.ToString();
        int occurrence = value.IndexOf(sequence, StringComparison.Ordinal);
        AssertTrue(occurrence >= 0, name + " sequence exists");
        while (occurrence >= 0)
        {
            for (int split = 1; split < sequence.Length; split++)
            {
                AssertTrue(
                    !boundaries.Contains(occurrence + split),
                    name);
            }

            occurrence = value.IndexOf(
                sequence,
                occurrence + sequence.Length,
                StringComparison.Ordinal);
        }
    }

    private static bool StartsOrEndsInsideTextElement(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        char first = value[0];
        char last = value[value.Length - 1];
        return first == '\u200D' || first == '\uFE0F' ||
            CharUnicodeInfo.GetUnicodeCategory(first) ==
                UnicodeCategory.NonSpacingMark ||
            last == '\u200D';
    }

    private static int CountSlides(ZipArchive archive)
    {
        int count = 0;
        while (archive.GetEntry(
            "ppt/slides/slide" +
            (count + 1).ToString() +
            ".xml") != null)
        {
            count++;
        }

        return count;
    }

    private static IList<string> ReadShapeParagraphs(
        string slideXml,
        string shapeName)
    {
        XmlDocument document = ParseXmlDocument(
            slideXml,
            "pptx slide XML");
        XmlNamespaceManager namespaces =
            CreatePresentationNamespaces(document);

        XmlNode shape = document.SelectSingleNode(
            "//p:sp[p:nvSpPr/p:cNvPr[@name='" +
            shapeName +
            "']]",
            namespaces);
        AssertTrue(shape != null, "pptx shape " + shapeName);

        List<string> paragraphs = new List<string>();
        XmlNodeList nodes = shape.SelectNodes(
            "p:txBody/a:p",
            namespaces);
        foreach (XmlNode node in nodes)
        {
            StringBuilder text = new StringBuilder();
            XmlNodeList runs = node.SelectNodes(".//a:t", namespaces);
            foreach (XmlNode run in runs)
            {
                text.Append(run.InnerText);
            }
            paragraphs.Add(text.ToString());
        }

        return paragraphs;
    }

    private static XmlNamespaceManager CreatePresentationNamespaces(
        XmlDocument document)
    {
        XmlNamespaceManager namespaces =
            new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace(
            "p",
            "http://schemas.openxmlformats.org/presentationml/2006/main");
        namespaces.AddNamespace(
            "a",
            "http://schemas.openxmlformats.org/drawingml/2006/main");
        return namespaces;
    }

    private static int CountOccurrences(string value, string expected)
    {
        int count = 0;
        int offset = 0;
        while (offset <= value.Length - expected.Length)
        {
            int index = value.IndexOf(
                expected,
                offset,
                StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            count++;
            offset = index + expected.Length;
        }

        return count;
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using (Stream stream = entry.Open())
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
        {
            return reader.ReadToEnd();
        }
    }

    private static void ParseXml(string value, string name)
    {
        ParseXmlDocument(value, name);
    }

    private static XmlDocument ParseXmlDocument(
        string value,
        string name)
    {
        XmlDocument document = new XmlDocument();
        document.LoadXml(value);
        AssertTrue(document.DocumentElement != null, name + " root");
        return document;
    }

    private static void AssertContains(string actual, string expected, string name)
    {
        if (actual == null || actual.IndexOf(expected, StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(name + " missing: " + expected);
        }
    }

    private static void AssertEqual(string expected, string actual, string name)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(name + " mismatch.");
        }
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " failed.");
        }
    }
}
