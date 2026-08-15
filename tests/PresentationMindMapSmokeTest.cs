using System;
using System.Collections.Generic;
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
        XmlDocument document = new XmlDocument();
        document.LoadXml(value);
        AssertTrue(document.DocumentElement != null, name + " root");
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
