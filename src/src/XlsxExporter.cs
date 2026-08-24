using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace FilePromptAIWin7
{
    internal static class XlsxExporter
    {
        private const int MaximumCellCharacters = 32767;

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

            if (document.Tables == null || document.Tables.Count == 0)
            {
                throw new InvalidOperationException(
                    "The Markdown text does not contain a table.");
            }

            using (XSSFWorkbook workbook = new XSSFWorkbook())
            {
                ICellStyle headerStyle = CreateHeaderStyle(workbook);
                ICellStyle bodyStyle = CreateBodyStyle(workbook);
                for (int index = 0; index < document.Tables.Count; index++)
                {
                    WriteTable(
                        workbook,
                        document.Tables[index],
                        index,
                        headerStyle,
                        bodyStyle);
                }

                using (MemoryStream memory = new MemoryStream())
                {
                    workbook.Write(memory, true);
                    return memory.ToArray();
                }
            }
        }

        private static ICellStyle CreateHeaderStyle(IWorkbook workbook)
        {
            ICellStyle style = workbook.CreateCellStyle();
            style.FillForegroundColor = IndexedColors.SeaGreen.Index;
            style.FillPattern = FillPattern.SolidForeground;
            style.BorderBottom = BorderStyle.Thin;
            style.BorderLeft = BorderStyle.Thin;
            style.BorderRight = BorderStyle.Thin;
            style.BorderTop = BorderStyle.Thin;
            style.Alignment = HorizontalAlignment.Left;
            style.VerticalAlignment = VerticalAlignment.Center;
            style.WrapText = true;

            IFont font = workbook.CreateFont();
            font.IsBold = true;
            font.Color = IndexedColors.White.Index;
            font.FontName = "Microsoft YaHei";
            font.FontHeightInPoints = 10;
            style.SetFont(font);
            return style;
        }

        private static ICellStyle CreateBodyStyle(IWorkbook workbook)
        {
            ICellStyle style = workbook.CreateCellStyle();
            style.BorderBottom = BorderStyle.Thin;
            style.BorderLeft = BorderStyle.Thin;
            style.BorderRight = BorderStyle.Thin;
            style.BorderTop = BorderStyle.Thin;
            style.VerticalAlignment = VerticalAlignment.Top;
            style.WrapText = true;

            IFont font = workbook.CreateFont();
            font.FontName = "Microsoft YaHei";
            font.FontHeightInPoints = 10;
            style.SetFont(font);
            return style;
        }

        private static void WriteTable(
            IWorkbook workbook,
            MarkdownTable table,
            int tableIndex,
            ICellStyle headerStyle,
            ICellStyle bodyStyle)
        {
            if (table == null || table.Headers == null ||
                table.Headers.Count == 0)
            {
                return;
            }

            ISheet sheet = workbook.CreateSheet(
                "表格 " + (tableIndex + 1).ToString());
            int columnCount = table.Headers.Count;
            int[] maximumWidths = new int[columnCount];

            IRow header = sheet.CreateRow(0);
            for (int column = 0; column < columnCount; column++)
            {
                string value = SanitizeCellText(table.Headers[column]);
                ICell cell = header.CreateCell(column, CellType.String);
                cell.SetCellValue(value);
                cell.CellStyle = headerStyle;
                maximumWidths[column] = MeasureWidth(value);
            }

            int rowIndex = 1;
            foreach (IList<string> values in table.Rows)
            {
                IRow row = sheet.CreateRow(rowIndex++);
                for (int column = 0; column < columnCount; column++)
                {
                    string value = values != null && column < values.Count
                        ? SanitizeCellText(values[column])
                        : string.Empty;
                    ICell cell = row.CreateCell(column, CellType.String);
                    cell.SetCellValue(value);
                    cell.CellStyle = bodyStyle;
                    maximumWidths[column] = Math.Max(
                        maximumWidths[column],
                        MeasureWidth(value));
                }
            }

            sheet.CreateFreezePane(0, 1);
            sheet.SetAutoFilter(
                new NPOI.SS.Util.CellRangeAddress(
                    0,
                    Math.Max(0, rowIndex - 1),
                    0,
                    columnCount - 1));
            for (int column = 0; column < columnCount; column++)
            {
                int characters = Math.Max(8, Math.Min(60, maximumWidths[column]));
                sheet.SetColumnWidth(column, Math.Min(255 * 256, (characters + 2) * 256));
            }
        }

        private static int MeasureWidth(string value)
        {
            int width = 0;
            int maximum = 0;
            foreach (char current in value ?? string.Empty)
            {
                if (current == '\r')
                {
                    continue;
                }

                if (current == '\n')
                {
                    maximum = Math.Max(maximum, width);
                    width = 0;
                }
                else
                {
                    width += current > 255 ? 2 : 1;
                }
            }

            return Math.Max(maximum, width);
        }

        private static string SanitizeCellText(string value)
        {
            StringBuilder result = new StringBuilder();
            string source = value ?? string.Empty;
            for (int index = 0;
                index < source.Length && result.Length < MaximumCellCharacters;
                index++)
            {
                char current = source[index];
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 < source.Length &&
                        char.IsLowSurrogate(source[index + 1]) &&
                        result.Length + 2 <= MaximumCellCharacters)
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
