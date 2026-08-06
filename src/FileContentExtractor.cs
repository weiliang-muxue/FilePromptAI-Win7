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
            LoadAssembliesMatching("NPOI*.dll");
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

        private static void LoadAssembliesMatching(string pattern)
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, pattern);
            }
            catch
            {
                return;
            }

            foreach (string file in files)
            {
                try
                {
                    Assembly.LoadFrom(file);
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
