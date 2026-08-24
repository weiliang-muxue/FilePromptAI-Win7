using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FilePromptAIWin7
{
    internal static class CsvExporter
    {
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

            if (document.Tables == null || document.Tables.Count == 0)
            {
                throw new InvalidOperationException(
                    "The Markdown text does not contain a table.");
            }

            Export(document.Tables[0], path);
        }

        public static void Export(MarkdownTable table, string path)
        {
            if (table == null)
            {
                throw new ArgumentNullException("table");
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

            // UTF-8 with BOM keeps Chinese text readable in older Excel versions.
            AtomicFile.WriteAllText(
                path,
                ToCsv(table),
                new UTF8Encoding(true));
        }

        public static string ToCsv(string markdown)
        {
            MarkdownDocument document = MarkdownDocument.Parse(markdown);
            if (document.Tables == null || document.Tables.Count == 0)
            {
                return string.Empty;
            }

            return ToCsv(document.Tables[0]);
        }

        public static string ToCsv(MarkdownTable table)
        {
            if (table == null)
            {
                return string.Empty;
            }

            StringBuilder result = new StringBuilder();
            IList<IList<string>> rows = table.GetAllRows();
            int columnCount = 0;
            foreach (IList<string> row in rows)
            {
                if (row != null && row.Count > columnCount)
                {
                    columnCount = row.Count;
                }
            }

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                IList<string> row = rows[rowIndex];
                for (int columnIndex = 0;
                    columnIndex < columnCount;
                    columnIndex++)
                {
                    if (columnIndex > 0)
                    {
                        result.Append(',');
                    }

                    string value = row != null && columnIndex < row.Count
                        ? row[columnIndex]
                        : string.Empty;
                    result.Append(EscapeCell(value));
                }

                if (rowIndex + 1 < rows.Count)
                {
                    result.Append("\r\n");
                }
            }

            return result.ToString();
        }

        private static string EscapeCell(string value)
        {
            string text = NeutralizeSpreadsheetFormula(
                SanitizeText(value));
            bool quote = text.IndexOf(',') >= 0 ||
                text.IndexOf('"') >= 0 ||
                text.IndexOf('\r') >= 0 ||
                text.IndexOf('\n') >= 0;
            if (text.IndexOf('"') >= 0)
            {
                text = text.Replace("\"", "\"\"");
            }

            return quote ? "\"" + text + "\"" : text;
        }

        private static string NeutralizeSpreadsheetFormula(string value)
        {
            string text = value ?? string.Empty;
            int index = 0;
            while (index < text.Length && text[index] == ' ')
            {
                index++;
            }

            if (index >= text.Length)
            {
                return text;
            }

            char marker = text[index];
            bool dangerous = marker == '=' || marker == '+' ||
                marker == '@' || marker == '\t' || marker == '\r' ||
                marker == '\n';
            if (marker == '-' && !IsPlainNumber(text.Substring(index)))
            {
                dangerous = true;
            }

            return dangerous ? "'" + text : text;
        }

        private static bool IsPlainNumber(string value)
        {
            decimal parsed;
            return decimal.TryParse(
                value,
                System.Globalization.NumberStyles.Float |
                    System.Globalization.NumberStyles.AllowThousands,
                System.Globalization.CultureInfo.InvariantCulture,
                out parsed);
        }

        private static string SanitizeText(string value)
        {
            string text = value ?? string.Empty;
            StringBuilder result = new StringBuilder(text.Length);
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

                result.Append(current);
            }

            return result.ToString();
        }
    }
}
