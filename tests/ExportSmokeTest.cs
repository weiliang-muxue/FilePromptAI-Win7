using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

using FilePromptAIWin7;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

internal static class ExportSmokeTest
{
    private static int Main()
    {
        try
        {
            TestMarkdownTableAndCsv();
            TestRaggedCsvAndInvalidCharacters();
            TestCsvFormulaNeutralizationAndAtomicWrite();
            TestDocxXmlAndNumbering();
            TestIndependentOrderedLists();
            TestPdfExport();
            TestXlsxExport();
            Console.WriteLine("PASS | export hardening");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL | export hardening");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void TestMarkdownTableAndCsv()
    {
        string markdown =
            "| \u540D\u79F0 | \u5185\u5BB9 | \u8DEF\u5F84 | \u591A\u884C |\n" +
            "| --- | :--- | ---: | :---: |\n" +
            "| \u4E2D\u6587 | \u9017\u53F7, \u5F15\u53F7\" & <tag> | " +
            "C:\\Temp\\file_name.txt | \u7B2C\u4E00\u884C<br>\u7B2C\u4E8C\u884C |\n" +
            "| pipe | A\\|B | \\\\server\\share\\report_01.docx | `__init__` |";

        MarkdownDocument document = MarkdownDocument.Parse(markdown);
        AssertEqual(1, document.Tables.Count, "table count");
        MarkdownTable table = document.Tables[0];
        AssertEqual(4, table.Headers.Count, "header count");
        AssertEqual(2, table.Rows.Count, "row count");
        AssertEqual(
            "C:\\Temp\\file_name.txt",
            table.Rows[0][2],
            "Windows path and underscore preservation");
        AssertEqual("\u7B2C\u4E00\u884C\n\u7B2C\u4E8C\u884C", table.Rows[0][3], "HTML line break");
        AssertEqual("A|B", table.Rows[1][1], "escaped pipe");
        AssertEqual(
            "\\\\server\\share\\report_01.docx",
            table.Rows[1][2],
            "UNC path preservation");
        AssertEqual("__init__", table.Rows[1][3], "inline code preservation");

        string expected =
            "\u540D\u79F0,\u5185\u5BB9,\u8DEF\u5F84,\u591A\u884C\r\n" +
            "\u4E2D\u6587,\"\u9017\u53F7, \u5F15\u53F7\"\" & <tag>\"," +
            "C:\\Temp\\file_name.txt,\"\u7B2C\u4E00\u884C\n\u7B2C\u4E8C\u884C\"\r\n" +
            "pipe,A|B,\\\\server\\share\\report_01.docx,__init__";
        AssertEqual(expected, CsvExporter.ToCsv(document.Tables[0]), "complex CSV");

        MarkdownDocument oneColumn = MarkdownDocument.Parse(
            "| \u9879\u76EE |\n| --- |\n| \u503C |");
        AssertEqual(1, oneColumn.Tables.Count, "one-column table count");
        AssertEqual(
            "\u9879\u76EE\r\n\u503C",
            CsvExporter.ToCsv(oneColumn.Tables[0]),
            "one-column CSV");

        MarkdownDocument mismatchedColumns = MarkdownDocument.Parse(
            "Not a table | value\n---\nparagraph");
        AssertEqual(
            0,
            mismatchedColumns.Tables.Count,
            "mismatched delimiter is not a table");

        string outputPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "export-hardening.csv");
        CsvExporter.Export(document, outputPath);
        byte[] bytes = File.ReadAllBytes(outputPath);
        AssertTrue(
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF,
            "CSV UTF-8 BOM");
        AssertEqual(
            expected,
            Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
            "CSV file content");
    }

    private static void TestRaggedCsvAndInvalidCharacters()
    {
        MarkdownTable table = new MarkdownTable(
            new List<string> { "A", "B" });
        table.Rows.Add(new List<string> { "one" });
        table.Rows.Add(new List<string>
        {
            "two",
            "line 1\r\nline 2",
            "quote \"value\""
        });
        table.Rows.Add(new List<string>
        {
            "bad\u0000control",
            "bad\uD800surrogate"
        });

        string csv = CsvExporter.ToCsv(table);
        string expected =
            "A,B,\r\n" +
            "one,,\r\n" +
            "two,\"line 1\r\nline 2\",\"quote \"\"value\"\"\"\r\n" +
            "bad\uFFFDcontrol,bad\uFFFDsurrogate,";
        AssertEqual(expected, csv, "ragged and sanitized CSV");
    }

    private static void TestCsvFormulaNeutralizationAndAtomicWrite()
    {
        MarkdownTable table = new MarkdownTable(
            new List<string> { "Value" });
        table.Rows.Add(new List<string> { "=1+1" });
        table.Rows.Add(new List<string> { " +cmd" });
        table.Rows.Add(new List<string> { "-2+3" });
        table.Rows.Add(new List<string> { "@SUM(A1:A2)" });
        table.Rows.Add(new List<string> { "-123.45" });
        table.Rows.Add(new List<string> { "\t=hidden" });
        string expected =
            "Value\r\n" +
            "'=1+1\r\n" +
            "' +cmd\r\n" +
            "'-2+3\r\n" +
            "'@SUM(A1:A2)\r\n" +
            "-123.45\r\n" +
            "'\t=hidden";
        AssertEqual(
            expected,
            CsvExporter.ToCsv(table),
            "CSV formula neutralization");

        string outputPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "export-atomic.csv");
        byte[] original = Encoding.UTF8.GetBytes("original content");
        File.WriteAllBytes(outputPath, original);
        bool failed = false;
        using (FileStream locked = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            try
            {
                CsvExporter.Export(table, outputPath);
            }
            catch (IOException)
            {
                failed = true;
            }
        }

        AssertTrue(failed, "locked CSV export fails safely");
        byte[] after = File.ReadAllBytes(outputPath);
        AssertTrue(
            Convert.ToBase64String(original) == Convert.ToBase64String(after),
            "failed CSV export preserves original file");
    }

