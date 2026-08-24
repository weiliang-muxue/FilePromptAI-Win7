using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace FilePromptAIWin7
{
    internal static class MarkdownRichTextRenderer
    {
        private enum RenderBlockKind
        {
            Paragraph,
            Heading,
            Code,
            UnorderedList,
            OrderedList,
            Quote,
            Table,
            HorizontalRule
        }

        private sealed class RenderBlock
        {
            public RenderBlockKind Kind { get; set; }
            public string Text { get; set; }
            public int Level { get; set; }
            public IList<string> Items { get; private set; }
            public IList<IList<string>> Rows { get; private set; }

            public RenderBlock()
            {
                Text = string.Empty;
                Items = new List<string>();
                Rows = new List<IList<string>>();
            }
        }

        private sealed class RenderPalette
        {
            public Color Text { get; private set; }
            public Color MutedText { get; private set; }
            public Color Background { get; private set; }
            public Color CodeBackground { get; private set; }
            public Color QuoteBackground { get; private set; }
            public Color TableHeaderBackground { get; private set; }

            public static RenderPalette FromControl(RichTextBox target)
            {
                Color background = target.BackColor;
                Color text = target.ForeColor;
                if (ContrastRatio(text, background) < 4.5)
                {
                    text = ContrastRatio(Color.Black, background) >= 4.5
                        ? Color.Black
                        : Color.White;
                }

                bool dark = RelativeLuminance(background) < 0.35;
                Color surfaceTarget = dark ? Color.White : Color.Black;
                Color muted = Blend(text, background, 0.35);
                if (ContrastRatio(muted, background) < 4.5)
                {
                    muted = text;
                }

                return new RenderPalette
                {
                    Text = text,
                    MutedText = muted,
                    Background = background,
                    CodeBackground = Blend(background, surfaceTarget, dark ? 0.10 : 0.055),
                    QuoteBackground = Blend(background, surfaceTarget, dark ? 0.075 : 0.035),
                    TableHeaderBackground = Blend(background, surfaceTarget, dark ? 0.13 : 0.075)
                };
            }
        }

        private sealed class RenderContext : IDisposable
        {
            private readonly Dictionary<string, Font> fonts;
            private readonly string bodyFamily;

            public RichTextBox Target { get; private set; }
            public RenderPalette Palette { get; private set; }
            public float BodySize { get; private set; }

            public RenderContext(RichTextBox target)
            {
                Target = target;
                Palette = RenderPalette.FromControl(target);
                BodySize = Math.Max(8.0F, target.Font.SizeInPoints);
                bodyFamily = target.Font.FontFamily.Name;
                fonts = new Dictionary<string, Font>(StringComparer.Ordinal);
            }

            public Font GetBodyFont(float size, FontStyle style)
            {
                return GetFont(bodyFamily, size, style);
            }

            public Font GetCodeFont(float size, FontStyle style)
            {
                return GetFont("Consolas", size, style);
            }

            public void Dispose()
            {
                foreach (Font font in fonts.Values)
                {
                    font.Dispose();
                }

                fonts.Clear();
            }

            private Font GetFont(string family, float size, FontStyle style)
            {
                float safeSize = Math.Max(8.0F, Math.Min(22.0F, size));
                string key = family + "|" + safeSize.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "|" + ((int)style).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Font result;
                if (fonts.TryGetValue(key, out result))
                {
                    return result;
                }

                try
                {
                    result = new Font(family, safeSize, style, GraphicsUnit.Point);
                }
                catch (ArgumentException)
                {
                    result = new Font(TargetFontFamily(), safeSize, style, GraphicsUnit.Point);
                }

                fonts.Add(key, result);
                return result;
            }

            private FontFamily TargetFontFamily()
            {
                return Target.Font.FontFamily;
            }
        }

        public static void Render(RichTextBox target, string markdown)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            target.SuspendLayout();
            try
            {
                target.Clear();
                AppendCore(target, markdown);
                target.SelectionStart = 0;
                target.SelectionLength = 0;
            }
            finally
            {
                target.ResumeLayout(true);
            }
        }

        public static void Append(RichTextBox target, string markdown)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            target.SuspendLayout();
            try
            {
                target.SelectionStart = target.TextLength;
                target.SelectionLength = 0;
                AppendCore(target, markdown);
            }
            finally
            {
                target.ResumeLayout(true);
            }
        }

        private static void AppendCore(RichTextBox target, string markdown)
        {
            IList<RenderBlock> blocks = Parse(markdown);
            using (RenderContext context = new RenderContext(target))
            {
                for (int index = 0; index < blocks.Count; index++)
                {
                    if (index > 0)
                    {
                        context.Target.AppendText("\r\n");
                        ResetParagraph(context);
                        ApplyRunFormat(
                            context,
                            context.BodySize,
                            FontStyle.Regular,
                            false,
                            context.Palette.Text,
                            context.Palette.Background);
                        context.Target.AppendText("\r\n");
                    }

                    AppendBlock(context, blocks[index]);
                }

                if (blocks.Count > 0)
                {
                    context.Target.AppendText("\r\n");
                    ResetParagraph(context);
                    ApplyRunFormat(
                        context,
                        context.BodySize,
                        FontStyle.Regular,
                        false,
                        context.Palette.Text,
                        context.Palette.Background);
                }
            }
        }

        private static void AppendBlock(RenderContext context, RenderBlock block)
        {
            switch (block.Kind)
            {
                case RenderBlockKind.Heading:
                    AppendHeading(context, block);
                    break;
                case RenderBlockKind.Code:
                    AppendCodeBlock(context, block.Text);
                    break;
                case RenderBlockKind.UnorderedList:
                    AppendList(context, block.Items, false);
                    break;
                case RenderBlockKind.OrderedList:
                    AppendList(context, block.Items, true);
                    break;
                case RenderBlockKind.Quote:
                    AppendQuote(context, block.Text);
                    break;
                case RenderBlockKind.Table:
                    AppendTable(context, block.Rows);
                    break;
                case RenderBlockKind.HorizontalRule:
                    AppendHorizontalRule(context);
                    break;
                default:
                    ResetParagraph(context);
                    AppendInline(
                        context,
                        block.Text,
                        context.BodySize,
                        FontStyle.Regular,
                        context.Palette.Text,
                        context.Palette.Background);
                    break;
            }
        }

        private static void AppendHeading(RenderContext context, RenderBlock block)
        {
            float[] increments = { 6.0F, 4.0F, 2.5F, 1.5F, 0.75F, 0.25F };
            int level = Math.Max(1, Math.Min(6, block.Level));
            float size = context.BodySize + increments[level - 1];
            ResetParagraph(context);
            AppendInline(
                context,
                block.Text,
                size,
                FontStyle.Bold,
                context.Palette.Text,
                context.Palette.Background);
        }

        private static void AppendCodeBlock(RenderContext context, string code)
        {
            ResetParagraph(context);
            context.Target.SelectionIndent = 12;
            context.Target.SelectionRightIndent = 12;
            AppendRun(
                context,
                code,
                context.BodySize,
                FontStyle.Regular,
                true,
                context.Palette.Text,
                context.Palette.CodeBackground);
        }

        private static void AppendList(
            RenderContext context,
            IList<string> items,
            bool ordered)
        {
            for (int index = 0; index < items.Count; index++)
            {
                if (index > 0)
                {
                    AppendRun(
                        context,
                        "\r\n",
                        context.BodySize,
                        FontStyle.Regular,
                        false,
                        context.Palette.Text,
                        context.Palette.Background);
                }

                ResetParagraph(context);
                context.Target.SelectionIndent = ordered ? 28 : 24;
                context.Target.SelectionHangingIndent = ordered ? 28 : 12;
                if (ordered)
                {
                    AppendRun(
                        context,
                        (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". ",
                        context.BodySize,
                        FontStyle.Bold,
                        false,
                        context.Palette.MutedText,
                        context.Palette.Background);
                }
                else
                {
                    context.Target.SelectionBullet = true;
                }

                AppendInline(
                    context,
                    items[index],
                    context.BodySize,
                    FontStyle.Regular,
                    context.Palette.Text,
                    context.Palette.Background);
            }

        }

        private static void AppendQuote(RenderContext context, string quote)
        {
            ResetParagraph(context);
            context.Target.SelectionIndent = 18;
            context.Target.SelectionRightIndent = 10;
            AppendInline(
                context,
                quote,
                context.BodySize,
                FontStyle.Italic,
                context.Palette.MutedText,
                context.Palette.QuoteBackground);
        }

        private static void AppendTable(
            RenderContext context,
            IList<IList<string>> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            int[] tabStops = BuildTableTabStops(context, rows);
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                if (rowIndex > 0)
                {
                    AppendRun(
                        context,
                        "\r\n",
                        context.BodySize,
                        FontStyle.Regular,
                        false,
                        context.Palette.Text,
                        context.Palette.Background);
                }

                ResetParagraph(context);
                context.Target.SelectionIndent = 8;
                context.Target.SelectionRightIndent = 8;
                context.Target.SelectionTabs = tabStops;
                IList<string> row = rows[rowIndex];
                int columnCount = rows[0].Count;
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    if (columnIndex > 0)
                    {
                        AppendRun(
                            context,
                            "\t",
                            context.BodySize,
                            FontStyle.Regular,
                            false,
                            context.Palette.Text,
                            rowIndex == 0
                                ? context.Palette.TableHeaderBackground
                                : context.Palette.Background);
                    }

                    string cell = columnIndex < row.Count
                        ? FlattenTableCell(row[columnIndex])
                        : string.Empty;
                    AppendInline(
                        context,
                        cell,
                        context.BodySize,
                        rowIndex == 0 ? FontStyle.Bold : FontStyle.Regular,
                        context.Palette.Text,
                        rowIndex == 0
                            ? context.Palette.TableHeaderBackground
                            : context.Palette.Background);
                }
            }
        }

        private static void AppendHorizontalRule(RenderContext context)
        {
            ResetParagraph(context);
            AppendRun(
                context,
                new string('\u2500', 36),
                Math.Max(8.0F, context.BodySize - 1.0F),
                FontStyle.Regular,
                false,
                context.Palette.MutedText,
                context.Palette.Background);
        }

        private static void AppendInline(
            RenderContext context,
            string text,
            float size,
            FontStyle style,
            Color color,
            Color background)
        {
            AppendInlineRange(
                context,
                text ?? string.Empty,
                0,
                (text ?? string.Empty).Length,
                size,
                style,
                color,
                background);
        }

        private static void AppendInlineRange(
            RenderContext context,
            string text,
            int start,
            int length,
            float size,
            FontStyle style,
            Color color,
            Color background)
        {
            int end = start + length;
            int plainStart = start;
            int index = start;
            while (index < end)
            {
                int delimiterLength;
                int closing;
                if (text[index] == '`')
                {
                    delimiterLength = CountRun(text, index, end, '`');
                    closing = FindDelimiter(
                        text,
                        index + delimiterLength,
                        end,
                        new string('`', delimiterLength));
                    if (closing >= 0)
                    {
                        AppendPlainRange(context, text, plainStart, index - plainStart, size, style, color, background);
                        AppendRun(
                            context,
                            text.Substring(index + delimiterLength, closing - index - delimiterLength),
                            size,
                            style,
                            true,
                            color,
                            context.Palette.CodeBackground);
                        index = closing + delimiterLength;
                        plainStart = index;
                        continue;
                    }
                }

                string boldMarker = null;
                if (StartsWith(text, index, end, "**") &&
                    CanOpenBold(text, index, end, "**"))
                {
                    boldMarker = "**";
                }
                else if (StartsWith(text, index, end, "__") &&
                    CanOpenBold(text, index, end, "__"))
                {
                    boldMarker = "__";
                }

                if (boldMarker != null)
                {
                    closing = FindClosingBold(
                        text,
                        index + boldMarker.Length,
                        end,
                        boldMarker);
                    if (closing >= 0)
                    {
                        AppendPlainRange(context, text, plainStart, index - plainStart, size, style, color, background);
                        AppendInlineRange(
                            context,
                            text,
                            index + boldMarker.Length,
                            closing - index - boldMarker.Length,
                            size,
                            style | FontStyle.Bold,
                            color,
                            background);
                        index = closing + boldMarker.Length;
                        plainStart = index;
                        continue;
                    }
                }

                if (text[index] == '\\' && index + 1 < end &&
                    IsEscapableMarkdownCharacter(text[index + 1]))
                {
                    AppendPlainRange(context, text, plainStart, index - plainStart, size, style, color, background);
                    AppendRun(
                        context,
                        text[index + 1].ToString(),
                        size,
                        style,
                        false,
                        color,
                        background);
                    index += 2;
                    plainStart = index;
                    continue;
                }

                int breakLength = HtmlBreakLength(text, index, end);
                if (breakLength > 0)
                {
                    AppendPlainRange(context, text, plainStart, index - plainStart, size, style, color, background);
                    AppendRun(
                        context,
                        "\r\n",
                        size,
                        style,
                        false,
                        color,
                        background);
                    index += breakLength;
                    plainStart = index;
                    continue;
                }

                index++;
            }

            AppendPlainRange(context, text, plainStart, end - plainStart, size, style, color, background);
        }

        private static void AppendPlainRange(
            RenderContext context,
            string text,
            int start,
            int length,
            float size,
            FontStyle style,
            Color color,
            Color background)
        {
            if (length <= 0)
            {
                return;
            }

            AppendRun(
                context,
                text.Substring(start, length),
                size,
                style,
                false,
                color,
                background);
        }

        private static void AppendRun(
            RenderContext context,
            string text,
            float size,
            FontStyle style,
            bool code,
            Color color,
            Color background)
        {
            string safeText = SanitizeText(text);
            if (safeText.Length == 0)
            {
                return;
            }

            int start = context.Target.TextLength;
            ApplyRunFormat(context, size, style, code, color, background);
            context.Target.AppendText(safeText);
            int end = context.Target.TextLength;
            context.Target.Select(start, end - start);
            ApplyRunFormat(context, size, style, code, color, background);
            context.Target.Select(end, 0);
        }

        private static void ApplyRunFormat(
            RenderContext context,
            float size,
            FontStyle style,
            bool code,
            Color color,
            Color background)
        {
            context.Target.SelectionFont = code
                ? context.GetCodeFont(size, style)
                : context.GetBodyFont(size, style);
            context.Target.SelectionColor = color;
            context.Target.SelectionBackColor = background;
        }

        private static void ResetParagraph(RenderContext context)
        {
            context.Target.SelectionBullet = false;
            context.Target.SelectionIndent = 0;
            context.Target.SelectionHangingIndent = 0;
            context.Target.SelectionRightIndent = 0;
            context.Target.SelectionTabs = new int[0];
        }

        private static IList<RenderBlock> Parse(string markdown)
        {
            string normalized = (markdown ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");
            string[] lines = normalized.Split(new[] { '\n' }, StringSplitOptions.None);
            List<RenderBlock> blocks = new List<RenderBlock>();
            int index = 0;
            while (index < lines.Length)
            {
                if (string.IsNullOrWhiteSpace(lines[index]))
                {
                    index++;
                    continue;
                }

                string fence;
                if (TryReadFence(lines[index], out fence))
                {
                    index++;
                    StringBuilder code = new StringBuilder();
                    while (index < lines.Length && !IsFenceEnd(lines[index], fence))
                    {
                        if (code.Length > 0)
                        {
                            code.Append('\n');
                        }

                        code.Append(lines[index]);
                        index++;
                    }

                    if (index < lines.Length)
                    {
                        index++;
                    }

                    blocks.Add(new RenderBlock
                    {
                        Kind = RenderBlockKind.Code,
                        Text = code.ToString()
                    });
                    continue;
                }

                int headingLevel;
                string headingText;
                if (TryReadHeading(lines[index], out headingLevel, out headingText))
                {
                    blocks.Add(new RenderBlock
                    {
                        Kind = RenderBlockKind.Heading,
                        Level = headingLevel,
                        Text = headingText
                    });
                    index++;
                    continue;
                }

                IList<IList<string>> tableRows;
                int tableEnd;
                if (TryReadTable(lines, index, out tableRows, out tableEnd))
                {
                    RenderBlock table = new RenderBlock
                    {
                        Kind = RenderBlockKind.Table
                    };
                    foreach (IList<string> row in tableRows)
                    {
                        table.Rows.Add(row);
                    }

                    blocks.Add(table);
                    index = tableEnd;
                    continue;
                }

                bool ordered;
                string item;
                if (TryReadListItem(lines[index], out ordered, out item))
                {
                    RenderBlock list = new RenderBlock
                    {
                        Kind = ordered
                            ? RenderBlockKind.OrderedList
                            : RenderBlockKind.UnorderedList
                    };
                    while (index < lines.Length)
                    {
                        bool currentOrdered;
                        string currentItem;
                        if (!TryReadListItem(lines[index], out currentOrdered, out currentItem) ||
                            currentOrdered != ordered)
                        {
                            break;
                        }

                        list.Items.Add(currentItem);
                        index++;
                    }

                    blocks.Add(list);
                    continue;
                }

                if (IsQuoteLine(lines[index]))
                {
                    StringBuilder quote = new StringBuilder();
                    while (index < lines.Length && IsQuoteLine(lines[index]))
                    {
                        if (quote.Length > 0)
                        {
                            quote.Append('\n');
                        }

                        quote.Append(RemoveQuotePrefix(lines[index]));
                        index++;
                    }

                    blocks.Add(new RenderBlock
                    {
                        Kind = RenderBlockKind.Quote,
                        Text = quote.ToString()
                    });
                    continue;
                }

                if (IsHorizontalRule(lines[index]))
                {
                    blocks.Add(new RenderBlock
                    {
                        Kind = RenderBlockKind.HorizontalRule
                    });
                    index++;
                    continue;
                }

                StringBuilder paragraph = new StringBuilder();
                while (index < lines.Length &&
                    !string.IsNullOrWhiteSpace(lines[index]) &&
                    (paragraph.Length == 0 || !IsBlockStart(lines, index)))
                {
                    if (paragraph.Length > 0)
                    {
                        paragraph.Append('\n');
                    }

                    paragraph.Append(lines[index].Trim());
                    index++;
                }

                if (paragraph.Length > 0)
                {
                    blocks.Add(new RenderBlock
                    {
                        Kind = RenderBlockKind.Paragraph,
                        Text = paragraph.ToString()
                    });
                }
                else
                {
                    index++;
                }
            }

            return blocks;
        }

        private static bool IsBlockStart(string[] lines, int index)
        {
            string fence;
            int level;
            string heading;
            bool ordered;
            string item;
            IList<IList<string>> rows;
            int end;
            return TryReadFence(lines[index], out fence) ||
                TryReadHeading(lines[index], out level, out heading) ||
                TryReadListItem(lines[index], out ordered, out item) ||
                IsQuoteLine(lines[index]) ||
                IsHorizontalRule(lines[index]) ||
                TryReadTable(lines, index, out rows, out end);
        }

        private static bool TryReadFence(string line, out string fence)
        {
            string value = (line ?? string.Empty).TrimStart();
            fence = string.Empty;
            if (value.StartsWith("```", StringComparison.Ordinal))
            {
                fence = "```";
                return true;
            }

            if (value.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = "~~~";
                return true;
            }

            return false;
        }

        private static bool IsFenceEnd(string line, string fence)
        {
            return (line ?? string.Empty).TrimStart().StartsWith(
                fence,
                StringComparison.Ordinal);
        }

        private static bool TryReadHeading(string line, out int level, out string text)
        {
            string value = (line ?? string.Empty).TrimStart();
            level = 0;
            while (level < value.Length && level < 6 && value[level] == '#')
            {
                level++;
            }

            text = string.Empty;
            if (level == 0 || level >= value.Length || !char.IsWhiteSpace(value[level]))
            {
                level = 0;
                return false;
            }

            text = value.Substring(level).Trim();
            int closing = text.Length - 1;
            while (closing >= 0 && text[closing] == '#')
            {
                closing--;
            }

            if (closing >= 0 && closing < text.Length - 1 && char.IsWhiteSpace(text[closing]))
            {
                text = text.Substring(0, closing).TrimEnd();
            }

            return true;
        }

        private static bool TryReadListItem(string line, out bool ordered, out string item)
        {
            string value = (line ?? string.Empty).TrimStart();
            ordered = false;
            item = string.Empty;
            if (value.Length >= 2 &&
                (value[0] == '-' || value[0] == '*' || value[0] == '+') &&
                char.IsWhiteSpace(value[1]))
            {
                item = value.Substring(2).Trim();
                return true;
            }

            int digitEnd = 0;
            while (digitEnd < value.Length && char.IsDigit(value[digitEnd]))
            {
                digitEnd++;
            }

            if (digitEnd == 0 || digitEnd + 1 >= value.Length ||
                value[digitEnd] != '.' || !char.IsWhiteSpace(value[digitEnd + 1]))
            {
                return false;
            }

            ordered = true;
            item = value.Substring(digitEnd + 2).Trim();
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
            return value.Length <= 1 ? string.Empty : value.Substring(1).TrimStart();
        }

        private static bool IsHorizontalRule(string line)
        {
            string value = (line ?? string.Empty).Trim().Replace(" ", string.Empty).Replace("\t", string.Empty);
            if (value.Length < 3)
            {
                return false;
            }

            char marker = value[0];
            if (marker != '-' && marker != '*' && marker != '_')
            {
                return false;
            }

            for (int index = 1; index < value.Length; index++)
            {
                if (value[index] != marker)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadTable(
            string[] lines,
            int start,
            out IList<IList<string>> rows,
            out int end)
        {
            rows = new List<IList<string>>();
            end = start;
            if (start + 1 >= lines.Length || lines[start].IndexOf('|') < 0)
            {
                return false;
            }

            IList<string> headers = SplitTableRow(lines[start]);
            IList<string> delimiters = SplitTableRow(lines[start + 1]);
            if (headers.Count == 0 || headers.Count != delimiters.Count ||
                !IsTableDelimiter(delimiters))
            {
                return false;
            }

            int columnCount = headers.Count;
            rows.Add(headers);
            int index = start + 2;
            while (index < lines.Length &&
                !string.IsNullOrWhiteSpace(lines[index]) &&
                lines[index].IndexOf('|') >= 0)
            {
                IList<string> row = SplitTableRow(lines[index]);
                while (row.Count < columnCount)
                {
                    row.Add(string.Empty);
                }

                if (row.Count > columnCount)
                {
                    List<string> trimmed = new List<string>();
                    for (int column = 0; column < columnCount; column++)
                    {
                        trimmed.Add(row[column]);
                    }

                    row = trimmed;
                }

                rows.Add(row);
                index++;
            }

            end = index;
            return true;
        }

        private static IList<string> SplitTableRow(string line)
        {
            string value = (line ?? string.Empty).Trim();
            if (value.StartsWith("|", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (value.EndsWith("|", StringComparison.Ordinal) && !IsEscaped(value, value.Length - 1))
            {
                value = value.Substring(0, value.Length - 1);
            }

            List<string> cells = new List<string>();
            StringBuilder cell = new StringBuilder();
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current == '|' && !IsEscaped(value, index))
                {
                    cells.Add(UnescapeTableCell(cell.ToString().Trim()));
                    cell.Length = 0;
                }
                else
                {
                    cell.Append(current);
                }
            }

            cells.Add(UnescapeTableCell(cell.ToString().Trim()));
            return cells;
        }

        private static bool IsTableDelimiter(IList<string> cells)
        {
            for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                string value = (cells[cellIndex] ?? string.Empty).Trim();
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

                for (int index = 0; index < value.Length; index++)
                {
                    if (value[index] != '-')
                    {
                        return false;
                    }
                }
            }

            return cells.Count > 0;
        }

        private static string UnescapeTableCell(string value)
        {
            return (value ?? string.Empty).Replace("\\|", "|");
        }

        private static bool IsEscaped(string value, int characterIndex)
        {
            int slashCount = 0;
            for (int index = characterIndex - 1; index >= 0 && value[index] == '\\'; index--)
            {
                slashCount++;
            }

            return slashCount % 2 == 1;
        }

        private static int[] BuildTableTabStops(
            RenderContext context,
            IList<IList<string>> rows)
        {
            int columnCount = rows[0].Count;
            if (columnCount <= 1)
            {
                return new int[0];
            }

            int available = Math.Max(180, context.Target.ClientSize.Width - 40);
            int maximumColumn = Math.Max(72, Math.Min(240, available / columnCount));
            int[] widths = new int[columnCount];
            Font measuringFont = context.GetBodyFont(context.BodySize, FontStyle.Regular);
            for (int column = 0; column < columnCount; column++)
            {
                int width = 64;
                for (int row = 0; row < rows.Count; row++)
                {
                    string value = column < rows[row].Count
                        ? FlattenTableCell(rows[row][column])
                        : string.Empty;
                    Size measured = TextRenderer.MeasureText(
                        value,
                        measuringFont,
                        new Size(maximumColumn, int.MaxValue),
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    width = Math.Max(width, Math.Min(maximumColumn, measured.Width + 22));
                }

                widths[column] = width;
            }

            int total = 0;
            for (int index = 0; index < widths.Length; index++)
            {
                total += widths[index];
            }

            if (total > available)
            {
                double scale = (double)available / total;
                for (int index = 0; index < widths.Length; index++)
                {
                    widths[index] = Math.Max(56, (int)Math.Floor(widths[index] * scale));
                }
            }

            int[] tabs = new int[columnCount - 1];
            int position = 0;
            for (int index = 0; index < tabs.Length; index++)
            {
                position += widths[index];
                tabs[index] = position;
            }

            return tabs;
        }

        private static string FlattenTableCell(string value)
        {
            return (value ?? string.Empty)
                .Replace("<br />", " / ")
                .Replace("<br/>", " / ")
                .Replace("<br>", " / ")
                .Replace("\r\n", " / ")
                .Replace("\r", " / ")
                .Replace("\n", " / ");
        }

        private static bool StartsWith(string text, int index, int end, string value)
        {
            if (index < 0 || index + value.Length > end)
            {
                return false;
            }

            return string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
        }

        private static bool CanOpenBold(string text, int index, int end, string marker)
        {
            int contentStart = index + marker.Length;
            if (contentStart >= end || char.IsWhiteSpace(text[contentStart]) || IsEscaped(text, index))
            {
                return false;
            }

            if (marker == "__" && index > 0 && IsWordCharacter(text[index - 1]))
            {
                return false;
            }

            return true;
        }

        private static int FindClosingBold(string text, int start, int end, string marker)
        {
            int index = start;
            while (index + marker.Length <= end)
            {
                if (StartsWith(text, index, end, marker) &&
                    !IsEscaped(text, index) &&
                    index > start &&
                    !char.IsWhiteSpace(text[index - 1]) &&
                    (marker != "__" || index + marker.Length >= end ||
                        !IsWordCharacter(text[index + marker.Length])))
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        private static int FindDelimiter(
            string text,
            int start,
            int end,
            string delimiter)
        {
            int index = start;
            while (index + delimiter.Length <= end)
            {
                if (StartsWith(text, index, end, delimiter) && !IsEscaped(text, index))
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        private static int CountRun(string text, int index, int end, char value)
        {
            int count = 0;
            while (index + count < end && text[index + count] == value)
            {
                count++;
            }

            return count;
        }

        private static bool IsEscapableMarkdownCharacter(char value)
        {
            return value == '\\' || value == '`' || value == '*' ||
                value == '_' || value == '#' || value == '[' ||
                value == ']' || value == '|' || value == '>' ||
                value == '~';
        }

        private static bool IsWordCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        private static int HtmlBreakLength(string text, int index, int end)
        {
            string[] values = { "<br>", "<br/>", "<br />" };
            for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                string value = values[valueIndex];
                if (index + value.Length <= end &&
                    string.Compare(
                        text,
                        index,
                        value,
                        0,
                        value.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return value.Length;
                }
            }

            return 0;
        }

        private static string SanitizeText(string value)
        {
            string normalized = (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");
            StringBuilder result = new StringBuilder(normalized.Length + 8);
            for (int index = 0; index < normalized.Length; index++)
            {
                char current = normalized[index];
                if (current == '\n')
                {
                    result.Append("\r\n");
                }
                else if (current == '\t')
                {
                    result.Append(current);
                }
                else if (char.IsHighSurrogate(current))
                {
                    if (index + 1 < normalized.Length &&
                        char.IsLowSurrogate(normalized[index + 1]))
                    {
                        result.Append(current);
                        result.Append(normalized[++index]);
                    }
                    else
                    {
                        result.Append('\uFFFD');
                    }
                }
                else if (char.IsLowSurrogate(current) ||
                    (char.IsControl(current) && current != '\t'))
                {
                    result.Append('\uFFFD');
                }
                else
                {
                    result.Append(current);
                }
            }

            return result.ToString();
        }

        private static Color Blend(Color first, Color second, double secondWeight)
        {
            double weight = Math.Max(0.0, Math.Min(1.0, secondWeight));
            int red = (int)Math.Round(first.R * (1.0 - weight) + second.R * weight);
            int green = (int)Math.Round(first.G * (1.0 - weight) + second.G * weight);
            int blue = (int)Math.Round(first.B * (1.0 - weight) + second.B * weight);
            return Color.FromArgb(red, green, blue);
        }

        private static double ContrastRatio(Color first, Color second)
        {
            double lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
            double darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static double RelativeLuminance(Color color)
        {
            return 0.2126 * LinearChannel(color.R) +
                0.7152 * LinearChannel(color.G) +
                0.0722 * LinearChannel(color.B);
        }

        private static double LinearChannel(byte value)
        {
            double channel = value / 255.0;
            return channel <= 0.03928
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
    }
}
