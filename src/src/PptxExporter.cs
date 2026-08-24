using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace FilePromptAIWin7
{
    // Small, dependency-free Open XML writer for the offline build. It keeps
    // the generated deck intentionally simple so it opens on Office 2007+.
    internal static class PptxExporter
    {
        private const long SlideWidth = 12192000L;
        private const long SlideHeight = 6858000L;
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
                        titleConsumed = true;
                        continue;
                    }

                    AppendBlockLines(current.Lines, block);
                    if (current.Lines.Count >= 10)
                    {
                        result.Add(current);
                        current = new SlideModel("继续");
                        titleConsumed = true;
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

        private static void AppendBlockLines(IList<string> lines, MarkdownBlock block)
        {
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
                return;
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
                return;
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
        }

        private static string NormalizeLine(string value)
        {
            string text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            text = text.Trim();
            if (text.Length > 155)
            {
                text = text.Substring(0, 152) + "...";
            }
            return text;
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
            return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
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
                "<p:sldMaster xmlns:a=\"" + DrawingNamespace + "\" xmlns:r=\"" + RelationshipsNamespace + "\" xmlns:p=\"" + PresentationNamespace + "\"><p:cSld name=\"FilePrompt AI\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"1\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>";
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
            AppendTextShape(xml, 2, "标题", 500000, 300000, 11200000, 900000, slide.Title, 30, "1F2937", true);
            AppendTextShape(xml, 3, "正文", 700000, 1350000, 10800000, 4700000, string.Join("\n", slide.Lines), 18, "263238", false);
            AppendTextShape(xml, 4, "页码", 10500000, 6250000, 1200000, 350000, number.ToString(), 11, "64748B", false);
            xml.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            return xml.ToString();
        }

        private static void AppendTextShape(StringBuilder xml, int id, string name, long x, long y, long cx, long cy, string text, int size, string color, bool bold)
        {
            xml.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id.ToString()).Append("\" name=\"").Append(Xml(name)).Append("\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"").Append(x.ToString()).Append("\" y=\"").Append(y.ToString()).Append("\"/><a:ext cx=\"").Append(cx.ToString()).Append("\" cy=\"").Append(cy.ToString()).Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln><a:noFill/></a:ln></p:spPr><p:txBody><a:bodyPr wrap=\"square\"/><a:lstStyle/>");
            string[] paragraphs = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split(new[] { '\n' }, StringSplitOptions.None);
            foreach (string paragraph in paragraphs)
            {
                xml.Append("<a:p><a:pPr marL=\"0\" indent=\"0\"/><a:r><a:rPr lang=\"zh-CN\" sz=\"").Append((size * 100).ToString()).Append("\"");
                if (bold)
                {
                    xml.Append(" b=\"1\"");
                }
                xml.Append("><a:solidFill><a:srgbClr val=\"").Append(color).Append("\"/></a:solidFill><a:latin typeface=\"Microsoft YaHei\"/><a:ea typeface=\"Microsoft YaHei\"/></a:rPr><a:t>").Append(Xml(paragraph)).Append("</a:t></a:r><a:endParaRPr lang=\"zh-CN\"/></a:p>");
            }
            xml.Append("</p:txBody></p:sp>");
        }

        private static string BuildTheme()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<a:theme xmlns:a=\"" + DrawingNamespace + "\" name=\"FilePrompt AI\"><a:themeElements><a:clrScheme name=\"FilePrompt AI\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"1F2937\"/></a:dk2><a:lt2><a:srgbClr val=\"F8FAFC\"/></a:lt2><a:accent1><a:srgbClr val=\"0F766E\"/></a:accent1><a:accent2><a:srgbClr val=\"2563EB\"/></a:accent2><a:accent3><a:srgbClr val=\"D97706\"/></a:accent3><a:accent4><a:srgbClr val=\"DC2626\"/></a:accent4><a:accent5><a:srgbClr val=\"7C3AED\"/></a:accent5><a:accent6><a:srgbClr val=\"0891B2\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme><a:fontScheme name=\"FilePrompt AI\"><a:majorFont><a:latin typeface=\"Microsoft YaHei\"/></a:majorFont><a:minorFont><a:latin typeface=\"Microsoft YaHei\"/></a:minorFont></a:fontScheme><a:fmtScheme name=\"FilePrompt AI\"><a:fillStyleLst/><a:lnStyleLst/><a:effectStyleLst/><a:bgFillStyleLst/></a:fmtScheme></a:themeElements></a:theme>";
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
