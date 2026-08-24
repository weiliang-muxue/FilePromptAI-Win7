using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FilePromptAIWin7
{
    // Creates the XML package used by XMind without requiring an installed
    // Office/XMind application or any online dependency.
    internal static class XMindExporter
    {
        private const string ContentNamespace =
            "urn:xmind:xmap:xmlns:content:2.0";
        private const string StyleNamespace =
            "urn:xmind:xmap:xmlns:style:2.0";
        private const string MetaNamespace =
            "urn:xmind:xmap:xmlns:meta:2.0";
        private const string ManifestNamespace =
            "urn:xmind:xmap:xmlns:manifest:1.0";
        private const string FoNamespace =
            "http://www.w3.org/1999/XSL/Format";
        private const string SvgNamespace =
            "http://www.w3.org/2000/svg";
        private const string XhtmlNamespace =
            "http://www.w3.org/1999/xhtml";

        private sealed class TopicNode
        {
            public string Id;
            public string Title;
            public string Notes;
            public string StyleId;
            public IList<TopicNode> Children;

            public TopicNode()
            {
                Id = string.Empty;
                Title = string.Empty;
                Notes = string.Empty;
                StyleId = string.Empty;
                Children = new List<TopicNode>();
            }
        }

        private sealed class HeadingFrame
        {
            public int Level;
            public TopicNode Topic;

            public HeadingFrame(int level, TopicNode topic)
            {
                Level = level;
                Topic = topic;
            }
        }

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

            TopicNode root = BuildTopicTree(document);
            string content = BuildContentXml(root);
            string styles = BuildStylesXml();
            string meta = BuildMetaXml();
            string manifest = BuildManifestXml();

            MemoryStream memory = new MemoryStream();
            using (ZipArchive archive = new ZipArchive(
                memory,
                ZipArchiveMode.Create,
                true,
                Encoding.UTF8))
            {
                // XMind expects this entry to be the first, uncompressed file.
                WriteEntry(
                    archive,
                    "mimetype",
                    "application/xmind",
                    CompressionLevel.NoCompression,
                    Encoding.ASCII);
                WriteEntry(archive, "content.xml", content);
                WriteEntry(archive, "styles.xml", styles);
                WriteEntry(archive, "meta.xml", meta);
                WriteEntry(
                    archive,
                    "META-INF/manifest.xml",
                    manifest);
            }

            return memory.ToArray();
        }

        private static TopicNode BuildTopicTree(MarkdownDocument document)
        {
            string rootTitle = "FilePrompt AI 导出";
            int titleBlockIndex = -1;
            for (int index = 0; index < document.Blocks.Count; index++)
            {
                MarkdownBlock block = document.Blocks[index];
                if (block != null &&
                    block.Kind == MarkdownBlockKind.Heading &&
                    block.Level <= 1)
                {
                    string title = SingleLine(block.Text);
                    if (title.Length > 0)
                    {
                        rootTitle = title;
                        titleBlockIndex = index;
                    }

                    break;
                }
            }

            int nextId = 1;
            TopicNode root = NewTopic(rootTitle, "style-root", ref nextId);
            List<HeadingFrame> headings = new List<HeadingFrame>();
            headings.Add(new HeadingFrame(0, root));

            for (int index = 0; index < document.Blocks.Count; index++)
            {
                if (index == titleBlockIndex)
                {
                    continue;
                }

                MarkdownBlock block = document.Blocks[index];
                if (block == null)
                {
                    continue;
                }

                if (block.Kind == MarkdownBlockKind.Heading)
                {
                    int level = Math.Max(1, Math.Min(6, block.Level));
                    while (headings.Count > 1 &&
                        headings[headings.Count - 1].Level >= level)
                    {
                        headings.RemoveAt(headings.Count - 1);
                    }

                    TopicNode heading = NewTopic(
                        SingleLine(block.Text),
                        "style-topic",
                        ref nextId);
                    headings[headings.Count - 1].Topic.Children.Add(heading);
                    headings.Add(new HeadingFrame(level, heading));
                    continue;
                }

                AppendBlock(
                    headings[headings.Count - 1].Topic,
                    block,
                    ref nextId);
            }

            return root;
        }

        private static void AppendBlock(
            TopicNode parent,
            MarkdownBlock block,
            ref int nextId)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Paragraph:
                    parent.Children.Add(
                        CreateTextTopic("段落", block.Text, ref nextId));
                    break;
                case MarkdownBlockKind.CodeBlock:
                    string language = SingleLine(block.Language);
                    string codeTitle = language.Length == 0
                        ? "代码"
                        : "代码（" + language + "）";
                    parent.Children.Add(
                        CreateTextTopic(codeTitle, block.Text, ref nextId));
                    break;
                case MarkdownBlockKind.Quote:
                    parent.Children.Add(
                        CreateTextTopic("引用", block.Text, ref nextId));
                    break;
                case MarkdownBlockKind.List:
                    TopicNode list = NewTopic(
                        block.Ordered ? "有序列表" : "列表",
                        "style-topic",
                        ref nextId);
                    parent.Children.Add(list);
                    if (block.Items != null)
                    {
                        for (int index = 0; index < block.Items.Count; index++)
                        {
                            string item = block.Items[index] ?? string.Empty;
                            string prefix = block.Ordered
                                ? (index + 1).ToString(
                                    CultureInfo.InvariantCulture) + ". "
                                : "• ";
                            list.Children.Add(
                                CreateTextTopic(
                                    prefix + SingleLine(item),
                                    item,
                                    ref nextId));
                        }
                    }
                    break;
                case MarkdownBlockKind.Table:
                    AppendTable(parent, block.Table, ref nextId);
                    break;
                case MarkdownBlockKind.HorizontalRule:
                    parent.Children.Add(
                        NewTopic("分隔线", "style-topic", ref nextId));
                    break;
            }
        }

        private static void AppendTable(
            TopicNode parent,
            MarkdownTable table,
            ref int nextId)
        {
            TopicNode tableTopic = NewTopic("表格", "style-topic", ref nextId);
            parent.Children.Add(tableTopic);
            if (table == null)
            {
                return;
            }

            IList<IList<string>> rows = table.GetAllRows();
            for (int index = 0; index < rows.Count; index++)
            {
                string rowText = JoinCells(rows[index]);
                string title = index == 0
                    ? "表头: " + rowText
                    : "第" + index.ToString(CultureInfo.InvariantCulture) +
                        "行: " + rowText;
                tableTopic.Children.Add(
                    NewTopic(title, "style-topic", ref nextId));
            }
        }

        private static TopicNode CreateTextTopic(
            string fallbackTitle,
            string text,
            ref int nextId)
        {
            string sanitized = SanitizeText(text);
            string title = SingleLine(sanitized);
            if (title.Length == 0)
            {
                title = fallbackTitle;
            }

            TopicNode topic = NewTopic(title, "style-topic", ref nextId);
            if (sanitized.IndexOf('\n') >= 0 ||
                sanitized.IndexOf('\r') >= 0)
            {
                topic.Notes = sanitized;
            }

            return topic;
        }

        private static TopicNode NewTopic(
            string title,
            string styleId,
            ref int nextId)
        {
            TopicNode topic = new TopicNode();
            topic.Id = "topic-" + nextId.ToString(CultureInfo.InvariantCulture);
            topic.Title = SingleLine(title);
            topic.StyleId = styleId ?? string.Empty;
            nextId++;
            return topic;
        }

        private static string BuildContentXml(TopicNode root)
        {
            StringBuilder builder = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.OmitXmlDeclaration = true;
            settings.CheckCharacters = false;
            using (XmlWriter writer = XmlWriter.Create(builder, settings))
            {
                writer.WriteStartElement("xmap-content", ContentNamespace);
                writer.WriteAttributeString(
                    "timestamp",
                    DateTime.UtcNow.ToString(
                        "yyyy-MM-ddTHH:mm:ss.fffZ",
                        CultureInfo.InvariantCulture));
                writer.WriteAttributeString("version", "2.0");
                writer.WriteStartElement("sheet", ContentNamespace);
                writer.WriteAttributeString("id", "sheet-1");
                writer.WriteElementString("title", ContentNamespace, root.Title);
                WriteTopic(writer, root);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
                builder.ToString();
        }

        private static void WriteTopic(XmlWriter writer, TopicNode topic)
        {
            writer.WriteStartElement("topic", ContentNamespace);
            writer.WriteAttributeString("id", topic.Id);
            if (!string.IsNullOrEmpty(topic.StyleId))
            {
                writer.WriteAttributeString("style-id", topic.StyleId);
            }

            writer.WriteElementString("title", ContentNamespace, topic.Title);
            if (!string.IsNullOrEmpty(topic.Notes))
            {
                writer.WriteStartElement("notes", ContentNamespace);
                writer.WriteStartElement("plain", ContentNamespace);
                writer.WriteString(topic.Notes);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            if (topic.Children.Count > 0)
            {
                writer.WriteStartElement("children", ContentNamespace);
                writer.WriteStartElement("topics", ContentNamespace);
                writer.WriteAttributeString("type", "attached");
                foreach (TopicNode child in topic.Children)
                {
                    WriteTopic(writer, child);
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        private static string BuildStylesXml()
        {
            StringBuilder builder = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.OmitXmlDeclaration = true;
            using (XmlWriter writer = XmlWriter.Create(builder, settings))
            {
                writer.WriteStartElement("xmap-styles", StyleNamespace);
                writer.WriteAttributeString("version", "2.0");
                writer.WriteAttributeString("xmlns", "fo", null, FoNamespace);
                writer.WriteAttributeString("xmlns", "svg", null, SvgNamespace);
                writer.WriteAttributeString(
                    "xmlns",
                    "xhtml",
                    null,
                    XhtmlNamespace);
                WriteStyle(writer, "style-root", "map", "20pt", true);
                WriteStyle(writer, "style-topic", "attached", "12pt", false);
                writer.WriteEndElement();
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
                builder.ToString();
        }

        private static void WriteStyle(
            XmlWriter writer,
            string id,
            string type,
            string fontSize,
            bool bold)
        {
            writer.WriteStartElement("style", StyleNamespace);
            writer.WriteAttributeString("id", id);
            writer.WriteStartElement("topic-style", StyleNamespace);
            writer.WriteAttributeString("type", type);
            writer.WriteStartElement("properties", StyleNamespace);
            writer.WriteAttributeString("fo", "font-size", FoNamespace, fontSize);
            if (bold)
            {
                writer.WriteAttributeString(
                    "fo",
                    "font-weight",
                    FoNamespace,
                    "bold");
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private static string BuildMetaXml()
        {
            StringBuilder builder = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.OmitXmlDeclaration = true;
            using (XmlWriter writer = XmlWriter.Create(builder, settings))
            {
                writer.WriteStartElement("meta", MetaNamespace);
                writer.WriteElementString("Author", MetaNamespace, "FilePrompt AI");
                writer.WriteElementString("Creator", MetaNamespace, "FilePrompt AI");
                writer.WriteElementString(
                    "CreateTime",
                    MetaNamespace,
                    DateTime.UtcNow.ToString(
                        "o",
                        CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
                builder.ToString();
        }

        private static string BuildManifestXml()
        {
            StringBuilder builder = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.OmitXmlDeclaration = true;
            using (XmlWriter writer = XmlWriter.Create(builder, settings))
            {
                writer.WriteStartElement("manifest", ManifestNamespace);
                WriteManifestEntry(writer, "/", "application/xmind");
                WriteManifestEntry(writer, "mimetype", "text/plain");
                WriteManifestEntry(writer, "content.xml", "text/xml");
                WriteManifestEntry(writer, "styles.xml", "text/xml");
                WriteManifestEntry(writer, "meta.xml", "text/xml");
                WriteManifestEntry(
                    writer,
                    "META-INF/manifest.xml",
                    "text/xml");
                writer.WriteEndElement();
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
                builder.ToString();
        }

        private static void WriteManifestEntry(
            XmlWriter writer,
            string path,
            string mediaType)
        {
            writer.WriteStartElement("file-entry", ManifestNamespace);
            writer.WriteAttributeString("full-path", path);
            writer.WriteAttributeString("media-type", mediaType);
            writer.WriteEndElement();
        }

        private static void WriteEntry(
            ZipArchive archive,
            string name,
            string value)
        {
            WriteEntry(archive, name, value, CompressionLevel.Optimal, Encoding.UTF8);
        }

        private static void WriteEntry(
            ZipArchive archive,
            string name,
            string value,
            CompressionLevel compression,
            Encoding encoding)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, compression);
            byte[] bytes = encoding.GetBytes(value ?? string.Empty);
            using (Stream stream = entry.Open())
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static string JoinCells(IList<string> cells)
        {
            if (cells == null || cells.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < cells.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append(SingleLine(cells[index]));
            }

            return builder.ToString();
        }

        private static string SingleLine(string value)
        {
            string text = SanitizeText(value);
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            return text.Trim();
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
    }
}
