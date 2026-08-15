using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FilePromptAIWin7
{
    internal static class XMindExportSmokeTest
    {
        private static void Main()
        {
            string markdown =
                "# XMind 导出\n\n" +
                "说明段落，支持中文和多行。\n第二行内容。\n\n" +
                "## 任务\n\n" +
                "- 设计\n" +
                "- 测试\n\n" +
                "| 名称 | 值 |\n| --- | --- |\n| 中文 | 通过 |";
            byte[] content = XMindExporter.Create(markdown);
            AssertTrue(content.Length > 500, "XMind package has content");

            using (MemoryStream memory = new MemoryStream(content))
            using (ZipArchive archive = new ZipArchive(
                memory,
                ZipArchiveMode.Read,
                false,
                Encoding.UTF8))
            {
                AssertTrue(archive.GetEntry("mimetype") != null, "mimetype entry");
                AssertTrue(archive.GetEntry("content.xml") != null, "content.xml entry");
                AssertTrue(archive.GetEntry("styles.xml") != null, "styles.xml entry");
                AssertTrue(archive.GetEntry("meta.xml") != null, "meta.xml entry");
                AssertTrue(
                    archive.GetEntry("META-INF/manifest.xml") != null,
                    "manifest entry");

                string mimetype = ReadEntry(archive.GetEntry("mimetype"));
                AssertEqual("application/xmind", mimetype, "XMind mimetype");
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith(
                        ".xml",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    XmlDocument parsed = new XmlDocument();
                    parsed.LoadXml(ReadEntry(entry));
                }

                string contentXml = ReadEntry(archive.GetEntry("content.xml"));
                AssertContains(contentXml, "XMind 导出", "root title");
                AssertContains(contentXml, "设计", "list item");
                AssertContains(contentXml, "中文", "table cell");
                AssertContains(contentXml, "第二行内容。", "paragraph notes");
            }

            string outputPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "xmind-export-smoke.xmind");
            XMindExporter.Export(markdown, outputPath);
            AssertTrue(File.ReadAllBytes(outputPath).Length > 500, "XMind file export");
            Console.WriteLine("PASS | XMind export smoke");
        }

        private static string ReadEntry(ZipArchiveEntry entry)
        {
            using (Stream stream = entry.Open())
            using (StreamReader reader = new StreamReader(
                stream,
                Encoding.UTF8,
                true))
            {
                return reader.ReadToEnd();
            }
        }

        private static void AssertContains(
            string actual,
            string expected,
            string name)
        {
            if (actual == null ||
                actual.IndexOf(expected, StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    name + " did not contain [" + expected + "].");
            }
        }

        private static void AssertEqual(
            string expected,
            string actual,
            string name)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    name + " mismatch. Expected [" + expected + "] but got [" +
                    actual + "].");
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
}
