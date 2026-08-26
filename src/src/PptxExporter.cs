using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Text;

namespace FilePromptAIWin7
{
    // Small, dependency-free Open XML writer for the offline build. It keeps
    // the generated deck intentionally simple so it opens on Office 2007+.
    internal static class PptxExporter
    {
        private const long SlideWidth = 12192000L;
        private const long SlideHeight = 6858000L;
        private const int MaxBodyLinesPerSlide = 10;
        private const int MaxBodyLineWidth = 88;
        private const int MaxTitleLineWidth = 48;
        private const string PresentationNamespace =
            "http://schemas.openxmlformats.org/presentationml/2006/main";
        private const string DrawingNamespace =
            "http://schemas.openxmlformats.org/drawingml/2006/main";
        private const string RelationshipsNamespace =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private sealed class SlideModel
        {
            public string Title { get; set; }
            public IList<string> Lines { get; private set; }

            public SlideModel(string title)
            {
                Title = title ?? string.Empty;
                Lines = new List<string>();
            }
        }

        public static void Export(string markdown, string path)
        {
            Export(MarkdownDocument.Parse(markdown), path);
        }

        public static void Export(MarkdownDocument document, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An output path is required.", "path");
            }

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

            IList<SlideModel> slides = BuildSlides(document);
            MemoryStream memory = new MemoryStream();
            using (ZipArchive archive = new ZipArchive(
                memory, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                AddTextEntry(archive, "[Content_Types].xml", BuildContentTypes(slides.Count));
                AddTextEntry(archive, "_rels/.rels", BuildRootRelationships());
                AddTextEntry(archive, "ppt/presentation.xml", BuildPresentation(slides.Count));
                AddTextEntry(archive, "ppt/_rels/presentation.xml.rels", BuildPresentationRelationships(slides.Count));
                AddTextEntry(archive, "ppt/slideMasters/slideMaster1.xml", BuildSlideMaster());
                AddTextEntry(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels", BuildSlideMasterRelationships());
                AddTextEntry(archive, "ppt/slideLayouts/slideLayout1.xml", BuildSlideLayout());
                AddTextEntry(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", BuildSlideLayoutRelationships());
                AddTextEntry(archive, "ppt/theme/theme1.xml", BuildTheme());
                for (int index = 0; index < slides.Count; index++)
                {
                    AddTextEntry(
                        archive,
                        "ppt/slides/slide" + (index + 1).ToString() + ".xml",
                        BuildSlide(slides[index], index + 1));
                    AddTextEntry(
                        archive,
                        "ppt/slides/_rels/slide" + (index + 1).ToString() + ".xml.rels",
                        BuildSlideRelationships());
                }

                AddTextEntry(archive, "docProps/core.xml", BuildCoreProperties());
                AddTextEntry(archive, "docProps/app.xml", BuildAppProperties(slides.Count));
            }

            return memory.ToArray();
        }

        private static IList<SlideModel> BuildSlides(MarkdownDocument document)
        {
            List<SlideModel> result = new List<SlideModel>();
            SlideModel current = new SlideModel("FilePrompt AI 输出");
            string sectionTitle = current.Title;
            int sectionPage = 1;
            bool titleConsumed = false;

            if (document.Blocks != null)
            {
                foreach (MarkdownBlock block in document.Blocks)
                {
                    if (block == null)
                    {
                        continue;
                    }

                    if (block.Kind == MarkdownBlockKind.Heading)
                    {
                        if (current.Lines.Count > 0 || titleConsumed)
                        {
                            result.Add(current);
                        }

                        current = new SlideModel(
                            string.IsNullOrWhiteSpace(block.Text)
                                ? "内容"
                                : block.Text.Trim());
                        sectionTitle = current.Title;
                        sectionPage = 1;
                        titleConsumed = true;
                        continue;
                    }

                    IList<string> blockLines = GetBlockLines(block);
                    foreach (string blockLine in blockLines)
                    {
                        IList<string> wrappedLines = WrapLine(
                            blockLine,
                            MaxBodyLineWidth);
                        foreach (string wrappedLine in wrappedLines)
                        {
                            if (current.Lines.Count >= MaxBodyLinesPerSlide)
                            {
                                result.Add(current);
                                sectionPage++;
                                current = new SlideModel(
                                    BuildContinuationTitle(
                                        sectionTitle,
                                        sectionPage));
                                titleConsumed = true;
                            }

                            current.Lines.Add(wrappedLine);
                        }
                    }
                }
            }

            if (current.Lines.Count > 0 || result.Count == 0)
            {
                result.Add(current);
            }

            // A compact deck is easier to read and safer for older PowerPoint.
            for (int index = result.Count - 1; index >= 0; index--)
            {
                if (result[index].Lines.Count == 0 && result.Count > 1)
                {
                    result.RemoveAt(index);
                }
            }

            return result;
        }

        private static IList<string> GetBlockLines(MarkdownBlock block)
        {
            List<string> lines = new List<string>();
            if (block.Kind == MarkdownBlockKind.List)
            {
                if (block.Items != null)
                {
                    for (int index = 0; index < block.Items.Count; index++)
                    {
                        string prefix = block.Ordered
                            ? (index + 1).ToString() + ". "
                            : "- ";
                        lines.Add(prefix + NormalizeLine(block.Items[index]));
                    }
                }
                return lines;
            }

            if (block.Kind == MarkdownBlockKind.Table && block.Table != null)
            {
                IList<IList<string>> rows = block.Table.GetAllRows();
                foreach (IList<string> row in rows)
                {
                    StringBuilder line = new StringBuilder();
                    if (row != null)
                    {
                        for (int index = 0; index < row.Count; index++)
                        {
                            if (index > 0)
                            {
                                line.Append(" | ");
                            }
                            line.Append(NormalizeLine(row[index]));
                        }
                    }
                    lines.Add(line.ToString());
                }
                return lines;
            }

            string text = NormalizeLine(block.Text);
            if (block.Kind == MarkdownBlockKind.CodeBlock)
            {
                lines.Add("代码: " + text);
            }
            else if (block.Kind == MarkdownBlockKind.Quote)
            {
                lines.Add("> " + text);
            }
            else if (block.Kind != MarkdownBlockKind.HorizontalRule && text.Length > 0)
            {
                string[] parts = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    lines.Add(NormalizeLine(part));
                }
            }

            return lines;
        }

        private static string NormalizeLine(string value)
        {
            string text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            return text.Trim();
        }

        private static IList<string> WrapLine(
            string value,
            int maximumWidth)
        {
            List<string> result = new List<string>();
            string text = value ?? string.Empty;
            if (text.Length == 0)
            {
                result.Add(string.Empty);
                return result;
            }

            IList<string> elements = GetTextElements(text);
            int start = 0;
            while (start < elements.Count)
            {
                int width = 0;
                int end = start;
                while (end < elements.Count)
                {
                    int elementWidth = GetTextElementWidth(elements[end]);
                    if (end > start &&
                        width + elementWidth > maximumWidth)
                    {
                        break;
                    }

                    width += elementWidth;
                    end++;
                }

                if (end < elements.Count)
                {
                    int minimumNaturalBreak = start +
                        ((end - start) * 2 / 3);
                    for (int index = end - 1;
                        index >= minimumNaturalBreak;
                        index--)
                    {
                        if (IsNaturalLineBreak(elements[index]))
                        {
                            end = index + 1;
                            break;
                        }
                    }
                }

                result.Add(JoinTextElements(elements, start, end));
                start = end;
            }

            return result;
        }

        private static IList<string> GetTextElements(string text)
        {
            List<string> result = new List<string>();
            TextElementEnumerator enumerator =
                StringInfo.GetTextElementEnumerator(text ?? string.Empty);
            while (enumerator.MoveNext())
            {
                string element = enumerator.GetTextElement();
                if (result.Count > 0 &&
                    (StartsWithJoiner(element) ||
                        EndsWithJoiner(result[result.Count - 1]) ||
                        IsEmojiModifier(element) ||
                        (IsRegionalIndicator(element) &&
                            IsRegionalIndicator(
                                result[result.Count - 1]))))
                {
                    result[result.Count - 1] += element;
                }
                else
                {
                    result.Add(element);
                }
            }

            return result;
        }

        private static bool IsEmojiModifier(string value)
        {
            int scalar;
            return TryGetSingleUnicodeScalar(value, out scalar) &&
                scalar >= 0x1F3FB && scalar <= 0x1F3FF;
        }

        private static bool IsRegionalIndicator(string value)
        {
            int scalar;
            return TryGetSingleUnicodeScalar(value, out scalar) &&
                scalar >= 0x1F1E6 && scalar <= 0x1F1FF;
        }

        private static bool TryGetSingleUnicodeScalar(
            string value,
            out int scalar)
        {
            scalar = 0;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (value.Length == 1 && !char.IsSurrogate(value[0]))
            {
                scalar = value[0];
                return true;
            }

            if (value.Length == 2 &&
                char.IsHighSurrogate(value[0]) &&
                char.IsLowSurrogate(value[1]))
            {
                scalar = char.ConvertToUtf32(value[0], value[1]);
                return true;
            }

            return false;
        }

        private static bool StartsWithJoiner(string value)
        {
            return !string.IsNullOrEmpty(value) && value[0] == '\u200D';
        }

        private static bool EndsWithJoiner(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                value[value.Length - 1] == '\u200D';
        }

        private static int GetTextElementWidth(string element)
        {
            if (string.IsNullOrEmpty(element))
            {
                return 0;
            }

            if (element[0] == '\t')
            {
                return 4;
            }

            return element[0] <= 0x7F ? 1 : 2;
        }

        private static string JoinTextElements(
            IList<string> elements,
            int start,
            int end)
        {
            StringBuilder text = new StringBuilder();
            for (int index = start; index < end; index++)
            {
                text.Append(elements[index]);
            }

            return text.ToString();
        }

        private static bool IsNaturalLineBreak(string element)
        {
            if (string.IsNullOrEmpty(element))
            {
                return false;
            }

            char value = element[element.Length - 1];
            return char.IsWhiteSpace(value) ||
                value == ',' || value == '.' || value == ';' ||
                value == ':' || value == '!' || value == '?' ||
                value == ')' || value == ']' || value == '}' ||
                value == '，' || value == '。' || value == '；' ||
                value == '：' || value == '！' || value == '？' ||
                value == '、' || value == '）' || value == '】';
        }

        private static string BuildContinuationTitle(
            string title,
            int pageNumber)
        {
            return (string.IsNullOrWhiteSpace(title) ? "内容" : title) +
                "（续 " + pageNumber.ToString() + "）";
        }

        private static string WrapTitle(string title)
        {
            return string.Join(
                "\n",
                new List<string>(WrapLine(
                    title,
                    MaxTitleLineWidth)).ToArray());
        }

        private static void AddTextEntry(ZipArchive archive, string name, string value)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(value ?? string.Empty);
            }
        }

