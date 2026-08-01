using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FilePromptWin7
{
    internal static class DocxExporter
    {
        private const string WordNamespace =
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const string RelationshipNamespace =
            "http://schemas.openxmlformats.org/package/2006/relationships";

        public static void Export(string markdown, string path)
        {
            Export(MarkdownDocument.Parse(markdown), path);
        }

        public static void Export(MarkdownDocument document, string path)
        {
            if (document == null)
            {
                throw new ArgumentNullException("document");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "An output path is required.",
                    "path");
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) &&
                !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] content = Create(document);
            using (FileStream stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Write(content, 0, content.Length);
            }
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

            MemoryStream memory = new MemoryStream();
            using (ZipArchive archive = new ZipArchive(
                memory,
                ZipArchiveMode.Create,
                true,
                Encoding.UTF8))
            {
                AddTextEntry(
                    archive,
                    "[Content_Types].xml",
                    BuildContentTypes());
                AddTextEntry(
                    archive,
                    "_rels/.rels",
                    BuildPackageRelationships());
                AddTextEntry(
                    archive,
                    "word/document.xml",
                    BuildDocument(document));
                AddTextEntry(
                    archive,
                    "word/styles.xml",
                    BuildStyles());
                AddTextEntry(
                    archive,
                    "word/numbering.xml",
                    BuildNumbering());
                AddTextEntry(
                    archive,
                    "word/_rels/document.xml.rels",
                    BuildDocumentRelationships());
                AddTextEntry(
                    archive,
                    "docProps/core.xml",
                    BuildCoreProperties());
            }

            return memory.ToArray();
        }

        private static void AddTextEntry(
            ZipArchive archive,
            string name,
            string value)
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                name,
                CompressionLevel.Fastest);
            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(
                stream,
                new UTF8Encoding(false)))
            {
                writer.Write(value ?? string.Empty);
            }
        }

        private static string BuildDocument(MarkdownDocument document)
        {
            StringBuilder xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<w:document xmlns:w=\"");
            xml.Append(WordNamespace);
            xml.Append("\"><w:body>");

            bool hasBody = false;
            if (document.Blocks != null)
            {
                foreach (MarkdownBlock block in document.Blocks)
                {
                    if (block == null)
                    {
                        continue;
                    }

                    hasBody = true;
                    AppendBlock(xml, block);
                }
            }

            if (!hasBody)
            {
                AppendParagraph(xml, string.Empty, "Normal");
            }

            xml.Append(
                "<w:sectPr>" +
                "<w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
                "<w:pgMar w:top=\"1440\" w:right=\"1440\" " +
                "w:bottom=\"1440\" w:left=\"1440\" " +
                "w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/>" +
                "</w:sectPr>");
            xml.Append("</w:body></w:document>");
            return xml.ToString();
        }

        private static void AppendBlock(
            StringBuilder xml,
            MarkdownBlock block)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    AppendParagraph(
                        xml,
                        block.Text,
                        "Heading" + ClampHeadingLevel(block.Level));
                    break;
                case MarkdownBlockKind.CodeBlock:
                    AppendParagraph(
                        xml,
                        block.Text,
                        "Code");
                    break;
                case MarkdownBlockKind.List:
                    AppendList(xml, block);
                    break;
                case MarkdownBlockKind.Quote:
                    AppendParagraph(xml, block.Text, "Quote");
                    break;
                case MarkdownBlockKind.Table:
                    AppendTable(xml, block.Table);
                    break;
                case MarkdownBlockKind.HorizontalRule:
                    AppendHorizontalRule(xml);
                    break;
                default:
                    AppendParagraph(xml, block.Text, "Normal");
                    break;
            }
        }

        private static int ClampHeadingLevel(int level)
        {
            if (level < 1)
            {
                return 1;
            }

            return level > 6 ? 6 : level;
        }

        private static void AppendParagraph(
            StringBuilder xml,
            string value,
            string style)
        {
            xml.Append("<w:p>");
            if (!string.IsNullOrEmpty(style))
            {
                xml.Append("<w:pPr><w:pStyle w:val=\"");
                xml.Append(EscapeXml(style));
                xml.Append("\"/></w:pPr>");
            }

            AppendTextRuns(xml, value ?? string.Empty);
            xml.Append("</w:p>");
        }

        private static void AppendTextRuns(
            StringBuilder xml,
            string value)
        {
            string text = value ?? string.Empty;
            string[] lines = text.Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split(new[] { '\n' }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                xml.Append("<w:r><w:t xml:space=\"preserve\">");
                xml.Append(EscapeXml(lines[i]));
                xml.Append("</w:t></w:r>");
                if (i + 1 < lines.Length)
                {
                    xml.Append("<w:r><w:br/></w:r>");
                }
            }
        }

        private static void AppendList(
            StringBuilder xml,
            MarkdownBlock block)
        {
            if (block.Items == null || block.Items.Count == 0)
            {
                AppendParagraph(xml, string.Empty, "Normal");
                return;
            }

            foreach (string item in block.Items)
            {
                xml.Append("<w:p><w:pPr><w:numPr>");
                xml.Append("<w:ilvl w:val=\"0\"/>");
                xml.Append("<w:numId w:val=\"");
                xml.Append(block.Ordered ? "2" : "1");
                xml.Append("\"/></w:numPr></w:pPr>");
                AppendTextRuns(xml, item ?? string.Empty);
                xml.Append("</w:p>");
            }
        }

        private static void AppendHorizontalRule(StringBuilder xml)
        {
            xml.Append(
                "<w:p><w:pPr><w:pBdr><w:bottom " +
                "w:val=\"single\" w:sz=\"8\" w:space=\"1\" " +
                "w:color=\"B7C9D6\"/></w:pBdr></w:pPr>");
            xml.Append("<w:r><w:t xml:space=\"preserve\"> </w:t></w:r>");
            xml.Append("</w:p>");
        }

        private static void AppendTable(
            StringBuilder xml,
            MarkdownTable table)
        {
            if (table == null)
            {
                return;
            }

            IList<IList<string>> rows = table.GetAllRows();
            int columnCount = table.Headers == null
                ? 0
                : table.Headers.Count;
            foreach (IList<string> row in table.Rows)
            {
                if (row != null && row.Count > columnCount)
                {
                    columnCount = row.Count;
                }
            }

            if (columnCount == 0)
            {
                return;
            }

            xml.Append("<w:tbl><w:tblPr>");
            xml.Append("<w:tblW w:w=\"0\" w:type=\"auto\"/>");
            xml.Append("<w:tblLayout w:type=\"autofit\"/>");
            xml.Append(
                "<w:tblBorders>" +
                "<w:top w:val=\"single\" w:sz=\"4\" w:color=\"AAB7C4\"/>" +
                "<w:left w:val=\"single\" w:sz=\"4\" w:color=\"AAB7C4\"/>" +
                "<w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"AAB7C4\"/>" +
                "<w:right w:val=\"single\" w:sz=\"4\" w:color=\"AAB7C4\"/>" +
                "<w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"D8E0E8\"/>" +
                "<w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"D8E0E8\"/>" +
                "</w:tblBorders>");
            xml.Append("</w:tblPr>");

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                IList<string> row = rows[rowIndex];
                xml.Append("<w:tr>");
                for (int columnIndex = 0;
                    columnIndex < columnCount;
                    columnIndex++)
                {
                    string value = row != null &&
                        columnIndex < row.Count
                        ? row[columnIndex]
                        : string.Empty;
                    bool header = rowIndex == 0 && table.Headers != null &&
                        table.Headers.Count > 0;
                    xml.Append("<w:tc><w:tcPr>");
                    if (header)
                    {
                        xml.Append("<w:shd w:val=\"clear\" w:color=\"auto\" ");
                        xml.Append("w:fill=\"D9E7F2\"/>");
                    }

                    xml.Append("</w:tcPr><w:p>");
                    if (header)
                    {
                        xml.Append("<w:pPr><w:pStyle w:val=\"TableHeader\"/>");
                        xml.Append("</w:pPr>");
                    }

                    AppendTextRuns(xml, value ?? string.Empty);
                    xml.Append("</w:p></w:tc>");
                }

                xml.Append("</w:tr>");
            }

            xml.Append("</w:tbl>");
        }

        private static string BuildContentTypes()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"" +
                "http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"" +
                "application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"" +
                "application/vnd.openxmlformats-officedocument.wordprocessingml." +
                "document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"" +
                "application/vnd.openxmlformats-officedocument.wordprocessingml." +
                "styles+xml\"/>" +
                "<Override PartName=\"/word/numbering.xml\" ContentType=\"" +
                "application/vnd.openxmlformats-officedocument.wordprocessingml." +
                "numbering+xml\"/>" +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"" +
                "application/vnd.openxmlformats-package.core-properties+xml\"/>" +
                "</Types>";
        }

        private static string BuildPackageRelationships()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"" + RelationshipNamespace + "\">" +
                "<Relationship Id=\"rId1\" Type=\"" +
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" +
                "officeDocument\" Target=\"word/document.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"" +
                "http://schemas.openxmlformats.org/package/2006/relationships/" +
                "metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                "</Relationships>";
        }

        private static string BuildDocumentRelationships()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"" + RelationshipNamespace + "\">" +
                "<Relationship Id=\"rId1\" Type=\"" +
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" +
                "numbering\" Target=\"numbering.xml\"/>" +
                "</Relationships>";
        }

        private static string BuildCoreProperties()
        {
            string now = DateTime.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture);
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<cp:coreProperties xmlns:cp=\"" +
                "http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                "xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
                "xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" " +
                "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                "<dc:title>FilePrompt conversation</dc:title>" +
                "<dc:creator>FilePrompt</dc:creator>" +
                "<dcterms:created xsi:type=\"dcterms:W3CDTF\">" +
                now + "</dcterms:created>" +
                "<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" +
                now + "</dcterms:modified>" +
                "</cp:coreProperties>";
        }

        private static string BuildStyles()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:styles xmlns:w=\"" + WordNamespace + "\">" +
                "<w:docDefaults><w:rPrDefault><w:rPr>" +
                "<w:rFonts w:ascii=\"Microsoft YaHei\" w:hAnsi=\"Microsoft YaHei\" " +
                "w:eastAsia=\"Microsoft YaHei\"/>" +
                "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/>" +
                "</w:rPr></w:rPrDefault></w:docDefaults>" +
                Style("Normal", "Normal", "22", false, "000000") +
                Style("Heading1", "heading 1", "34", true, "1F4E79") +
                Style("Heading2", "heading 2", "29", true, "2E75B6") +
                Style("Heading3", "heading 3", "25", true, "5B9BD5") +
                Style("Heading4", "heading 4", "23", true, "5B9BD5") +
                Style("Heading5", "heading 5", "22", true, "5B9BD5") +
                Style("Heading6", "heading 6", "22", true, "5B9BD5") +
                "<w:style w:type=\"paragraph\" w:styleId=\"Quote\">" +
                "<w:name w:val=\"Quote\"/><w:pPr><w:ind w:left=\"360\"/>" +
                "<w:pBdr><w:left w:val=\"single\" w:sz=\"18\" " +
                "w:space=\"8\" w:color=\"5B9BD5\"/></w:pBdr></w:pPr>" +
                "<w:rPr><w:color w:val=\"4F5B66\"/><w:i/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Code\">" +
                "<w:name w:val=\"Code\"/><w:pPr><w:shd w:fill=\"F1F4F7\"/>" +
                "<w:spacing w:before=\"80\" w:after=\"80\"/></w:pPr>" +
                "<w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" " +
                "w:eastAsia=\"Microsoft YaHei\"/><w:sz w:val=\"20\"/>" +
                "</w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"TableHeader\">" +
                "<w:name w:val=\"Table Header\"/><w:rPr><w:b/>" +
                "<w:color w:val=\"1F2933\"/></w:rPr></w:style>" +
                "</w:styles>";
        }

        private static string Style(
            string id,
            string name,
            string size,
            bool bold,
            string color)
        {
            StringBuilder value = new StringBuilder();
            value.Append("<w:style w:type=\"paragraph\" w:styleId=\"");
            value.Append(id);
            value.Append("\"><w:name w:val=\"");
            value.Append(name);
            value.Append("\"/><w:basedOn w:val=\"Normal\"/>");
            value.Append("<w:rPr>");
            if (bold)
            {
                value.Append("<w:b/>");
            }

            value.Append("<w:color w:val=\"");
            value.Append(color);
            value.Append("\"/><w:sz w:val=\"");
            value.Append(size);
            value.Append("\"/></w:rPr></w:style>");
            return value.ToString();
        }

        private static string BuildNumbering()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:numbering xmlns:w=\"" + WordNamespace + "\">" +
                "<w:abstractNum w:abstractNumId=\"0\">" +
                "<w:multiLevelType w:val=\"singleLevel\"/>" +
                "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/>" +
                "<w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"&#x2022;\"/>" +
                "<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" " +
                "w:hanging=\"360\"/></w:pPr></w:lvl>" +
                "</w:abstractNum>" +
                "<w:abstractNum w:abstractNumId=\"1\">" +
                "<w:multiLevelType w:val=\"singleLevel\"/>" +
                "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/>" +
                "<w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/>" +
                "<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" " +
                "w:hanging=\"360\"/></w:pPr></w:lvl></w:abstractNum>" +
                "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>" +
                "<w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num>" +
                "</w:numbering>";
        }

        private static string EscapeXml(string value)
        {
            string text = value ?? string.Empty;
            StringBuilder result = new StringBuilder(text.Length + 16);
            for (int index = 0; index < text.Length; index++)
            {
                char current = text[index];
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 < text.Length &&
                        char.IsLowSurrogate(text[index + 1]))
                    {
                        result.Append(current);
                        result.Append(text[index + 1]);
                        index++;
                    }
                    else
                    {
                        result.Append('\uFFFD');
                    }

                    continue;
                }

                if (char.IsLowSurrogate(current) ||
                    current == '\uFFFE' ||
                    current == '\uFFFF' ||
                    (current < '\u0020' &&
                        current != '\t' &&
                        current != '\r' &&
                        current != '\n'))
                {
                    result.Append('\uFFFD');
                    continue;
                }

                switch (current)
                {
                    case '&':
                        result.Append("&amp;");
                        break;
                    case '<':
                        result.Append("&lt;");
                        break;
                    case '>':
                        result.Append("&gt;");
                        break;
                    case '"':
                        result.Append("&quot;");
                        break;
                    case '\'':
                        result.Append("&apos;");
                        break;
                    default:
                        result.Append(current);
                        break;
                }
            }

            return result.ToString();
        }
    }
}