    private static void TestDocxXmlAndNumbering()
    {
        string markdown =
            "# \u5BFC\u51FA\u6D4B\u8BD5\n\n" +
            "A & B <tag> \"quote\" 'apostrophe'\n\n" +
            "- \u5217\u8868\u9879\u4E00\n" +
            "- \u5217\u8868\u9879\u4E8C\n\n" +
            "| \u5217 | \u503C |\n| --- | --- |\n" +
            "| \u4E2D\u6587 | before\u0000middle\u000Bafter\uD800 \uD83D\uDE00 |";

        byte[] content = DocxExporter.Create(markdown);
        string outputPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "export-hardening.docx");
        File.WriteAllBytes(outputPath, content);

        using (MemoryStream memory = new MemoryStream(content))
        using (ZipArchive archive = new ZipArchive(
            memory,
            ZipArchiveMode.Read,
            false,
            Encoding.UTF8))
        {
            AssertTrue(archive.GetEntry("word/document.xml") != null, "document.xml entry");
            AssertTrue(archive.GetEntry("word/numbering.xml") != null, "numbering.xml entry");

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string xml = ReadEntry(entry);
                XmlDocument parsed = new XmlDocument();
                parsed.PreserveWhitespace = true;
                parsed.LoadXml(xml);
            }

            string documentXml = ReadEntry(archive.GetEntry("word/document.xml"));
            AssertContains(documentXml, "A &amp; B &lt;tag&gt;", "XML escaping");
            AssertTrue(documentXml.IndexOf('\u0000') < 0, "NUL removed from XML");
            AssertTrue(documentXml.IndexOf('\u000B') < 0, "vertical tab removed from XML");
            AssertTrue(documentXml.IndexOf('\uD800') < 0, "isolated surrogate removed from XML");
            AssertContains(documentXml, "before\uFFFDmiddle\uFFFDafter\uFFFD", "invalid XML replacement");
            AssertContains(documentXml, "\uD83D\uDE00", "supplementary Unicode preservation");