        private static string Xml(string value)
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
                        result.Append(text[++index]);
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

        private static string BuildContentTypes(int count)
        {
            StringBuilder xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            xml.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            xml.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            xml.Append("<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>");
            xml.Append("<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>");
            xml.Append("<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>");
            xml.Append("<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
            xml.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
            xml.Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
            for (int index = 1; index <= count; index++)
            {
                xml.Append("<Override PartName=\"/ppt/slides/slide");
                xml.Append(index.ToString());
                xml.Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
            }
            xml.Append("</Types>");
            return xml.ToString();
        }

        private static string BuildRootRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
                "</Relationships>";
        }

        private static string BuildPresentation(int count)
        {
            StringBuilder xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<p:presentation xmlns:a=\"").Append(DrawingNamespace).Append("\" xmlns:r=\"").Append(RelationshipsNamespace).Append("\" xmlns:p=\"").Append(PresentationNamespace).Append("\">");
            xml.Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>");
            xml.Append("<p:sldIdLst>");
            for (int index = 1; index <= count; index++)
            {
                xml.Append("<p:sldId id=\"").Append((255 + index).ToString()).Append("\" r:id=\"rId").Append((index + 2).ToString()).Append("\"/>");
            }
            xml.Append("</p:sldIdLst><p:sldSz cx=\"").Append(SlideWidth.ToString()).Append("\" cy=\"").Append(SlideHeight.ToString()).Append("\" type=\"screen16x9\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
            return xml.ToString();
        }

