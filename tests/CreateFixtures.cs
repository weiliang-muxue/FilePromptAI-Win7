using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

internal static class CreateFixtures
{
    private static void Main(string[] args)
    {
        string outputDirectory = args.Length == 0
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures")
            : args[0];
        Directory.CreateDirectory(outputDirectory);

        File.WriteAllText(
            Path.Combine(outputDirectory, "sample.txt"),
            "中文文本测试\r\nHello FilePrompt\r\n第二行内容",
            new UTF8Encoding(true));
        CreateDocx(Path.Combine(outputDirectory, "sample.docx"));
        CreatePdf(Path.Combine(outputDirectory, "sample.pdf"));
        CreateImage(Path.Combine(outputDirectory, "sample.png"));
    }

    private static void CreateDocx(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            ZipArchiveEntry document = archive.CreateEntry("word/document.xml");
            using (Stream stream = document.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                    "<w:body>" +
                    "<w:p><w:r><w:t>Word 中文测试</w:t></w:r></w:p>" +
                    "<w:p><w:r><w:t>Hello DOCX</w:t><w:tab/><w:t>第二列</w:t></w:r></w:p>" +
                    "</w:body></w:document>");
            }
        }
    }

    private static void CreatePdf(string path)
    {
        string streamText = "BT /F1 18 Tf 72 720 Td (Hello PDF 123) Tj ET";
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length " + Encoding.ASCII.GetByteCount(streamText)
                .ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" +
                streamText + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        using (FileStream file = File.Create(path))
        using (StreamWriter writer = new StreamWriter(file, Encoding.ASCII))
        {
            writer.NewLine = "\n";
            writer.Write("%PDF-1.4\n");
            writer.Flush();

            List<long> offsets = new List<long>();
            for (int index = 0; index < objects.Length; index++)
            {
                offsets.Add(file.Position);
                writer.Write(
                    (index + 1).ToString(CultureInfo.InvariantCulture) +
                    " 0 obj\n" + objects[index] + "\nendobj\n");
                writer.Flush();
            }

            long xref = file.Position;
            writer.Write("xref\n0 6\n");
            writer.Write("0000000000 65535 f \n");
            foreach (long offset in offsets)
            {
                writer.Write(
                    offset.ToString("0000000000", CultureInfo.InvariantCulture) +
                    " 00000 n \n");
            }

            writer.Write(
                "trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" +
                xref.ToString(CultureInfo.InvariantCulture) +
                "\n%%EOF\n");
        }
    }

    private static void CreateImage(string path)
    {
        using (Bitmap bitmap = new Bitmap(640, 360))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (Font font = new Font("Arial", 28F, FontStyle.Bold))
        {
            graphics.Clear(Color.White);
            graphics.DrawRectangle(Pens.RoyalBlue, 10, 10, 619, 339);
            graphics.DrawString(
                "FilePrompt Image Test",
                font,
                Brushes.Black,
                new PointF(80F, 145F));
            bitmap.Save(path, ImageFormat.Png);
        }
    }
}