            string numberingXml = ReadEntry(archive.GetEntry("word/numbering.xml"));
            AssertContains(numberingXml, "w:val=\"&#x2022;\"", "stable bullet character");
            AssertTrue(
                numberingXml.IndexOf("Symbol", StringComparison.OrdinalIgnoreCase) < 0,
                "bullet does not depend on Symbol font remapping");
        }
    }

    private static void TestIndependentOrderedLists()
    {
        string markdown =
            "1. first\n" +
            "2. second\n\n" +
            "between\n\n" +
            "1. again\n" +
            "2. last";
        byte[] content = DocxExporter.Create(markdown);
        using (MemoryStream memory = new MemoryStream(content))
        using (ZipArchive archive = new ZipArchive(
            memory,
            ZipArchiveMode.Read,
            false,
            Encoding.UTF8))
        {
            string documentXml = ReadEntry(
                archive.GetEntry("word/document.xml"));
            string numberingXml = ReadEntry(
                archive.GetEntry("word/numbering.xml"));
            AssertContains(
                documentXml,
                "<w:numId w:val=\"1\"/>",
                "first ordered list numbering instance");
            AssertContains(
                documentXml,
                "<w:numId w:val=\"2\"/>",
                "second ordered list numbering instance");
            AssertContains(
                numberingXml,
                "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"1\"/>",
                "first ordered list definition");
            AssertContains(
                numberingXml,
                "<w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/>",
                "second ordered list definition");
        }
    }

    private static void TestPdfExport()
    {
        string markdown =
            "# PDF export\n\n" +
            "A paragraph with Unicode \u4E2D\u6587 and a table.\n\n" +
            "| Name | Value |\n| --- | --- |\n| one | two |";
        byte[] content = PdfExporter.Create(markdown);
        AssertTrue(content.Length > 500, "PDF has content");
        string header = Encoding.ASCII.GetString(
            content,
            0,
            Math.Min(content.Length, 8));
        AssertTrue(header.StartsWith("%PDF-", StringComparison.Ordinal),
            "PDF header");
        string tail = Encoding.ASCII.GetString(
            content,
            Math.Max(0, content.Length - 32),
            Math.Min(32, content.Length));
        AssertTrue(tail.IndexOf("%%EOF", StringComparison.Ordinal) >= 0,
            "PDF EOF marker");
        string pdfText = Encoding.ASCII.GetString(content);
        AssertTrue(
            pdfText.IndexOf("/FontFile", StringComparison.Ordinal) >= 0,
            "PDF embeds the selected font");
        using (MemoryStream memory = new MemoryStream(content))
        using (PdfDocument parsed = PdfReader.Open(
            memory,
            PdfDocumentOpenMode.ReadOnly))
        {
            AssertTrue(parsed.PageCount > 0, "PDF page structure");
            AssertEqual(
                "FilePrompt AI 导出文档",
                parsed.Info.Title,
                "PDF Unicode document title");
        }

        string outputPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "export-hardening.pdf");
        PdfExporter.Export(markdown, outputPath);
        AssertTrue(File.ReadAllBytes(outputPath).Length > 500,
            "PDF atomic export");

        byte[] original = Encoding.UTF8.GetBytes("original PDF content");
        File.WriteAllBytes(outputPath, original);
        bool failed = false;
        using (FileStream locked = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            try
            {
                PdfExporter.Export(markdown, outputPath);
            }
            catch (IOException)
            {
                failed = true;
            }
        }

        AssertTrue(failed, "locked PDF export fails safely");
        AssertTrue(
            Convert.ToBase64String(original) ==
            Convert.ToBase64String(File.ReadAllBytes(outputPath)),
            "failed PDF export preserves original file");
    }

    private static void TestXlsxExport()
    {
        string markdown =
            "| Name | Value | Note |\n| --- | --- | --- |\n" +
            "| \u4E2D\u6587 | =1+1 | before\u0000after |\n" +
            "| line | text | first<br>second |\n\n" +
            "| Second | Column |\n| --- | --- |\n| a | b |";
        MarkdownDocument document = MarkdownDocument.Parse(markdown);
        byte[] content = XlsxExporter.Create(document);
        AssertTrue(content.Length > 1000, "XLSX has content");

        using (MemoryStream memory = new MemoryStream(content))
        using (ZipArchive archive = new ZipArchive(
            memory,
            ZipArchiveMode.Read,
            false,
            Encoding.UTF8))
        {
            AssertTrue(archive.GetEntry("[Content_Types].xml") != null,
                "XLSX content types");
            AssertTrue(archive.GetEntry("xl/worksheets/sheet1.xml") != null,
                "XLSX first worksheet");
            AssertTrue(archive.GetEntry("xl/worksheets/sheet2.xml") != null,
                "XLSX second worksheet");

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".xml",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                XmlDocument parsed = new XmlDocument();
                parsed.LoadXml(ReadEntry(entry));
            }

            ZipArchiveEntry sharedStrings = archive.GetEntry(
                "xl/sharedStrings.xml");
            if (sharedStrings != null)
            {
                AssertContains(
                    ReadEntry(sharedStrings),
                    "\u4E2D\u6587",
                    "XLSX Unicode cell value");
            }
        }

        using (MemoryStream memory = new MemoryStream(content))
        using (XSSFWorkbook workbook = new XSSFWorkbook(memory, true))
        {
            AssertEqual(2, workbook.NumberOfSheets, "XLSX sheet count");
            AssertEqual("表格 1", workbook.GetSheetName(0),
                "XLSX first sheet name");
            AssertEqual("表格 2", workbook.GetSheetName(1),
                "XLSX second sheet name");

            ISheet first = workbook.GetSheetAt(0);
            IRow firstRow = first.GetRow(1);
            AssertEqual("中文", firstRow.GetCell(0).StringCellValue,
                "XLSX Unicode cell value");
            AssertEqual(CellType.String, firstRow.GetCell(1).CellType,
                "XLSX formula-looking value remains text");
            AssertEqual("=1+1", firstRow.GetCell(1).StringCellValue,
                "XLSX formula-looking text is preserved");
            AssertEqual("before\uFFFDafter", firstRow.GetCell(2).StringCellValue,
                "XLSX invalid control sanitization");
            AssertEqual("first\nsecond",
                first.GetRow(2).GetCell(2).StringCellValue,
                "XLSX multiline cell value");
            AssertTrue(first.PaneInformation != null,
                "XLSX freeze pane metadata");
            AssertEqual("a",
                workbook.GetSheetAt(1).GetRow(1).GetCell(0).StringCellValue,
                "XLSX second sheet content");
        }

        string outputPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "export-hardening.xlsx");
        XlsxExporter.Export(document, outputPath);
        AssertTrue(File.ReadAllBytes(outputPath).Length > 1000,
            "XLSX atomic export");

        byte[] original = Encoding.UTF8.GetBytes("original XLSX content");
        File.WriteAllBytes(outputPath, original);
        bool failed = false;
        using (FileStream locked = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            try
            {
                XlsxExporter.Export(document, outputPath);
            }
            catch (IOException)
            {
                failed = true;
            }
        }

        AssertTrue(failed, "locked XLSX export fails safely");
        AssertTrue(
            Convert.ToBase64String(original) ==
            Convert.ToBase64String(File.ReadAllBytes(outputPath)),
            "failed XLSX export preserves original file");
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
        string expectedFragment,
        string name)
    {
        if (actual == null ||
            actual.IndexOf(expectedFragment, StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                name + " did not contain the expected value: " + expectedFragment);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                name + " mismatch. Expected [" + expected + "] but got [" + actual + "].");
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
