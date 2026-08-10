using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Drawing.Text;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace FilePromptAIWin7
{
    internal static class PdfExporter
    {
        private static string bodyFont;
        private static string codeFont;

        public static void Export(string markdown, string path)
        {
            Export(MarkdownDocument.Parse(markdown), path);
        }

        public static void Export(MarkdownDocument document, string path)
        {
            AtomicFile.WriteAllBytes(path, Create(document));
        }

        public static byte[] Create(string markdown)
        {
            return Create(MarkdownDocument.Parse(markdown));
        }

        public static byte[] Create(MarkdownDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException("document");
            }

            Document output = BuildDocument(document);
            PdfDocumentRenderer renderer = new PdfDocumentRenderer(true);
            renderer.Document = output;
            renderer.RenderDocument();

            using (MemoryStream memory = new MemoryStream())
            {
                renderer.PdfDocument.Save(memory, false);
                return memory.ToArray();
            }
        }

        private static Document BuildDocument(MarkdownDocument source)
        {
            Document document = new Document();
            document.Info.Title = "FilePrompt AI 导出文档";
            document.Info.Author = "FilePrompt AI";

            Style normal = document.Styles[StyleNames.Normal];
            normal.Font.Name = BodyFont;
            normal.Font.Size = Unit.FromPoint(10.5);
            normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
            normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.AtLeast;
            normal.ParagraphFormat.LineSpacing = Unit.FromPoint(16);

            Section section = document.AddSection();
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
            section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
            section.PageSetup.LeftMargin = Unit.FromCentimeter(1.8);
            section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);

            foreach (MarkdownBlock block in source.Blocks)
            {
                if (block != null)
                {
                    AppendBlock(section, block);
                }
            }

            if (source.Blocks.Count == 0)
            {
                section.AddParagraph(string.Empty);
            }

            return document;
        }

        private static void AppendBlock(Section section, MarkdownBlock block)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    AppendHeading(section, block);
                    break;
                case MarkdownBlockKind.CodeBlock:
                    AppendCode(section, block.Text);
                    break;
                case MarkdownBlockKind.List:
                    AppendList(section, block);
                    break;
                case MarkdownBlockKind.Quote:
                    AppendQuote(section, block.Text);
                    break;
                case MarkdownBlockKind.Table:
                    AppendTable(section, block.Table);
                    break;
                case MarkdownBlockKind.HorizontalRule:
                    AppendHorizontalRule(section);
                    break;
                default:
                    Paragraph paragraph = section.AddParagraph();
                    AddMultilineText(paragraph, block.Text);
                    break;
            }
        }

        private static void AppendHeading(Section section, MarkdownBlock block)
        {
            int level = Math.Max(1, Math.Min(6, block.Level));
            double[] sizes = { 19, 16, 14, 12.5, 11.5, 10.5 };
            Paragraph paragraph = section.AddParagraph();
            paragraph.Format.Font.Name = BodyFont;
            paragraph.Format.Font.Size = Unit.FromPoint(sizes[level - 1]);
            paragraph.Format.Font.Bold = level <= 4;
            paragraph.Format.Font.Color = Color.FromRgb(35, 43, 53);
            paragraph.Format.SpaceBefore = Unit.FromPoint(level == 1 ? 12 : 8);
            paragraph.Format.SpaceAfter = Unit.FromPoint(5);
            paragraph.Format.KeepWithNext = true;
            AddMultilineText(paragraph, block.Text);
        }

        private static void AppendCode(Section section, string value)
        {
            Paragraph paragraph = section.AddParagraph();
            paragraph.Format.Font.Name = ContainsNonAscii(value)
                ? BodyFont
                : CodeFont;
            paragraph.Format.Font.Size = Unit.FromPoint(8.5);
            paragraph.Format.Font.Color = Color.FromRgb(36, 42, 48);
            paragraph.Format.Shading.Color = Color.FromRgb(245, 247, 249);
            paragraph.Format.LeftIndent = Unit.FromCentimeter(0.25);
            paragraph.Format.RightIndent = Unit.FromCentimeter(0.25);
            paragraph.Format.SpaceBefore = Unit.FromPoint(4);
            paragraph.Format.SpaceAfter = Unit.FromPoint(7);
            AddMultilineText(paragraph, value);
        }

        private static void AppendList(Section section, MarkdownBlock block)
        {
            foreach (string item in block.Items)
            {
                Paragraph paragraph = section.AddParagraph();
                paragraph.Format.ListInfo.ListType = block.Ordered
                    ? ListType.NumberList1
                    : ListType.BulletList1;
                paragraph.Format.LeftIndent = Unit.FromCentimeter(0.6);
                paragraph.Format.FirstLineIndent = Unit.FromCentimeter(-0.3);
                AddMultilineText(paragraph, item);
            }
        }

        private static void AppendQuote(Section section, string value)
        {
            Paragraph paragraph = section.AddParagraph();
            paragraph.Format.Font.Italic = true;
            paragraph.Format.Font.Color = Color.FromRgb(79, 91, 102);
            paragraph.Format.LeftIndent = Unit.FromCentimeter(0.6);
            paragraph.Format.RightIndent = Unit.FromCentimeter(0.3);
            paragraph.Format.Borders.Left.Width = Unit.FromPoint(2);
            paragraph.Format.Borders.Left.Color = Color.FromRgb(46, 125, 110);
            paragraph.Format.SpaceBefore = Unit.FromPoint(4);
            paragraph.Format.SpaceAfter = Unit.FromPoint(7);
            AddMultilineText(paragraph, value);
        }

        private static void AppendTable(Section section, MarkdownTable source)
        {
            if (source == null || source.Headers == null ||
                source.Headers.Count == 0)
            {
                return;
            }

            int columnCount = source.Headers.Count;
            double width = 16.8 / columnCount;
            Table table = section.AddTable();
            table.Format.Font.Name = BodyFont;
            table.Format.Font.Size = Unit.FromPoint(8.5);
            table.Borders.Width = Unit.FromPoint(0.4);
            table.Borders.Color = Color.FromRgb(177, 188, 198);
            table.Rows.LeftIndent = Unit.Zero;
            for (int index = 0; index < columnCount; index++)
            {
                table.AddColumn(Unit.FromCentimeter(width));
            }

            Row header = table.AddRow();
            header.HeadingFormat = true;
            header.Shading.Color = Color.FromRgb(230, 239, 237);
            header.Format.Font.Bold = true;
            header.VerticalAlignment = VerticalAlignment.Center;
            for (int column = 0; column < columnCount; column++)
            {
                AddMultilineText(
                    header.Cells[column].AddParagraph(),
                    source.Headers[column]);
            }

            foreach (IList<string> values in source.Rows)
            {
                Row row = table.AddRow();
                row.VerticalAlignment = VerticalAlignment.Top;
                for (int column = 0; column < columnCount; column++)
                {
                    string value = values != null && column < values.Count
                        ? values[column]
                        : string.Empty;
                    AddMultilineText(
                        row.Cells[column].AddParagraph(),
                        value);
                }
            }

            Paragraph spacer = section.AddParagraph();
            spacer.Format.SpaceAfter = Unit.FromPoint(3);
        }

        private static void AppendHorizontalRule(Section section)
        {
            Paragraph paragraph = section.AddParagraph();
            paragraph.Format.Borders.Bottom.Width = Unit.FromPoint(0.7);
            paragraph.Format.Borders.Bottom.Color =
                Color.FromRgb(170, 183, 195);
            paragraph.Format.SpaceBefore = Unit.FromPoint(4);
            paragraph.Format.SpaceAfter = Unit.FromPoint(8);
        }

        private static void AddMultilineText(Paragraph paragraph, string value)
        {
            string normalized = SanitizeText(value)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            string[] lines = normalized.Split(
                new[] { '\n' },
                StringSplitOptions.None);
            for (int index = 0; index < lines.Length; index++)
            {
                paragraph.AddText(lines[index]);
                if (index + 1 < lines.Length)
                {
                    paragraph.AddLineBreak();
                }
            }
        }

        private static string SanitizeText(string value)
        {
            StringBuilder result = new StringBuilder();
            string source = value ?? string.Empty;
            for (int index = 0; index < source.Length; index++)
            {
                char current = source[index];
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 < source.Length &&
                        char.IsLowSurrogate(source[index + 1]))
                    {
                        result.Append(current);
                        result.Append(source[++index]);
                    }
                    else
                    {
                        result.Append('\uFFFD');
                    }

                    continue;
                }

                if (char.IsLowSurrogate(current))
                {
                    result.Append('\uFFFD');
                }
                else if (current == '\t' || current == '\r' ||
                    current == '\n' || !char.IsControl(current))
                {
                    result.Append(current);
                }
                else
                {
                    result.Append('\uFFFD');
                }
            }

            return result.ToString();
        }

        private static string BodyFont
        {
            get
            {
                if (string.IsNullOrEmpty(bodyFont))
                {
                    bodyFont = FindInstalledFont(new[]
                    {
                        "Microsoft YaHei",
                        "SimSun",
                        "Arial Unicode MS",
                        "Noto Sans CJK SC",
                        "DengXian",
                        "Microsoft JhengHei",
                        "MingLiU",
                        "MS Gothic",
                        "Meiryo",
                        "Malgun Gothic"
                    });
                }

                return bodyFont;
            }
        }

        private static string CodeFont
        {
            get
            {
                if (string.IsNullOrEmpty(codeFont))
                {
                    codeFont = FindInstalledFont(new[]
                    {
                        "Consolas",
                        "Courier New",
                        BodyFont
                    });
                }

                return codeFont;
            }
        }

        private static string FindInstalledFont(string[] candidates)
        {
            using (InstalledFontCollection collection =
                new InstalledFontCollection())
            {
                HashSet<string> installed = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (System.Drawing.FontFamily family in collection.Families)
                {
                    installed.Add(family.Name);
                }

                foreach (string candidate in candidates)
                {
                    if (installed.Contains(candidate))
                    {
                        return candidate;
                    }
                }
            }

            throw new InvalidOperationException(
                "未找到可嵌入的中文字体。请在 Windows 中安装 Microsoft YaHei、SimSun 或 Arial Unicode MS 后重试。\r\n" +
                "No embeddable CJK font was found. Install Microsoft YaHei, SimSun, or Arial Unicode MS.");
        }

        private static bool ContainsNonAscii(string value)
        {
            foreach (char current in value ?? string.Empty)
            {
                if (current > 127)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
