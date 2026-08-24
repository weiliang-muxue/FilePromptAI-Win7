using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class GenerateAppIcon
{
    private static readonly int[] Sizes =
        { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: GenerateAppIcon <output.ico>");
            return 2;
        }

        string outputPath = Path.GetFullPath(args[0]);
        string directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<byte[]> images = new List<byte[]>();
        foreach (int size in Sizes)
        {
            images.Add(CreatePng(size));
        }

        using (FileStream stream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)Sizes.Length);

            int offset = 6 + (Sizes.Length * 16);
            for (int index = 0; index < Sizes.Length; index++)
            {
                int size = Sizes[index];
                byte dimension = size >= 256 ? (byte)0 : (byte)size;
                writer.Write(dimension);
                writer.Write(dimension);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(images[index].Length);
                writer.Write(offset);
                offset += images[index].Length;
            }

            foreach (byte[] image in images)
            {
                writer.Write(image);
            }
        }

        Console.WriteLine(outputPath);
        return 0;
    }

    private static byte[] CreatePng(int size)
    {
        using (Bitmap bitmap = new Bitmap(
            size,
            size,
            PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float margin = Math.Max(0.75F, size * 0.045F);
            RectangleF bounds = new RectangleF(
                margin,
                margin,
                size - (margin * 2F),
                size - (margin * 2F));
            using (GraphicsPath background = CreateRoundedRectangle(
                bounds,
                size * 0.18F))
            using (Brush fill = new SolidBrush(Color.FromArgb(255, 31, 35, 42)))
            using (Pen border = new Pen(
                Color.FromArgb(255, 91, 101, 116),
                Math.Max(0.8F, size * 0.018F)))
            {
                graphics.FillPath(fill, background);
                graphics.DrawPath(border, background);
            }

            float stroke = Math.Max(1.4F, size * 0.082F);
            using (Pen prompt = new Pen(
                Color.FromArgb(255, 45, 121, 218),
                stroke))
            using (Pen cursor = new Pen(
                Color.FromArgb(255, 241, 245, 249),
                Math.Max(1.25F, size * 0.065F)))
            {
                prompt.StartCap = LineCap.Round;
                prompt.EndCap = LineCap.Round;
                prompt.LineJoin = LineJoin.Round;
                cursor.StartCap = LineCap.Round;
                cursor.EndCap = LineCap.Round;

                PointF[] chevron =
                {
                    new PointF(size * 0.28F, size * 0.29F),
                    new PointF(size * 0.51F, size * 0.50F),
                    new PointF(size * 0.28F, size * 0.71F)
                };
                graphics.DrawLines(prompt, chevron);
                graphics.DrawLine(
                    cursor,
                    size * 0.54F,
                    size * 0.70F,
                    size * 0.75F,
                    size * 0.70F);
            }

            using (MemoryStream output = new MemoryStream())
            {
                bitmap.Save(output, ImageFormat.Png);
                return output.ToArray();
            }
        }
    }

    private static GraphicsPath CreateRoundedRectangle(
        RectangleF bounds,
        float radius)
    {
        float diameter = Math.Min(
            Math.Min(bounds.Width, bounds.Height),
            radius * 2F);
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Top,
            diameter,
            diameter,
            270F,
            90F);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0F,
            90F);
        path.AddArc(
            bounds.Left,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            90F,
            90F);
        path.CloseFigure();
        return path;
    }
}
