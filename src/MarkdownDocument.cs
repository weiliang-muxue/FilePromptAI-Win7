using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FilePromptWin7
{
    internal enum MarkdownBlockKind
    {
        Paragraph,
        Heading,
        CodeBlock,
        List,
        Quote,
        Table,
        HorizontalRule
    }

    internal sealed class MarkdownTable
    {
        public IList<string> Headers { get; private set; }
        public IList<IList<string>> Rows { get; private set; }

        public MarkdownTable()
        {
            Headers = new List<string>();
            Rows = new List<IList<string>>();
        }

        public MarkdownTable(IList<string> headers)
            : this()
        {
            if (headers != null)
            {
                foreach (string header in headers)
                {
                    Headers.Add(header ?? string.Empty);
                }
            }
        }

        public IList<IList<string>> GetAllRows()
        {
            List<IList<string>> result = new List<IList<string>>();
            List<string> header = new List<string>();
            foreach (string value in Headers)
            {
                header.Add(value ?? string.Empty);
            }

            if (header.Count > 0)
            {
                result.Add(header);
            }

            foreach (IList<string> row in Rows)
            {
                List<string> copy = new List<string>();
                if (row != null)
                {
                    foreach (string value in row)
                    {
                        copy.Add(value ?? string.Empty);
                    }
                }

                result.Add(copy);
            }

            return result;
        }
    }

    internal sealed class MarkdownBlock
    {
        public MarkdownBlockKind Kind { get; set; }
        public string Text { get; set; }
        public int Level { get; set; }
        public bool Ordered { get; set; }
        public string Language { get; set; }
        public IList<string> Items { get; private set; }
        public MarkdownTable Table { get; set; }

        public MarkdownBlock()
        {
            Text = string.Empty;
            Language = string.Empty;
            Items = new List<string>();
        }

        public static MarkdownBlock Paragraph(string text)
        {
            return new MarkdownBlock
            {
                Kind = MarkdownBlockKind.Paragraph,
                Text = text ?? string.Empty
            };
        }

        public static MarkdownBlock Heading(int level, string text)
        {
            return new MarkdownBlock
            {
                Kind = MarkdownBlockKind.Heading,
                Level = level,
                Text = text ?? string.Empty
            };
        }

        public static MarkdownBlock Code(string language, string text)
        {
            return new MarkdownBlock
            {
                Kind = MarkdownBlockKind.CodeBlock,
                Language = language ?? string.Empty,
                Text = text ?? string.Empty
            };
        }
    }

    internal sealed class MarkdownDocument
    {
        private static readonly Regex LinkRegex = new Regex(
            @"(?<!\\)!\[([^\]]*)\]\([^\)]*\)|(?<!\\)\[([^\]]+)\]\([^\)]*\)",
            RegexOptions.Compiled);
        private static readonly Regex HtmlLineBreakRegex = new Regex(
            @"<br\s*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex InlineCodeRegex = new Regex(
            @"(?<!\\)`([^`]+)(?<!\\)`",
            RegexOptions.Compiled);
        private static readonly Regex BoldAsteriskRegex = new Regex(
            @"(?<!\\)\*\*(?=\S)(.+?)(?<=\S)(?<!\\)\*\*",
            RegexOptions.Compiled);
        private static readonly Regex BoldUnderscoreRegex = new Regex(
            @"(?<![\w\\])__(?=\S)(.+?)(?<=\S)(?<!\\)__(?!\w)",
            RegexOptions.Compiled);
        private static readonly Regex ItalicAsteriskRegex = new Regex(
            @"(?<![\*\\])\*(?=\S)(.+?)(?<=\S)(?<!\\)\*(?!\*)",
            RegexOptions.Compiled);
        private static readonly Regex ItalicUnderscoreRegex = new Regex(
            @"(?<![\w_\\])_(?=\S)(.+?)(?<=\S)(?<!\\)_(?![\w_])",
            RegexOptions.Compiled);
        private static readonly Regex StrikeRegex = new Regex(
            @"(?<!\\)~~(?=\S)(.+?)(?<=\S)(?<!\\)~~",
            RegexOptions.Compiled);

        public IList<MarkdownBlock> Blocks { get; private set; }
        public IList<MarkdownTable> Tables { get; private set; }

        private MarkdownDocument()
        {
            Blocks = new List<MarkdownBlock>();
            Tables = new List<MarkdownTable>();
        }

        public static MarkdownDocument Parse(string markdown)
        {
            MarkdownDocument document = new MarkdownDocument();
            string normalized = (markdown ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");
            string[] lines = normalized.Split(
                new[] { '\n' },
                StringSplitOptions.None);
            int index = 0;

            while (index < lines.Length)
            {
                string line = lines[index] ?? string.Empty;
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    index++;
                    continue;
                }

                string fence;
                string language;
                if (TryReadFenceStart(line, out fence, out language))
                {
                    index++;
                    StringBuilder code = new StringBuilder();
                    while (index < lines.Length)
                    {
                        string codeLine = lines[index] ?? string.Empty;
                        if (IsFenceEnd(codeLine, fence))
                        {
                            index++;
                            break;
                        }

                        if (code.Length > 0)
                        {
                            code.Append('\n');
                        }

                        code.Append(codeLine);
                        index++;
                    }

                    document.Blocks.Add(
                        MarkdownBlock.Code(language, code.ToString()));
                    continue;
                }

                int headingLevel;
                string headingText;
                if (TryReadHeading(line, out headingLevel, out headingText))
                {
                    document.Blocks.Add(
                        MarkdownBlock.Heading(
                            headingLevel,
                            StripInlineMarkdown(headingText)));
                    index++;
                    continue;
                }

                if (index + 1 < lines.Length &&
                    IsTableDelimiter(lines[index + 1]) &&
                    HasMatchingTableColumns(line, lines[index + 1]) &&
                    line.IndexOf('|') >= 0)
                {
                    MarkdownTable table = new MarkdownTable(
                        SplitTableRow(line));
                    index += 2;
                    while (index < lines.Length)
                    {
                        string rowLine = lines[index] ?? string.Empty;
                        if (rowLine.Trim().Length == 0 ||
                            rowLine.IndexOf('|') < 0)
                        {
                            break;
                        }

                        IList<string> row = SplitTableRow(rowLine);
                        if (row.Count == 0)
                        {
                            break;
                        }

                        table.Rows.Add(row);
                        index++;
                    }

                    NormalizeTable(table);
                    document.Tables.Add(table);
                    MarkdownBlock tableBlock = new MarkdownBlock();
                    tableBlock.Kind = MarkdownBlockKind.Table;
                    tableBlock.Table = table;
                    document.Blocks.Add(tableBlock);
                    continue;
                }

                if (IsHorizontalRule(trimmed))
                {
                    MarkdownBlock horizontal = new MarkdownBlock();
                    horizontal.Kind = MarkdownBlockKind.HorizontalRule;
                    document.Blocks.Add(horizontal);
                    index++;
                    continue;
                }

                bool ordered;
                string listItem;
                if (TryReadListItem(line, out ordered, out listItem))
                {
                    MarkdownBlock list = new MarkdownBlock();
                    list.Kind = MarkdownBlockKind.List;
                    list.Ordered = ordered;
                    while (index < lines.Length)
                    {
                        string listLine = lines[index] ?? string.Empty;
                        bool currentOrdered;
                        string currentItem;
                        if (!TryReadListItem(
                            listLine,
                            out currentOrdered,
                            out currentItem) ||
                            currentOrdered != ordered)
                        {
                            break;
                        }

                        list.Items.Add(StripInlineMarkdown(currentItem));
                        index++;
                    }

                    document.Blocks.Add(list);
                    continue;
                }

                if (IsQuoteLine(line))
                {
                    StringBuilder quote = new StringBuilder();
                    while (index < lines.Length && IsQuoteLine(lines[index]))
                    {
                        if (quote.Length > 0)
                        {
                            quote.Append('\n');
                        }

                        quote.Append(
                            StripInlineMarkdown(
                                RemoveQuotePrefix(lines[index])));
                        index++;
                    }

                    MarkdownBlock quoteBlock = new MarkdownBlock();
                    quoteBlock.Kind = MarkdownBlockKind.Quote;
                    quoteBlock.Text = quote.ToString();
                    document.Blocks.Add(quoteBlock);
                    continue;
                }

                StringBuilder paragraph = new StringBuilder();
                while (index < lines.Length)
                {
                    string paragraphLine = lines[index] ?? string.Empty;
                    if (paragraphLine.Trim().Length == 0)
                    {
                        break;
                    }

                    if (paragraph.Length > 0 &&
                        IsBlockStart(lines, index))
                    {
                        break;
                    }

                    if (paragraph.Length > 0)
                    {
                        paragraph.Append('\n');
                    }

                    paragraph.Append(StripInlineMarkdown(paragraphLine));
                    index++;
                }

                if (paragraph.Length > 0)
                {
                    document.Blocks.Add(
                        MarkdownBlock.Paragraph(paragraph.ToString()));
                }
                else
                {
                    index++;
                }
            }

            return document;
        }

        public static string StripInlineMarkdown(string value)
        {
            string text = value ?? string.Empty;
            List<string> codeSpans = new List<string>();
            string codeTokenPrefix = "\uE000FilePromptCode";
            const string codeTokenSuffix = "\uE001";
            while (text.IndexOf(
                codeTokenPrefix,
                StringComparison.Ordinal) >= 0)
            {
                codeTokenPrefix += "_";
            }

            text = InlineCodeRegex.Replace(
                text,
                delegate(Match match)
                {
                    int codeIndex = codeSpans.Count;
                    codeSpans.Add(match.Groups[1].Value);
                    return codeTokenPrefix + codeIndex + codeTokenSuffix;
                });
            text = LinkRegex.Replace(
                text,
                delegate(Match match)
                {
                    if (!string.IsNullOrEmpty(match.Groups[1].Value))
                    {
                        return match.Groups[1].Value;
                    }

                    return match.Groups[2].Value;
                });

            text = HtmlLineBreakRegex.Replace(text, "\n");
            text = BoldAsteriskRegex.Replace(text, "$1");
            text = BoldUnderscoreRegex.Replace(text, "$1");
            text = ItalicAsteriskRegex.Replace(text, "$1");
            text = ItalicUnderscoreRegex.Replace(text, "$1");
            text = StrikeRegex.Replace(text, "$1");
            text = text.Replace("\\`", "`");
            text = text.Replace("\\*", "*");
            text = text.Replace("\\_", "_");
            text = text.Replace("\\#", "#");
            text = text.Replace("\\[", "[");
            text = text.Replace("\\]", "]");
            text = text.Replace("\\|", "|");
            text = text.Replace("\\>", ">");
            text = text.Replace("\\~", "~");
            for (int codeIndex = 0;
                codeIndex < codeSpans.Count;
                codeIndex++)
            {
                text = text.Replace(
                    codeTokenPrefix + codeIndex + codeTokenSuffix,
                    codeSpans[codeIndex]);
            }

            return text;
        }

        private static bool IsBlockStart(string[] lines, int index)
        {
            string line = lines[index] ?? string.Empty;
            string trimmed = line.Trim();
            string fence;
            string language;
            int level;
            string heading;
            bool ordered;
            string listItem;
            if (TryReadFenceStart(line, out fence, out language) ||
                TryReadHeading(line, out level, out heading) ||
                TryReadListItem(line, out ordered, out listItem) ||
                IsQuoteLine(line) ||
                IsHorizontalRule(trimmed))
            {
                return true;
            }

            return index + 1 < lines.Length &&
                line.IndexOf('|') >= 0 &&
                IsTableDelimiter(lines[index + 1]) &&
                HasMatchingTableColumns(line, lines[index + 1]);
        }

        private static bool TryReadFenceStart(
            string line,
            out string fence,
            out string language)
        {
            string trimmed = (line ?? string.Empty).TrimStart();
            fence = string.Empty;
            language = string.Empty;
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                fence = "```";
            }
            else if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = "~~~";
            }
            else
            {
                return false;
            }

            language = trimmed.Substring(fence.Length).Trim();
            return true;
        }

        private static bool IsFenceEnd(string line, string fence)
        {
            return (line ?? string.Empty).TrimStart().StartsWith(
                fence,
                StringComparison.Ordinal);
        }

        private static bool TryReadHeading(
            string line,
            out int level,
            out string text)
        {
            string value = (line ?? string.Empty).TrimStart();
            level = 0;
            text = string.Empty;
            while (level < value.Length &&
                level < 6 &&
                value[level] == '#')
            {
                level++;
            }

            if (level == 0 ||
                level >= value.Length ||
                !char.IsWhiteSpace(value[level]))
            {
                level = 0;
                return false;
            }

            text = value.Substring(level).Trim();
            return true;
        }

        private static bool TryReadListItem(
            string line,
            out bool ordered,
            out string item)
        {
            string value = (line ?? string.Empty).TrimStart();
            ordered = false;
            item = string.Empty;
            if (value.Length < 2)
            {
                return false;
            }

            char marker = value[0];
            if (marker == '-' || marker == '*' || marker == '+')
            {
                if (value.Length > 1 && char.IsWhiteSpace(value[1]))
                {
                    item = value.Substring(2).Trim();
                    return true;
                }

                return false;
            }

            int position = 0;
            while (position < value.Length && char.IsDigit(value[position]))
            {
                position++;
            }

            if (position == 0 ||
                position + 1 >= value.Length ||
                value[position] != '.' ||
                !char.IsWhiteSpace(value[position + 1]))
            {
                return false;
            }

            ordered = true;
            item = value.Substring(position + 2).Trim();
            return true;
        }

        private static bool IsQuoteLine(string line)
        {
            string value = (line ?? string.Empty).TrimStart();
            return value.StartsWith(">", StringComparison.Ordinal) &&
                (value.Length == 1 || char.IsWhiteSpace(value[1]));
        }

        private static string RemoveQuotePrefix(string line)
        {
            string value = (line ?? string.Empty).TrimStart();
            if (value.Length <= 1)
            {
                return string.Empty;
            }

            return value.Substring(1).TrimStart();
        }

        private static bool IsHorizontalRule(string value)
        {
            string text = (value ?? string.Empty).Replace(" ", string.Empty)
                .Replace("\t", string.Empty);
            if (text.Length < 3)
            {
                return false;
            }

            char marker = text[0];
            if (marker != '-' && marker != '*' && marker != '_')
            {
                return false;
            }

            for (int i = 1; i < text.Length; i++)
            {
                if (text[i] != marker)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsTableDelimiter(string line)
        {
            IList<string> cells = SplitTableRow(line);
            if (cells.Count == 0)
            {
                return false;
            }

            int validCells = 0;
            foreach (string cell in cells)
            {
                string value = (cell ?? string.Empty).Trim();
                if (value.StartsWith(":", StringComparison.Ordinal))
                {
                    value = value.Substring(1);
                }

                if (value.EndsWith(":", StringComparison.Ordinal))
                {
                    value = value.Substring(0, value.Length - 1);
                }

                if (value.Length < 3)
                {
                    return false;
                }

                for (int i = 0; i < value.Length; i++)
                {
                    if (value[i] != '-')
                    {
                        return false;
                    }
                }

                validCells++;
            }

            return validCells > 0;
        }

        private static bool HasMatchingTableColumns(
            string headerLine,
            string delimiterLine)
        {
            IList<string> headers = SplitTableRow(headerLine);
            IList<string> delimiters = SplitTableRow(delimiterLine);
            return headers.Count > 0 && headers.Count == delimiters.Count;
        }

        private static IList<string> SplitTableRow(string line)
        {
            string value = (line ?? string.Empty).Trim();
            if (value.StartsWith("|", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (value.EndsWith("|", StringComparison.Ordinal) &&
                !IsEscaped(value, value.Length - 1))
            {
                value = value.Substring(0, value.Length - 1);
            }

            List<string> cells = new List<string>();
            StringBuilder cell = new StringBuilder();
            int index = 0;
            while (index < value.Length)
            {
                char current = value[index];
                if (current == '\\')
                {
                    int slashStart = index;
                    while (index < value.Length && value[index] == '\\')
                    {
                        index++;
                    }

                    int slashCount = index - slashStart;
                    bool escapedPipe = index < value.Length &&
                        value[index] == '|' &&
                        slashCount % 2 == 1;
                    int preservedSlashes = escapedPipe
                        ? slashCount - 1
                        : slashCount;
                    for (int slashIndex = 0;
                        slashIndex < preservedSlashes;
                        slashIndex++)
                    {
                        cell.Append('\\');
                    }

                    if (escapedPipe)
                    {
                        cell.Append('|');
                        index++;
                    }

                    continue;
                }

                if (current == '|')
                {
                    cells.Add(StripInlineMarkdown(cell.ToString().Trim()));
                    cell.Length = 0;
                }
                else
                {
                    cell.Append(current);
                }

                index++;
            }

            cells.Add(StripInlineMarkdown(cell.ToString().Trim()));
            return cells;
        }

        private static bool IsEscaped(string value, int characterIndex)
        {
            int slashCount = 0;
            for (int index = characterIndex - 1;
                index >= 0 && value[index] == '\\';
                index--)
            {
                slashCount++;
            }

            return slashCount % 2 == 1;
        }

        private static void NormalizeTable(MarkdownTable table)
        {
            if (table == null)
            {
                return;
            }

            int columnCount = table.Headers.Count;
            foreach (IList<string> row in table.Rows)
            {
                if (row != null && row.Count > columnCount)
                {
                    columnCount = row.Count;
                }
            }

            while (table.Headers.Count < columnCount)
            {
                table.Headers.Add(string.Empty);
            }

            foreach (IList<string> row in table.Rows)
            {
                if (row == null)
                {
                    continue;
                }

                while (row.Count < columnCount)
                {
                    row.Add(string.Empty);
                }
            }
        }
    }
}