        private static string BuildPresentationRelationships(int count)
        {
            StringBuilder xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            xml.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>");
            xml.Append("<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/>");
            for (int index = 1; index <= count; index++)
            {
                xml.Append("<Relationship Id=\"rId").Append((index + 2).ToString()).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide").Append(index.ToString()).Append(".xml\"/>");
            }
            xml.Append("</Relationships>");
            return xml.ToString();
        }

        private static string BuildSlideMaster()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:sldMaster xmlns:a=\"" + DrawingNamespace + "\" xmlns:r=\"" + RelationshipsNamespace + "\" xmlns:p=\"" + PresentationNamespace + "\"><p:cSld name=\"FilePrompt AI\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>";
        }

        private static string BuildSlideMasterRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/></Relationships>";
        }

        private static string BuildSlideLayout()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:sldLayout xmlns:a=\"" + DrawingNamespace + "\" xmlns:r=\"" + RelationshipsNamespace + "\" xmlns:p=\"" + PresentationNamespace + "\" type=\"blank\"><p:cSld name=\"Blank\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";
        }

        private static string BuildSlideLayoutRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>";
        }

        private static string BuildSlideRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/></Relationships>";
        }

        private static string BuildSlide(SlideModel slide, int number)
        {
            StringBuilder xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<p:sld xmlns:a=\"").Append(DrawingNamespace).Append("\" xmlns:r=\"").Append(RelationshipsNamespace).Append("\" xmlns:p=\"").Append(PresentationNamespace).Append("\"><p:cSld><p:spTree>");
            xml.Append("<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>");
            AppendTextShape(xml, 2, "标题", 500000, 240000, 11200000, 1150000, WrapTitle(slide.Title), 30, "1F2937", true, true);
            AppendTextShape(xml, 3, "正文", 700000, 1550000, 10800000, 4450000, string.Join("\n", slide.Lines), 18, "263238", false, false);
            AppendTextShape(xml, 4, "页码", 10500000, 6250000, 1200000, 350000, number.ToString(), 11, "64748B", false, false);
            xml.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            return xml.ToString();
        }

        private static void AppendTextShape(StringBuilder xml, int id, string name, long x, long y, long cx, long cy, string text, int size, string color, bool bold, bool autoFit)
        {
            xml.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id.ToString()).Append("\" name=\"").Append(Xml(name)).Append("\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"").Append(x.ToString()).Append("\" y=\"").Append(y.ToString()).Append("\"/><a:ext cx=\"").Append(cx.ToString()).Append("\" cy=\"").Append(cy.ToString()).Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln><a:noFill/></a:ln></p:spPr><p:txBody><a:bodyPr wrap=\"square\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" anchor=\"t\">");
            if (autoFit)
            {
                xml.Append("<a:normAutofit/>");
            }
            xml.Append("</a:bodyPr><a:lstStyle/>");
            string[] paragraphs = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split(new[] { '\n' }, StringSplitOptions.None);
            foreach (string paragraph in paragraphs)
            {
                xml.Append("<a:p><a:pPr marL=\"0\" indent=\"0\"/><a:r><a:rPr lang=\"zh-CN\" sz=\"").Append((size * 100).ToString()).Append("\"");
                if (bold)
                {
                    xml.Append(" b=\"1\"");
                }
                xml.Append("><a:solidFill><a:srgbClr val=\"").Append(color).Append("\"/></a:solidFill><a:latin typeface=\"Microsoft YaHei\"/><a:ea typeface=\"Microsoft YaHei\"/></a:rPr><a:t xml:space=\"preserve\">").Append(Xml(paragraph)).Append("</a:t></a:r><a:endParaRPr lang=\"zh-CN\"/></a:p>");
            }
            xml.Append("</p:txBody></p:sp>");
        }

        private static string BuildTheme()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<a:theme xmlns:a=\"" + DrawingNamespace + "\" name=\"FilePrompt AI\"><a:themeElements><a:clrScheme name=\"FilePrompt AI\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"1F2937\"/></a:dk2><a:lt2><a:srgbClr val=\"F8FAFC\"/></a:lt2><a:accent1><a:srgbClr val=\"0F766E\"/></a:accent1><a:accent2><a:srgbClr val=\"2563EB\"/></a:accent2><a:accent3><a:srgbClr val=\"D97706\"/></a:accent3><a:accent4><a:srgbClr val=\"DC2626\"/></a:accent4><a:accent5><a:srgbClr val=\"7C3AED\"/></a:accent5><a:accent6><a:srgbClr val=\"0891B2\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
                "<a:fontScheme name=\"FilePrompt AI\"><a:majorFont><a:latin typeface=\"Microsoft YaHei\"/><a:ea typeface=\"Microsoft YaHei\"/><a:cs typeface=\"Microsoft YaHei\"/></a:majorFont><a:minorFont><a:latin typeface=\"Microsoft YaHei\"/><a:ea typeface=\"Microsoft YaHei\"/><a:cs typeface=\"Microsoft YaHei\"/></a:minorFont></a:fontScheme>" +
                "<a:fmtScheme name=\"FilePrompt AI\">" +
                "<a:fillStyleLst>" +
                BuildSolidThemeFill() +
                BuildSolidThemeFill() +
                BuildSolidThemeFill() +
                "</a:fillStyleLst>" +
                "<a:lnStyleLst>" +
                BuildThemeLine("6350") +
                BuildThemeLine("12700") +
                BuildThemeLine("19050") +
                "</a:lnStyleLst>" +
                "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
                "<a:bgFillStyleLst>" +
                BuildSolidThemeFill() +
                BuildSolidThemeFill() +
                BuildSolidThemeFill() +
                "</a:bgFillStyleLst>" +
                "</a:fmtScheme></a:themeElements></a:theme>";
        }

        private static string BuildSolidThemeFill()
        {
            return "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        }

        private static string BuildThemeLine(string width)
        {
            return "<a:ln w=\"" + width +
                "\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>";
        }

        private static string BuildCoreProperties()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>FilePrompt AI 输出</dc:title><dc:creator>FilePrompt AI</dc:creator><cp:lastModifiedBy>FilePrompt AI</cp:lastModifiedBy></cp:coreProperties>";
        }

        private static string BuildAppProperties(int count)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\"><Application>FilePrompt AI</Application><PresentationFormat>屏幕演示文稿 (16:9)</PresentationFormat><Slides>" + count.ToString() + "</Slides></Properties>";
        }
    }
}
