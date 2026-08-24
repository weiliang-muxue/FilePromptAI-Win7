using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace FilePromptAIWin7
{
    internal sealed class FileContentExtractor
    {
        private const int MaxTextCharacters = 2000000;
        private const long MaxSourceBytes = 100L * 1024L * 1024L;
        private const long MaxInlineFileBytes = 20L * 1024L * 1024L;
        private const long MaxOfficeXmlBytes = 32L * 1024L * 1024L;
        private const long MaxArchiveExpandedBytes = 256L * 1024L * 1024L;
        private const int MaxArchiveEntries = 4096;
        private const int MaxArchiveEntryNameCharacters = 512;
        private const long MinCompressionRatioCheckBytes = 1024L * 1024L;
        private const long MaxCompressionRatio = 200L;
        private const int MaxXMindTopics = 100000;
        private const int MaxXMindDepth = 128;
        private const int MaxImageSide = 2048;
        private const int MaxWorksheetColumns = 16384;

        private static readonly HashSet<string> ImageExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
            };

        private static readonly HashSet<string> TextExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl",
                ".xml", ".xsd", ".xsl", ".yaml", ".yml", ".ini", ".conf", ".config",
                ".properties", ".log", ".sql", ".java", ".cs", ".vb", ".fs", ".cpp",
                ".cc", ".c", ".h", ".hpp", ".py", ".js", ".mjs", ".cjs", ".ts",
                ".tsx", ".jsx", ".vue", ".html", ".htm", ".css", ".scss", ".less",
                ".php", ".rb", ".go", ".rs", ".swift", ".kt", ".kts", ".gradle",
                ".bat", ".cmd", ".ps1", ".sh", ".dockerfile", ".gitignore", ".svg"
            };

        public InputItem ExtractFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("找不到文件。", path);
            }

            FileInfo info = new FileInfo(path);
            if (info.Length > MaxSourceBytes)
            {
                throw new InvalidOperationException(
                    "文件超过 100 MB，为避免程序无响应，本版本不处理该文件。");
            }

            string extension = info.Extension.ToLowerInvariant();
            if (ImageExtensions.Contains(extension))
            {
                return ExtractImageFile(path, info.Name, info.Length);
            }

            if (extension == ".pdf")
            {
                return ExtractPdf(path, info.Name, info.Length);
            }

            if (extension == ".docx")
            {
                return CreateTextItem(info.Name, ExtractDocx(path), info.Length, "Word 文档");
            }

            if (extension == ".xlsx")
            {
                return CreateTextItem(info.Name, ExtractXlsx(path), info.Length, "Excel 工作簿");
            }

            if (extension == ".pptx")
            {
                return CreateTextItem(
                    info.Name,
                    ExtractPptx(path),
                    info.Length,
                    "PowerPoint 演示文稿");
            }

            if (extension == ".xmind")
            {
                return CreateTextItem(
                    info.Name,
                    ExtractXMind(path),
                    info.Length,
                    "XMind 思维导图");
            }

            if (extension == ".doc")
            {
                return CreateTextItem(info.Name, ExtractLegacyDoc(path), info.Length, "Word 97-2003 文档");
            }

            if (extension == ".xls")
            {
                return CreateTextItem(info.Name, ExtractLegacyXls(path), info.Length, "Excel 97-2003 工作簿");
            }

            if (extension == ".rtf")
            {
                return CreateTextItem(info.Name, ExtractRtf(path), info.Length, "RTF 文档");
            }

            if (TextExtensions.Contains(extension) || LooksLikeTextFile(path))
            {
                return CreateTextItem(info.Name, ReadTextFile(path), info.Length, "文本/代码");
            }

            throw new NotSupportedException(
                "暂不支持“" + extension + "”格式。可把内容复制后点击“粘贴内容”。");
        }

        public InputItem CreateClipboardText(string text)
        {
            return CreateTextItem(
                "剪贴板文本 " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"),
                text ?? string.Empty,
                Encoding.UTF8.GetByteCount(text ?? string.Empty),
                "从剪贴板粘贴");
        }

        public InputItem CreateClipboardImage(Image image)
        {
            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            string name = "剪贴板图片 " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".png";
            return CreateImageItem(image, name, 0);
        }

        private static InputItem CreateTextItem(
            string name,
            string text,
            long originalBytes,
            string note)
        {
            bool truncated = text != null && text.Length > MaxTextCharacters;
            if (truncated)
            {
                int cutoff = MaxTextCharacters;
                if (cutoff > 0 &&
                    cutoff < text.Length &&
                    char.IsHighSurrogate(text[cutoff - 1]) &&
                    char.IsLowSurrogate(text[cutoff]))
                {
                    cutoff--;
                }

                text = text.Substring(0, cutoff)
                    + "\r\n\r\n[内容过长，已在 2,000,000 字符处截断]";
            }

            return new InputItem
            {
                Name = name,
                Kind = InputKind.Text,
                TextContent = text ?? string.Empty,
                OriginalBytes = originalBytes,
                Note = truncated ? note + "；内容已截断" : note
            };
        }

        private static string ReadTextFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0xEF &&
                bytes[1] == 0xBB &&
                bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);
                return strictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    return Encoding.GetEncoding("GB18030").GetString(bytes);
                }
                catch
                {
                    return Encoding.Default.GetString(bytes);
                }
            }
        }

        private static bool LooksLikeTextFile(string path)
        {
            byte[] sample = new byte[4096];
            int count;
            using (FileStream stream = File.OpenRead(path))
            {
                count = stream.Read(sample, 0, sample.Length);
            }

            if (count == 0)
            {
                return true;
            }

            int suspicious = 0;
            for (int index = 0; index < count; index++)
            {
                byte value = sample[index];
                if (value == 0)
                {
                    return false;
                }

                if (value < 8 || (value > 13 && value < 32))
                {
                    suspicious++;
                }
            }

            return (long)suspicious * 20L < count;
        }

        private static string ExtractRtf(string path)
        {
            using (RichTextBox richText = new RichTextBox())
            {
                richText.LoadFile(path, RichTextBoxStreamType.RichText);
                return richText.Text;
            }
        }

        private static string ExtractDocx(string path)
        {
            LimitedTextBuilder output = new LimitedTextBuilder(MaxTextCharacters);
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                ZipArchiveEntry documentEntry = GetEntry(archive, "word/document.xml");
                if (documentEntry == null)
                {
                    throw new InvalidDataException("DOCX 中缺少 word/document.xml。");
                }

                AppendWordXml(output, documentEntry, null);

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string entryName = entry.FullName.ToLowerInvariant();
                    if (entryName.StartsWith("word/header") && entryName.EndsWith(".xml"))
                    {
                        AppendWordXml(output, entry, "页眉");
                    }
                    else if (entryName.StartsWith("word/footer") && entryName.EndsWith(".xml"))
                    {
                        AppendWordXml(output, entry, "页脚");
                    }
                }
            }

            return output.ToString();
        }

        private static void AppendWordXml(
            LimitedTextBuilder output,
            ZipArchiveEntry entry,
            string sectionName)
        {
            XDocument document = LoadOfficeXml(
                entry,
                "文档内部 XML",
                LoadOptions.PreserveWhitespace);

            if (!string.IsNullOrEmpty(sectionName))
            {
                output.AppendLine();
                output.AppendLine("[" + sectionName + "]");
            }

            IEnumerable<XElement> paragraphs =
                document.Descendants().Where(element => element.Name.LocalName == "p");
            foreach (XElement paragraph in paragraphs)
            {
                StringBuilder line = new StringBuilder();
                foreach (XElement node in paragraph.Descendants())
                {
                    string localName = node.Name.LocalName;
                    if (localName == "t")
                    {
                        line.Append(node.Value);
                    }
                    else if (localName == "tab")
                    {
                        line.Append('\t');
                    }
                    else if (localName == "br" || localName == "cr")
                    {
                        line.AppendLine();
                    }
                }

                string paragraphText = line.ToString();
                if (paragraphText.Length > 0)
                {
                    output.AppendLine(paragraphText);
                }

                if (output.IsFull)
                {
                    break;
                }
            }
        }

        private static string ExtractXlsx(string path)
        {
            LimitedTextBuilder output = new LimitedTextBuilder(MaxTextCharacters);
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                IList<string> sharedStrings = ReadSharedStrings(archive);
                ZipArchiveEntry workbookEntry = GetEntry(archive, "xl/workbook.xml");
                ZipArchiveEntry relationshipsEntry =
                    GetEntry(archive, "xl/_rels/workbook.xml.rels");

                if (workbookEntry == null)
                {
                    throw new InvalidDataException("XLSX 中缺少 xl/workbook.xml。");
                }

                Dictionary<string, string> relationshipTargets =
                    ReadWorkbookRelationships(relationshipsEntry);

                XDocument workbook = LoadOfficeXml(
                    workbookEntry,
                    "Excel 工作簿定义",
                    LoadOptions.None);

                int sheetIndex = 0;
                foreach (XElement sheetElement in
                    workbook.Descendants().Where(element => element.Name.LocalName == "sheet"))
                {
                    sheetIndex++;
                    string sheetName = GetAttributeValue(sheetElement, "name");
                    string relationshipId = GetAttributeValue(sheetElement, "id");
                    string target;
                    if (!relationshipTargets.TryGetValue(relationshipId, out target))
                    {
                        target = "worksheets/sheet" + sheetIndex.ToString(CultureInfo.InvariantCulture) + ".xml";
                    }

                    string entryPath = ResolveWorkbookTarget(target);
                    ZipArchiveEntry sheetEntry = GetEntry(archive, entryPath);
                    if (sheetEntry == null)
                    {
                        continue;
                    }

                    output.AppendLine();
                    output.AppendLine("===== 工作表：" + (string.IsNullOrEmpty(sheetName)
                        ? sheetIndex.ToString(CultureInfo.InvariantCulture)
                        : sheetName) + " =====");
                    AppendWorksheet(output, sheetEntry, sharedStrings);
                    if (output.IsFull)
                    {
                        break;
                    }
                }
            }

            return output.ToString();
        }

        private static IList<string> ReadSharedStrings(ZipArchive archive)
        {
            List<string> result = new List<string>();
            ZipArchiveEntry entry = GetEntry(archive, "xl/sharedStrings.xml");
            if (entry == null)
            {
                return result;
            }

            XDocument document = LoadOfficeXml(
                entry,
                "Excel 共享字符串表",
                LoadOptions.PreserveWhitespace);

            foreach (XElement item in
                document.Descendants().Where(element => element.Name.LocalName == "si"))
            {
                StringBuilder value = new StringBuilder();
                foreach (XElement text in
                    item.Descendants().Where(element => element.Name.LocalName == "t"))
                {
                    value.Append(text.Value);
                }

                result.Add(value.ToString());
            }

            return result;
        }

        private static Dictionary<string, string> ReadWorkbookRelationships(
            ZipArchiveEntry entry)
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entry == null)
            {
                return result;
            }

            XDocument document = LoadOfficeXml(
                entry,
                "Excel 工作簿关系",
                LoadOptions.None);

            foreach (XElement relationship in
                document.Descendants().Where(element => element.Name.LocalName == "Relationship"))
            {
                string id = GetAttributeValue(relationship, "Id");
                string target = GetAttributeValue(relationship, "Target");
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                {
                    result[id] = target;
                }
            }

            return result;
        }

        private static void AppendWorksheet(
            LimitedTextBuilder output,
            ZipArchiveEntry entry,
            IList<string> sharedStrings)
        {
            XDocument document = LoadOfficeXml(
                entry,
                "Excel 工作表 XML",
                LoadOptions.PreserveWhitespace);

            int renderedRows = 0;
            foreach (XElement row in
                document.Descendants().Where(element => element.Name.LocalName == "row"))
            {
                SortedDictionary<int, string> cells = new SortedDictionary<int, string>();
                int fallbackColumn = 0;
                foreach (XElement cell in
                    row.Elements().Where(element => element.Name.LocalName == "c"))
                {
                    string reference = GetAttributeValue(cell, "r");
                    int column = string.IsNullOrEmpty(reference)
                        ? fallbackColumn
                        : GetColumnIndex(reference);
                    if (column < 0 || column >= MaxWorksheetColumns)
                    {
                        throw new InvalidDataException(
                            "Excel 单元格列引用超出有效范围（A 到 XFD）。");
                    }

                    fallbackColumn = column + 1;
                    cells[column] = GetCellValue(cell, sharedStrings);
                }

                if (cells.Count > 0)
                {
                    int lastColumn = cells.Keys.Last();
                    StringBuilder line = new StringBuilder();
                    for (int column = 0; column <= lastColumn; column++)
                    {
                        if (column > 0)
                        {
                            line.Append('\t');
                        }

                        string value;
                        if (cells.TryGetValue(column, out value))
                        {
                            line.Append(EscapeSpreadsheetValue(value));
                        }
                    }

                    output.AppendLine(line.ToString());
                    renderedRows++;
                }

                if (output.IsFull || renderedRows >= 50000)
                {
                    if (renderedRows >= 50000)
                    {
                        output.AppendLine("[工作表超过 50,000 个非空行，后续内容已省略]");
                    }

                    break;
                }
            }
        }

        private static string ExtractPptx(string path)
        {
            LimitedTextBuilder output = new LimitedTextBuilder(MaxTextCharacters);
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                IDictionary<string, ZipArchiveEntry> entries =
                    ValidateStructuredArchive(archive, "PPTX");
                List<NumberedArchiveEntry> slides = GetNumberedEntries(
                    entries,
                    "ppt/slides/slide",
                    ".xml",
                    "PPTX 幻灯片");
                if (slides.Count == 0)
                {
                    throw new InvalidDataException(
                        "PPTX 中缺少 ppt/slides/slideN.xml。");
                }

                foreach (NumberedArchiveEntry slide in slides)
                {
                    output.AppendLine();
                    output.AppendLine(
                        "===== 第 " +
                        slide.Number.ToString(CultureInfo.InvariantCulture) +
                        " 页 =====");
                    XDocument slideDocument = LoadOfficeXml(
                        slide.Entry,
                        "PowerPoint 幻灯片 XML",
                        LoadOptions.PreserveWhitespace);
                    AppendPowerPointSlide(output, slideDocument);

                    ZipArchiveEntry notesEntry = GetPowerPointNotesEntry(
                        entries,
                        slide);
                    if (notesEntry != null)
                    {
                        XDocument notesDocument = LoadOfficeXml(
                            notesEntry,
                            "PowerPoint 备注 XML",
                            LoadOptions.PreserveWhitespace);
                        AppendPowerPointNotes(output, notesDocument);
                    }

                    if (output.IsFull)
                    {
                        break;
                    }
                }
            }

            return output.ToString();
        }

        private static void AppendPowerPointSlide(
            LimitedTextBuilder output,
            XDocument document)
        {
            List<string> titles = new List<string>();
            List<string> body = new List<string>();
            foreach (XElement shape in document.Descendants().Where(
                element => element.Name.LocalName == "sp"))
            {
                List<string> paragraphs = ReadDrawingParagraphs(shape);
                if (paragraphs.Count == 0)
                {
                    continue;
                }

                string placeholderType = GetPowerPointPlaceholderType(shape);
                if (string.Equals(
                        placeholderType,
                        "title",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        placeholderType,
                        "ctrTitle",
                        StringComparison.OrdinalIgnoreCase))
                {
                    titles.AddRange(paragraphs);
                }
                else
                {
                    body.AddRange(paragraphs);
                }
            }

            AppendTextSection(output, "标题", titles);
            AppendTextSection(output, "正文", body);

            int tableIndex = 0;
            foreach (XElement table in document.Descendants().Where(
                element => element.Name.LocalName == "tbl"))
            {
                tableIndex++;
                output.AppendLine(
                    "[表格 " +
                    tableIndex.ToString(CultureInfo.InvariantCulture) +
                    "]");
                foreach (XElement row in table.Elements().Where(
                    element => element.Name.LocalName == "tr"))
                {
                    List<string> cells = new List<string>();
                    foreach (XElement cell in row.Elements().Where(
                        element => element.Name.LocalName == "tc"))
                    {
                        cells.Add(EscapeSpreadsheetValue(
                            string.Join(" / ", ReadDrawingParagraphs(cell).ToArray())));
                    }

                    output.AppendLine(string.Join("\t", cells.ToArray()));
                    if (output.IsFull)
                    {
                        return;
                    }
                }
            }
        }

        private static void AppendPowerPointNotes(
            LimitedTextBuilder output,
            XDocument document)
        {
            List<string> notes = new List<string>();
            foreach (XElement shape in document.Descendants().Where(
                element => element.Name.LocalName == "sp"))
            {
                string placeholderType = GetPowerPointPlaceholderType(shape);
                if (string.Equals(placeholderType, "sldImg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(placeholderType, "sldNum", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(placeholderType, "dt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(placeholderType, "hdr", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(placeholderType, "ftr", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                notes.AddRange(ReadDrawingParagraphs(shape));
            }

            AppendTextSection(output, "备注", notes);
        }

        private static string GetPowerPointPlaceholderType(XElement shape)
        {
            XElement placeholder = shape.Descendants().FirstOrDefault(
                element => element.Name.LocalName == "ph");
            return placeholder == null
                ? string.Empty
                : GetAttributeValue(placeholder, "type");
        }

        private static List<string> ReadDrawingParagraphs(XElement container)
        {
            List<string> result = new List<string>();
            foreach (XElement paragraph in container.Descendants().Where(
                element => element.Name.LocalName == "p"))
            {
                StringBuilder value = new StringBuilder();
                foreach (XElement node in paragraph.Descendants())
                {
                    string localName = node.Name.LocalName;
                    if (localName == "t")
                    {
                        value.Append(node.Value);
                    }
                    else if (localName == "tab")
                    {
                        value.Append('\t');
                    }
                    else if (localName == "br")
                    {
                        value.AppendLine();
                    }
                }

                string text = value.ToString().Trim();
                if (text.Length > 0)
                {
                    result.Add(text);
                }
            }

            return result;
        }

        private static void AppendTextSection(
            LimitedTextBuilder output,
            string name,
            IList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            output.AppendLine("[" + name + "]");
            foreach (string value in values)
            {
                output.AppendLine(value);
                if (output.IsFull)
                {
                    break;
                }
            }
        }

        private static ZipArchiveEntry GetPowerPointNotesEntry(
            IDictionary<string, ZipArchiveEntry> entries,
            NumberedArchiveEntry slide)
        {
            string relationshipPath = "ppt/slides/_rels/slide" +
                slide.Number.ToString(CultureInfo.InvariantCulture) +
                ".xml.rels";
            ZipArchiveEntry relationshipEntry;
            if (entries.TryGetValue(relationshipPath, out relationshipEntry))
            {
                XDocument relationships = LoadOfficeXml(
                    relationshipEntry,
                    "PowerPoint 幻灯片关系 XML",
                    LoadOptions.None);
                foreach (XElement relationship in relationships.Descendants().Where(
                    element => element.Name.LocalName == "Relationship"))
                {
                    string type = GetAttributeValue(relationship, "Type");
                    if (!type.EndsWith(
                        "/notesSlide",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(
                        GetAttributeValue(relationship, "TargetMode"),
                        "External",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "PPTX 备注关系不能指向外部地址。");
                    }

                    string resolved = ResolveArchiveRelationshipTarget(
                        slide.Path,
                        GetAttributeValue(relationship, "Target"));
                    ZipArchiveEntry notesEntry;
                    if (!entries.TryGetValue(resolved, out notesEntry))
                    {
                        throw new InvalidDataException(
                            "PPTX 备注关系指向的文件不存在。");
                    }

                    return notesEntry;
                }
            }

            string fallback = "ppt/notesSlides/notesSlide" +
                slide.Number.ToString(CultureInfo.InvariantCulture) + ".xml";
            ZipArchiveEntry fallbackEntry;
            return entries.TryGetValue(fallback, out fallbackEntry)
                ? fallbackEntry
                : null;
        }

        private static string ExtractXMind(string path)
        {
            LimitedTextBuilder output = new LimitedTextBuilder(MaxTextCharacters);
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                IDictionary<string, ZipArchiveEntry> entries =
                    ValidateStructuredArchive(archive, "XMind");
                ZipArchiveEntry contentEntry;
                if (entries.TryGetValue("content.json", out contentEntry))
                {
                    ExtractModernXMind(output, contentEntry);
                }
                else if (entries.TryGetValue("content.xml", out contentEntry))
                {
                    ExtractLegacyXMind(output, contentEntry);
                }
                else
                {
                    throw new InvalidDataException(
                        "XMind 中缺少内容入口 content.json 或 content.xml。");
                }
            }

            return output.ToString();
        }

        private static void ExtractLegacyXMind(
            LimitedTextBuilder output,
            ZipArchiveEntry contentEntry)
        {
            XDocument document = LoadOfficeXml(
                contentEntry,
                "XMind content.xml",
                LoadOptions.PreserveWhitespace);
            XElement root = document.Root;
            if (root == null || root.Name.LocalName != "xmap-content")
            {
                throw new InvalidDataException(
                    "XMind content.xml 根元素无效。");
            }

            List<XElement> sheets = root.Elements().Where(
                element => element.Name.LocalName == "sheet").ToList();
            if (sheets.Count == 0)
            {
                throw new InvalidDataException(
                    "XMind content.xml 中缺少画布。");
            }

            int sheetIndex = 0;
            foreach (XElement sheet in sheets)
            {
                sheetIndex++;
                AppendXMindSheetHeader(
                    output,
                    GetDirectChildText(sheet, "title"),
                    sheetIndex);
                XElement topic = sheet.Elements().FirstOrDefault(
                    element => element.Name.LocalName == "topic");
                if (topic == null)
                {
                    throw new InvalidDataException(
                        "XMind 画布中缺少根主题。");
                }

                AppendXMindTopicTree(output, topic);
                if (output.IsFull)
                {
                    break;
                }
            }
        }

        private static void ExtractModernXMind(
            LimitedTextBuilder output,
            ZipArchiveEntry contentEntry)
        {
            string jsonText = ReadArchiveEntryUtf8(
                contentEntry,
                "XMind content.json");
            object root;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = (int)MaxOfficeXmlBytes;
                serializer.RecursionLimit = MaxXMindDepth + 32;
                root = serializer.DeserializeObject(jsonText);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    "XMind content.json 不是有效的 JSON。",
                    exception);
            }

            IList<object> sheets = root as object[];
            IDictionary<string, object> rootObject =
                root as IDictionary<string, object>;
            if (sheets == null && rootObject != null)
            {
                object sheetValue;
                if (rootObject.TryGetValue("sheets", out sheetValue))
                {
                    sheets = sheetValue as object[];
                }
                else if (GetJsonString(rootObject, "class") == "sheet" ||
                    rootObject.ContainsKey("rootTopic"))
                {
                    sheets = new List<object> { rootObject };
                }
            }

            if (sheets == null || sheets.Count == 0)
            {
                throw new InvalidDataException(
                    "XMind content.json 中缺少画布。");
            }

            for (int index = 0; index < sheets.Count; index++)
            {
                IDictionary<string, object> sheet =
                    sheets[index] as IDictionary<string, object>;
                if (sheet == null)
                {
                    throw new InvalidDataException(
                        "XMind content.json 包含无效画布。");
                }

                AppendXMindSheetHeader(
                    output,
                    GetJsonString(sheet, "title"),
                    index + 1);
                object topicValue;
                IDictionary<string, object> topic =
                    sheet.TryGetValue("rootTopic", out topicValue)
                        ? topicValue as IDictionary<string, object>
                        : null;
                if (topic == null)
                {
                    throw new InvalidDataException(
                        "XMind 画布中缺少根主题。");
                }

                AppendModernXMindTopicTree(output, topic);
                if (output.IsFull)
                {
                    break;
                }
            }
        }

        private static void AppendXMindSheetHeader(
            LimitedTextBuilder output,
            string title,
            int index)
        {
            output.AppendLine();
            output.AppendLine(
                "===== 画布：" +
                (string.IsNullOrWhiteSpace(title)
                    ? index.ToString(CultureInfo.InvariantCulture)
                    : NormalizeExtractedText(title)) +
                " =====");
        }

        private static void AppendModernXMindTopicTree(
            LimitedTextBuilder output,
            IDictionary<string, object> rootTopic)
        {
            Stack<XMindJsonTopicFrame> pending =
                new Stack<XMindJsonTopicFrame>();
            pending.Push(new XMindJsonTopicFrame(rootTopic, 0));
            int topicCount = 0;
            while (pending.Count > 0)
            {
                XMindJsonTopicFrame frame = pending.Pop();
                if (frame.Depth > MaxXMindDepth)
                {
                    throw new InvalidDataException(
                        "XMind 主题层级超过 128 层安全限制。");
                }

                topicCount++;
                if (topicCount > MaxXMindTopics)
                {
                    throw new InvalidDataException(
                        "XMind 主题数量超过 100,000 个安全限制。");
                }

                string indent = new string(' ', frame.Depth * 2);
                string title = GetJsonString(frame.Topic, "title");
                output.AppendLine(
                    indent + "- " +
                    (title.Length == 0 ? "(未命名主题)" : title));
                string notes = GetModernXMindNotes(frame.Topic);
                if (notes.Length > 0)
                {
                    output.AppendLine(indent + "  [备注] " + notes);
                }

                if (output.IsFull)
                {
                    return;
                }

                List<IDictionary<string, object>> children =
                    GetModernXMindChildren(frame.Topic);
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    pending.Push(new XMindJsonTopicFrame(
                        children[index],
                        frame.Depth + 1));
                }
            }
        }

        private static List<IDictionary<string, object>> GetModernXMindChildren(
            IDictionary<string, object> topic)
        {
            List<IDictionary<string, object>> result =
                new List<IDictionary<string, object>>();
            object childrenValue;
            IDictionary<string, object> children =
                topic.TryGetValue("children", out childrenValue)
                    ? childrenValue as IDictionary<string, object>
                    : null;
            if (children == null)
            {
                return result;
            }

            List<string> groups = new List<string>();
            if (children.ContainsKey("attached"))
            {
                groups.Add("attached");
            }
            if (children.ContainsKey("detached"))
            {
                groups.Add("detached");
            }
            groups.AddRange(children.Keys.Where(
                key => key != "attached" && key != "detached")
                .OrderBy(key => key, StringComparer.Ordinal));

            foreach (string group in groups)
            {
                object[] topics = children[group] as object[];
                if (topics == null)
                {
                    continue;
                }

                foreach (object value in topics)
                {
                    IDictionary<string, object> child =
                        value as IDictionary<string, object>;
                    if (child != null)
                    {
                        result.Add(child);
                    }
                }
            }

            return result;
        }

        private static string GetModernXMindNotes(
            IDictionary<string, object> topic)
        {
            object notesValue;
            IDictionary<string, object> notes =
                topic.TryGetValue("notes", out notesValue)
                    ? notesValue as IDictionary<string, object>
                    : null;
            if (notes == null)
            {
                return string.Empty;
            }

            string plain = GetModernXMindNoteValue(notes, "plain");
            return plain.Length > 0
                ? plain
                : GetModernXMindNoteValue(notes, "html");
        }

        private static string GetModernXMindNoteValue(
            IDictionary<string, object> notes,
            string name)
        {
            object value;
            if (!notes.TryGetValue(name, out value) || value == null)
            {
                return string.Empty;
            }

            IDictionary<string, object> container =
                value as IDictionary<string, object>;
            if (container != null)
            {
                return GetJsonString(container, "content");
            }

            return NormalizeExtractedText(Convert.ToString(
                value,
                CultureInfo.InvariantCulture));
        }

        private static string GetJsonString(
            IDictionary<string, object> value,
            string name)
        {
            object item;
            return value != null && value.TryGetValue(name, out item) &&
                item != null
                ? NormalizeExtractedText(Convert.ToString(
                    item,
                    CultureInfo.InvariantCulture))
                : string.Empty;
        }

        private static string ReadArchiveEntryUtf8(
            ZipArchiveEntry entry,
            string description)
        {
            if (entry == null || entry.Length > MaxOfficeXmlBytes)
            {
                throw new InvalidDataException(
                    description + " 超过 32 MB 安全限制。");
            }

            using (Stream stream = entry.Open())
            using (MemoryStream buffer = new MemoryStream(
                (int)Math.Min(entry.Length, MaxOfficeXmlBytes)))
            {
                byte[] block = new byte[8192];
                int total = 0;
                while (true)
                {
                    int read = stream.Read(block, 0, block.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxOfficeXmlBytes)
                    {
                        throw new InvalidDataException(
                            description + " 超过 32 MB 安全限制。");
                    }

                    buffer.Write(block, 0, read);
                }

                try
                {
                    byte[] bytes = buffer.ToArray();
                    int offset = bytes.Length >= 3 &&
                        bytes[0] == 0xEF && bytes[1] == 0xBB &&
                        bytes[2] == 0xBF
                            ? 3
                            : 0;
                    return new UTF8Encoding(false, true).GetString(
                        bytes,
                        offset,
                        bytes.Length - offset);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException(
                        description + " 不是有效的 UTF-8 文本。",
                        exception);
                }
            }
        }

        private static void AppendXMindTopicTree(
            LimitedTextBuilder output,
            XElement rootTopic)
        {
            Stack<XMindTopicFrame> pending = new Stack<XMindTopicFrame>();
            pending.Push(new XMindTopicFrame(rootTopic, 0));
            int topicCount = 0;
            while (pending.Count > 0)
            {
                XMindTopicFrame frame = pending.Pop();
                if (frame.Depth > MaxXMindDepth)
                {
                    throw new InvalidDataException(
                        "XMind 主题层级超过 128 层安全限制。");
                }

                topicCount++;
                if (topicCount > MaxXMindTopics)
                {
                    throw new InvalidDataException(
                        "XMind 主题数量超过 100,000 个安全限制。");
                }

                string indent = new string(' ', frame.Depth * 2);
                string title = GetDirectChildText(frame.Topic, "title");
                output.AppendLine(
                    indent + "- " +
                    (title.Length == 0 ? "(未命名主题)" : title));
                string notes = GetXMindNotes(frame.Topic);
                if (notes.Length > 0)
                {
                    output.AppendLine(indent + "  [备注] " + notes);
                }

                if (output.IsFull)
                {
                    return;
                }

                List<XElement> children = GetXMindChildTopics(frame.Topic);
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    pending.Push(new XMindTopicFrame(
                        children[index],
                        frame.Depth + 1));
                }
            }
        }

        private static List<XElement> GetXMindChildTopics(XElement topic)
        {
            List<XElement> result = new List<XElement>();
            foreach (XElement children in topic.Elements().Where(
                element => element.Name.LocalName == "children"))
            {
                foreach (XElement topics in children.Elements().Where(
                    element => element.Name.LocalName == "topics"))
                {
                    result.AddRange(topics.Elements().Where(
                        element => element.Name.LocalName == "topic"));
                }
            }

            return result;
        }

        private static string GetXMindNotes(XElement topic)
        {
            XElement notes = topic.Elements().FirstOrDefault(
                element => element.Name.LocalName == "notes");
            if (notes == null)
            {
                return string.Empty;
            }

            XElement plain = notes.Elements().FirstOrDefault(
                element => element.Name.LocalName == "plain");
            if (plain != null && !string.IsNullOrWhiteSpace(plain.Value))
            {
                return NormalizeExtractedText(plain.Value);
            }

            XElement html = notes.Elements().FirstOrDefault(
                element => element.Name.LocalName == "html");
            return html == null
                ? NormalizeExtractedText(notes.Value)
                : NormalizeExtractedText(html.Value);
        }

        private static string GetDirectChildText(XElement parent, string name)
        {
            XElement child = parent.Elements().FirstOrDefault(
                element => element.Name.LocalName == name);
            return child == null
                ? string.Empty
                : NormalizeExtractedText(child.Value);
        }

        private static string NormalizeExtractedText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            StringBuilder result = new StringBuilder(value.Length);
            bool pendingSpace = false;
            foreach (char character in value)
            {
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = result.Length > 0;
                }
                else
                {
                    if (pendingSpace)
                    {
                        result.Append(' ');
                        pendingSpace = false;
                    }

                    result.Append(character);
                }
            }

            return result.ToString();
        }

        private static IDictionary<string, ZipArchiveEntry> ValidateStructuredArchive(
            ZipArchive archive,
            string description)
        {
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                throw new InvalidDataException(
                    description + " 条目数超过 4,096 个安全限制。");
            }

            Dictionary<string, ZipArchiveEntry> result =
                new Dictionary<string, ZipArchiveEntry>(
                    StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string entryName = ValidateArchiveEntryName(entry.FullName);
                if (result.ContainsKey(entryName))
                {
                    throw new InvalidDataException(
                        description + " 包含重复路径：" + entryName);
                }

                long length = entry.Length;
                long compressedLength = entry.CompressedLength;
                if (length < 0 || compressedLength < 0 ||
                    expandedBytes > MaxArchiveExpandedBytes - length)
                {
                    throw new InvalidDataException(
                        description + " 总解压大小超过 256 MB 安全限制。");
                }

                expandedBytes += length;
                if (length >= MinCompressionRatioCheckBytes &&
                    (compressedLength == 0 ||
                     length > compressedLength * MaxCompressionRatio))
                {
                    throw new InvalidDataException(
                        description + " 条目压缩比超过 200:1 安全限制：" +
                        entryName);
                }

                result.Add(entryName, entry);
            }

            return result;
        }

        private static string ValidateArchiveEntryName(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                value.Length > MaxArchiveEntryNameCharacters ||
                value[0] == '/' ||
                value.IndexOf('\\') >= 0 ||
                value.IndexOf(':') >= 0)
            {
                throw new InvalidDataException("压缩包包含不安全或过长的条目路径。");
            }

            string candidate = value.EndsWith("/", StringComparison.Ordinal)
                ? value.Substring(0, value.Length - 1)
                : value;
            if (candidate.Length == 0)
            {
                throw new InvalidDataException("压缩包包含空条目路径。");
            }

            string[] segments = candidate.Split('/');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    throw new InvalidDataException(
                        "压缩包包含未规范化或危险的条目路径：" + value);
                }
            }

            return candidate;
        }

        private static List<NumberedArchiveEntry> GetNumberedEntries(
            IDictionary<string, ZipArchiveEntry> entries,
            string prefix,
            string suffix,
            string description)
        {
            List<NumberedArchiveEntry> result = new List<NumberedArchiveEntry>();
            HashSet<int> numbers = new HashSet<int>();
            foreach (KeyValuePair<string, ZipArchiveEntry> pair in entries)
            {
                string name = pair.Key;
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string numeric = name.Substring(
                    prefix.Length,
                    name.Length - prefix.Length - suffix.Length);
                int number;
                if (numeric.Length == 0 ||
                    numeric.Any(character => character < '0' || character > '9') ||
                    !int.TryParse(
                        numeric,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out number) ||
                    number <= 0)
                {
                    throw new InvalidDataException(
                        description + "编号无效：" + name);
                }

                if (!numbers.Add(number))
                {
                    throw new InvalidDataException(
                        description + "编号重复：" +
                        number.ToString(CultureInfo.InvariantCulture));
                }

                result.Add(new NumberedArchiveEntry(number, name, pair.Value));
            }

            result.Sort(delegate(NumberedArchiveEntry left, NumberedArchiveEntry right)
            {
                return left.Number.CompareTo(right.Number);
            });
            return result;
        }

        private static string ResolveArchiveRelationshipTarget(
            string sourcePath,
            string target)
        {
            if (string.IsNullOrWhiteSpace(target) ||
                target.IndexOf('\\') >= 0 ||
                target.IndexOf(':') >= 0)
            {
                throw new InvalidDataException(
                    "压缩包关系包含不安全的目标路径。");
            }

            List<string> segments = new List<string>();
            if (!target.StartsWith("/", StringComparison.Ordinal))
            {
                int slash = sourcePath.LastIndexOf('/');
                if (slash >= 0)
                {
                    segments.AddRange(sourcePath.Substring(0, slash).Split('/'));
                }
            }

            foreach (string segment in target.TrimStart('/').Split('/'))
            {
                if (segment.Length == 0 || segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (segments.Count == 0)
                    {
                        throw new InvalidDataException(
                            "压缩包关系目标越出包根目录。");
                    }

                    segments.RemoveAt(segments.Count - 1);
                }
                else
                {
                    segments.Add(segment);
                }
            }

            string resolved = string.Join("/", segments.ToArray());
            return ValidateArchiveEntryName(resolved);
        }

        private static string GetCellValue(XElement cell, IList<string> sharedStrings)
        {
            string type = GetAttributeValue(cell, "t");
            XElement valueElement =
                cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v");
            XElement formulaElement =
                cell.Elements().FirstOrDefault(element => element.Name.LocalName == "f");

            string rawValue = valueElement == null ? string.Empty : valueElement.Value;
            string value;
            if (type == "s")
            {
                int index;
                if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) &&
                    index >= 0 &&
                    index < sharedStrings.Count)
                {
                    value = sharedStrings[index];
                }
                else
                {
                    value = rawValue;
                }
            }
            else if (type == "inlineStr")
            {
                StringBuilder inline = new StringBuilder();
                foreach (XElement text in
                    cell.Descendants().Where(element => element.Name.LocalName == "t"))
                {
                    inline.Append(text.Value);
                }

                value = inline.ToString();
            }
            else if (type == "b")
            {
                value = rawValue == "1" ? "TRUE" : "FALSE";
            }
            else
            {
                value = rawValue;
            }

            if (formulaElement != null && !string.IsNullOrWhiteSpace(formulaElement.Value))
            {
                return "=" + formulaElement.Value +
                    (string.IsNullOrEmpty(value) ? string.Empty : " => " + value);
            }

            return value;
        }

        private static string EscapeSpreadsheetValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\r\n", "\\n")
                .Replace("\n", "\\n")
                .Replace("\r", "\\n")
                .Replace("\t", "\\t");
        }

        private static int GetColumnIndex(string cellReference)
        {
            long result = 0;
            int letters = 0;
            foreach (char character in cellReference)
            {
                if (character < 'A' || character > 'Z')
                {
                    if (character < 'a' || character > 'z')
                    {
                        break;
                    }
                }

                char upper = char.ToUpperInvariant(character);
                result = result * 26 + (upper - 'A' + 1);
                if (result > MaxWorksheetColumns)
                {
                    return -1;
                }

                letters++;
            }

            return letters == 0 ? -1 : (int)result - 1;
        }

        private static XDocument LoadOfficeXml(
            ZipArchiveEntry entry,
            string description,
            LoadOptions options)
        {
            if (entry == null)
            {
                throw new InvalidDataException(description + "不存在。");
            }

            if (entry.Length < 0 || entry.Length > MaxOfficeXmlBytes)
            {
                throw new InvalidDataException(
                    description + "超过 32 MB 安全限制。");
            }

            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.MaxCharactersInDocument = MaxOfficeXmlBytes;
            settings.MaxCharactersFromEntities = 0;
            using (Stream stream = entry.Open())
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                return XDocument.Load(reader, options);
            }
        }

        private static string ResolveWorkbookTarget(string target)
        {
            string normalized = (target ?? string.Empty).Replace('\\', '/').TrimStart('/');
            while (normalized.StartsWith("../", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(3);
            }

            if (normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            return "xl/" + normalized;
        }

        private static string GetAttributeValue(XElement element, string localName)
        {
            XAttribute attribute = element.Attributes()
                .FirstOrDefault(item => item.Name.LocalName == localName);
            return attribute == null ? string.Empty : attribute.Value;
        }

        private static ZipArchiveEntry GetEntry(ZipArchive archive, string name)
        {
            return archive.Entries.FirstOrDefault(
                entry => string.Equals(
                    entry.FullName,
                    name,
                    StringComparison.OrdinalIgnoreCase));
        }

        private sealed class NumberedArchiveEntry
        {
            public NumberedArchiveEntry(
                int number,
                string path,
                ZipArchiveEntry entry)
            {
                Number = number;
                Path = path;
                Entry = entry;
            }

            public int Number { get; private set; }
            public string Path { get; private set; }
            public ZipArchiveEntry Entry { get; private set; }
        }

        private sealed class XMindTopicFrame
        {
            public XMindTopicFrame(XElement topic, int depth)
            {
                Topic = topic;
                Depth = depth;
            }

            public XElement Topic { get; private set; }
            public int Depth { get; private set; }
        }

        private sealed class XMindJsonTopicFrame
        {
            public XMindJsonTopicFrame(
                IDictionary<string, object> topic,
                int depth)
            {
                Topic = topic;
                Depth = depth;
            }

            public IDictionary<string, object> Topic { get; private set; }
            public int Depth { get; private set; }
        }

        private static InputItem ExtractPdf(string path, string name, long originalBytes)
        {
            string text = ExtractPdfTextWithPdfPig(path);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return CreateTextItem(name, text, originalBytes, "PDF 文本");
            }

            if (originalBytes > MaxInlineFileBytes)
            {
                throw new InvalidOperationException(
                    "该 PDF 没有可提取文字，且文件超过 20 MB，无法作为内联文件提交。");
            }

            return new InputItem
            {
                Name = name,
                Kind = InputKind.File,
                BinaryData = File.ReadAllBytes(path),
                MimeType = "application/pdf",
                OriginalBytes = originalBytes,
                Note = "未提取到文字，将以内联 PDF 提交给支持文件输入的模型"
            };
        }

        private static string ExtractPdfTextWithPdfPig(string path)
        {
            Assembly assembly = LoadAssemblyFromApplicationDirectory("UglyToad.PdfPig.dll");
            if (assembly == null)
            {
                throw new InvalidOperationException(
                    "缺少 PDF 解析组件 UglyToad.PdfPig.dll，请使用完整发布目录。");
            }

            Type documentType = assembly.GetType("UglyToad.PdfPig.PdfDocument", true);
            MethodInfo openMethod = documentType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return method.Name == "Open" &&
                        parameters.Length >= 1 &&
                        parameters[0].ParameterType == typeof(string);
                });
            if (openMethod == null)
            {
                throw new MissingMethodException("PdfDocument.Open(string)");
            }

            ParameterInfo[] openParameters = openMethod.GetParameters();
            object[] openArguments = new object[openParameters.Length];
            openArguments[0] = path;
            for (int index = 1; index < openArguments.Length; index++)
            {
                openArguments[index] = openParameters[index].HasDefaultValue
                    ? openParameters[index].DefaultValue
                    : null;
            }

            object document = null;
            try
            {
                document = openMethod.Invoke(null, openArguments);
                PropertyInfo pageCountProperty = documentType.GetProperty("NumberOfPages");
                MethodInfo getPageMethod = documentType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(method =>
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        return method.Name == "GetPage" &&
                            parameters.Length >= 1 &&
                            parameters[0].ParameterType == typeof(int);
                    })
                    .OrderBy(method => method.GetParameters().Length)
                    .FirstOrDefault();
                if (pageCountProperty == null || getPageMethod == null)
                {
                    throw new MissingMemberException("PDF 解析组件接口不匹配。");
                }

                int pageCount = Convert.ToInt32(pageCountProperty.GetValue(document, null));
                LimitedTextBuilder output = new LimitedTextBuilder(MaxTextCharacters);
                for (int pageNumber = 1; pageNumber <= pageCount; pageNumber++)
                {
                    ParameterInfo[] pageParameters = getPageMethod.GetParameters();
                    object[] pageArguments = new object[pageParameters.Length];
                    pageArguments[0] = pageNumber;
                    for (int index = 1; index < pageArguments.Length; index++)
                    {
                        pageArguments[index] = pageParameters[index].HasDefaultValue
                            ? pageParameters[index].DefaultValue
                            : GetDefaultValue(pageParameters[index].ParameterType);
                    }

                    object page = getPageMethod.Invoke(document, pageArguments);
                    string pageText = GetPdfPageText(assembly, page);
                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        output.AppendLine();
                        output.AppendLine("===== 第 " +
                            pageNumber.ToString(CultureInfo.InvariantCulture) + " 页 =====");
                        output.AppendLine(pageText);
                    }

                    if (output.IsFull)
                    {
                        break;
                    }
                }

                return output.ToString();
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                throw new InvalidDataException("PDF 解析失败：" + inner.Message, inner);
            }
            finally
            {
                IDisposable disposable = document as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
        }

        private static string GetPdfPageText(Assembly assembly, object page)
        {
            if (page == null)
            {
                return string.Empty;
            }

            Type extractorType = assembly.GetType(
                "UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor",
                false);
            if (extractorType != null)
            {
                foreach (MethodInfo method in extractorType.GetMethods(
                    BindingFlags.Public | BindingFlags.Static))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (method.Name != "GetText" ||
                        parameters.Length == 0 ||
                        !parameters[0].ParameterType.IsAssignableFrom(page.GetType()))
                    {
                        continue;
                    }

                    object[] arguments = new object[parameters.Length];
                    arguments[0] = page;
                    for (int index = 1; index < arguments.Length; index++)
                    {
                        arguments[index] = parameters[index].HasDefaultValue
                            ? parameters[index].DefaultValue
                            : GetDefaultValue(parameters[index].ParameterType);
                    }

                    try
                    {
                        object result = method.Invoke(null, arguments);
                        if (result != null)
                        {
                            return Convert.ToString(result, CultureInfo.InvariantCulture);
                        }
                    }
                    catch
                    {
                        // Fall back to Page.Text below.
                    }
                }
            }

            PropertyInfo textProperty = page.GetType().GetProperty("Text");
            object text = textProperty == null ? null : textProperty.GetValue(page, null);
            return text == null ? string.Empty : Convert.ToString(text, CultureInfo.InvariantCulture);
        }

        private static string ExtractLegacyDoc(string path)
        {
            Type documentType = FindNpoiType("NPOI.HWPF.HWPFDocument");
            if (documentType == null)
            {
                throw new InvalidOperationException(
                    "缺少旧版 Word 解析组件。请使用包含 NPOI 组件的完整发布目录。");
            }

            object document = null;
            using (FileStream stream = File.OpenRead(path))
            {
                try
                {
                    document = Activator.CreateInstance(documentType, new object[] { stream });
                    MethodInfo getRange = documentType.GetMethod("GetRange", Type.EmptyTypes);
                    object range = getRange == null ? null : getRange.Invoke(document, null);
                    if (range == null)
                    {
                        throw new MissingMemberException("NPOI 无法读取 Word 文本范围。");
                    }

                    PropertyInfo textProperty = range.GetType().GetProperty("Text");
                    object value = textProperty == null
                        ? null
                        : textProperty.GetValue(range, null);
                    return value == null ? range.ToString() : Convert.ToString(value);
                }
                catch (TargetInvocationException exception)
                {
                    Exception inner = exception.InnerException ?? exception;
                    throw new InvalidDataException("旧版 Word 解析失败：" + inner.Message, inner);
                }
                finally
                {
                    IDisposable disposable = document as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                }
            }
        }

        private static string ExtractLegacyXls(string path)
        {
            Type workbookType = FindNpoiType("NPOI.HSSF.UserModel.HSSFWorkbook");
            if (workbookType == null)
            {
                throw new InvalidOperationException(
                    "缺少旧版 Excel 解析组件。请使用包含 NPOI 组件的完整发布目录。");
            }

            object workbook = null;
            using (FileStream stream = File.OpenRead(path))
            {
                try
                {
                    workbook = Activator.CreateInstance(workbookType, new object[] { stream });
                    int sheetCount = GetIntProperty(workbook, "NumberOfSheets");
                    MethodInfo getSheetAt = workbookType.GetMethod(
                        "GetSheetAt",
                        new[] { typeof(int) });
                    if (getSheetAt == null)
                    {
                        throw new MissingMethodException("NPOI GetSheetAt");
                    }

                    LimitedTextBuilder output = new LimitedTextBuilder(MaxTextCharacters);
                    for (int sheetIndex = 0; sheetIndex < sheetCount; sheetIndex++)
                    {
                        object sheet = getSheetAt.Invoke(workbook, new object[] { sheetIndex });
                        if (sheet == null)
                        {
                            continue;
                        }

                        string sheetName = GetStringProperty(sheet, "SheetName");
                        output.AppendLine();
                        output.AppendLine("===== 工作表：" + sheetName + " =====");
                        int lastRow = GetIntProperty(sheet, "LastRowNum");
                        MethodInfo getRow = sheet.GetType().GetMethod(
                            "GetRow",
                            new[] { typeof(int) });
                        if (getRow == null)
                        {
                            continue;
                        }

                        int renderedRows = 0;
                        for (int rowIndex = 0; rowIndex <= lastRow; rowIndex++)
                        {
                            object row = getRow.Invoke(sheet, new object[] { rowIndex });
                            if (row == null)
                            {
                                continue;
                            }

                            int lastCell = GetIntProperty(row, "LastCellNum");
                            if (lastCell <= 0)
                            {
                                continue;
                            }

                            MethodInfo getCell = row.GetType().GetMethod(
                                "GetCell",
                                new[] { typeof(int) });
                            if (getCell == null)
                            {
                                continue;
                            }

                            StringBuilder line = new StringBuilder();
                            for (int cellIndex = 0; cellIndex < lastCell; cellIndex++)
                            {
                                if (cellIndex > 0)
                                {
                                    line.Append('\t');
                                }

                                object cell = getCell.Invoke(row, new object[] { cellIndex });
                                if (cell != null)
                                {
                                    line.Append(EscapeSpreadsheetValue(cell.ToString()));
                                }
                            }

                            output.AppendLine(line.ToString());
                            renderedRows++;
                            if (output.IsFull || renderedRows >= 50000)
                            {
                                output.AppendLine("[工作表内容过长，后续内容已省略]");
                                break;
                            }
                        }

                        if (output.IsFull)
                        {
                            break;
                        }
                    }

                    return output.ToString();
                }
                catch (TargetInvocationException exception)
                {
                    Exception inner = exception.InnerException ?? exception;
                    throw new InvalidDataException("旧版 Excel 解析失败：" + inner.Message, inner);
                }
                finally
                {
                    IDisposable disposable = workbook as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                }
            }
        }

        private static Type FindNpoiType(string fullName)
        {
            LoadNpoiAssemblies();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static void LoadNpoiAssemblies()
        {
            string[] fileNames =
            {
                "NPOI.Core.dll",
                "NPOI.OOXML.dll",
                "NPOI.OpenXml4Net.dll",
                "NPOI.OpenXmlFormats.dll"
            };

            for (int index = 0; index < fileNames.Length; index++)
            {
                try
                {
                    LoadAssemblyFromApplicationDirectory(fileNames[index]);
                }
                catch
                {
                    // Some optional assemblies can be loaded later when their dependency is used.
                }
            }
        }

        private static Assembly LoadAssemblyFromApplicationDirectory(string fileName)
        {
            string simpleName = Path.GetFileNameWithoutExtension(fileName);
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(delegate(Assembly candidate)
                {
                    try
                    {
                        return string.Equals(
                            candidate.GetName().Name,
                            simpleName,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });
            if (loaded != null)
            {
                return loaded;
            }

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return Assembly.LoadFrom(path);
        }

        private static int GetIntProperty(object instance, string propertyName)
        {
            if (instance == null)
            {
                return 0;
            }

            PropertyInfo property = instance.GetType().GetProperty(propertyName);
            object value = property == null ? null : property.GetValue(instance, null);
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static string GetStringProperty(object instance, string propertyName)
        {
            if (instance == null)
            {
                return string.Empty;
            }

            PropertyInfo property = instance.GetType().GetProperty(propertyName);
            object value = property == null ? null : property.GetValue(instance, null);
            return value == null ? string.Empty : Convert.ToString(value);
        }

        private static object GetDefaultValue(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static InputItem ExtractImageFile(
            string path,
            string name,
            long originalBytes)
        {
            byte[] source = File.ReadAllBytes(path);
            using (MemoryStream stream = new MemoryStream(source, false))
            using (Image image = Image.FromStream(stream))
            {
                return CreateImageItem(image, name, originalBytes);
            }
        }

        private static InputItem CreateImageItem(Image source, string name, long originalBytes)
        {
            int width = source.Width;
            int height = source.Height;
            double scale = Math.Min(
                1d,
                Math.Min(MaxImageSide / (double)width, MaxImageSide / (double)height));
            int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(height * scale));

            using (Bitmap bitmap = new Bitmap(
                targetWidth,
                targetHeight,
                PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
                }

                bool usePng = Image.IsAlphaPixelFormat(source.PixelFormat);
                byte[] data = usePng
                    ? SavePng(bitmap)
                    : SaveJpeg(bitmap, 88L);
                string mime = usePng ? "image/png" : "image/jpeg";

                if (data.Length > 8 * 1024 * 1024)
                {
                    data = SaveJpeg(bitmap, 75L);
                    mime = "image/jpeg";
                }

                if (data.Length > 10 * 1024 * 1024)
                {
                    throw new InvalidOperationException(
                        "图片压缩后仍超过 10 MB，请先缩小图片再粘贴。");
                }

                return new InputItem
                {
                    Name = name,
                    Kind = InputKind.Image,
                    BinaryData = data,
                    MimeType = mime,
                    OriginalBytes = originalBytes,
                    Note = targetWidth.ToString(CultureInfo.InvariantCulture) +
                        " × " + targetHeight.ToString(CultureInfo.InvariantCulture)
                };
            }
        }

        private static byte[] SavePng(Image image)
        {
            using (MemoryStream output = new MemoryStream())
            {
                image.Save(output, ImageFormat.Png);
                return output.ToArray();
            }
        }

        private static byte[] SaveJpeg(Image image, long quality)
        {
            using (MemoryStream output = new MemoryStream())
            {
                ImageCodecInfo encoder = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                if (encoder == null)
                {
                    image.Save(output, ImageFormat.Jpeg);
                }
                else
                {
                    using (EncoderParameters parameters = new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(
                            System.Drawing.Imaging.Encoder.Quality,
                            quality);
                        image.Save(output, encoder, parameters);
                    }
                }

                return output.ToArray();
            }
        }

        private sealed class LimitedTextBuilder
        {
            private readonly int maximum;
            private readonly StringBuilder builder;

            public LimitedTextBuilder(int maximumCharacters)
            {
                maximum = maximumCharacters;
                builder = new StringBuilder(Math.Min(maximumCharacters, 32768));
            }

            public bool IsFull
            {
                get { return builder.Length >= maximum; }
            }

            public void AppendLine()
            {
                Append(Environment.NewLine);
            }

            public void AppendLine(string value)
            {
                Append(value);
                Append(Environment.NewLine);
            }

            public void Append(string value)
            {
                if (string.IsNullOrEmpty(value) || IsFull)
                {
                    return;
                }

                int remaining = maximum - builder.Length;
                if (value.Length <= remaining)
                {
                    builder.Append(value);
                }
                else
                {
                    builder.Append(value, 0, remaining);
                }
            }

            public override string ToString()
            {
                if (IsFull)
                {
                    return builder.ToString() +
                        "\r\n\r\n[内容过长，已在 2,000,000 字符处截断]";
                }

                return builder.ToString();
            }
        }
    }
}
