using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml;

internal static class ExtractorHardeningSmokeTest
{
    private static int Main(string[] args)
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "FilePromptAIExtractorHardening-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (args.Length != 1)
            {
                throw new ArgumentException(
                    "Usage: ExtractorHardeningSmokeTest <FilePromptAI.exe>");
            }

            string applicationPath = Path.GetFullPath(args[0]);
            ConfigureAssemblyResolution(applicationPath);
            Assembly application = Assembly.LoadFrom(applicationPath);
            Type extractorType = application.GetType(
                "FilePromptAIWin7.FileContentExtractor",
                true);
            object extractor = Activator.CreateInstance(extractorType, true);
            MethodInfo extractFile = extractorType.GetMethod(
                "ExtractFile",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo createClipboardText = extractorType.GetMethod(
                "CreateClipboardText",
                BindingFlags.Instance | BindingFlags.Public);

            Directory.CreateDirectory(temporaryRoot);
            TestShortText(extractor, extractFile, temporaryRoot);
            TestSparseColumns(extractor, extractFile, temporaryRoot);
            TestUnicodeTruncation(extractor, createClipboardText);
            TestStructuredArchiveXmlSecurity(
                extractor,
                extractFile,
                temporaryRoot);
            TestStructuredArchiveLimits(
                extractor,
                extractFile,
                temporaryRoot);
            TestStructuredArchivePaths(
                extractor,
                extractFile,
                temporaryRoot);
            TestModernXMindJsonSecurity(
                extractor,
                extractFile,
                temporaryRoot);
            Console.WriteLine("PASS | extractor hardening");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | extractor hardening");
            Console.Error.WriteLine(Unwrap(exception));
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }
            catch
            {
                // Temporary test files are safe to remove on the next run.
            }
        }
    }

    private static void TestShortText(
        object extractor,
        MethodInfo extractFile,
        string root)
    {
        string shortText = Path.Combine(root, "README");
        File.WriteAllText(shortText, "A", new UTF8Encoding(false));
        object item = extractFile.Invoke(extractor, new object[] { shortText });
        Type itemType = item.GetType();
        AssertEqual(
            "Text",
            Convert.ToString(itemType.GetProperty("Kind").GetValue(item, null)),
            "One-byte extensionless text classification");
        AssertEqual(
            "A",
            Convert.ToString(
                itemType.GetProperty("TextContent").GetValue(item, null)),
            "One-byte extensionless text content");

        string shortBinary = Path.Combine(root, "binary");
        File.WriteAllBytes(shortBinary, new byte[] { 0x41, 0x01 });
        Exception failure = InvokeForFailure(
            delegate
            {
                extractFile.Invoke(extractor, new object[] { shortBinary });
            });
        AssertTrue(
            failure is NotSupportedException,
            "Short binary is rejected");
    }

    private static void TestSparseColumns(
        object extractor,
        MethodInfo extractFile,
        string root)
    {
        string valid = Path.Combine(root, "valid-xfd.xlsx");
        CreateMinimalXlsx(valid, "XFD1", "edge");
        object validItem = extractFile.Invoke(
            extractor,
            new object[] { valid });
        string text = Convert.ToString(
            validItem.GetType().GetProperty("TextContent")
                .GetValue(validItem, null));
        AssertTrue(
            text.IndexOf("edge", StringComparison.Ordinal) >= 0,
            "XFD column is accepted");

        TestInvalidColumn(extractor, extractFile, root, "XFE1");
        TestInvalidColumn(extractor, extractFile, root, "ZZZZZZ1");
    }

    private static void TestInvalidColumn(
        object extractor,
        MethodInfo extractFile,
        string root,
        string reference)
    {
        string path = Path.Combine(root, "invalid-" + reference + ".xlsx");
        CreateMinimalXlsx(path, reference, "blocked");
        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception failure = InvokeForFailure(
            delegate
            {
                extractFile.Invoke(extractor, new object[] { path });
            });
        stopwatch.Stop();
        AssertTrue(
            failure is InvalidDataException,
            reference + " column is rejected");
        AssertTrue(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            reference + " rejection is bounded");
    }

    private static void TestUnicodeTruncation(
        object extractor,
        MethodInfo createClipboardText)
    {
        string value = new string('a', 1999999) +
            "\uD83D\uDE00" + "z";
        object item = createClipboardText.Invoke(
            extractor,
            new object[] { value });
        string text = Convert.ToString(
            item.GetType().GetProperty("TextContent").GetValue(item, null));
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length ||
                    !char.IsLowSurrogate(text[index + 1]))
                {
                    throw new InvalidDataException(
                        "Truncation produced an isolated high surrogate.");
                }

                index++;
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                throw new InvalidDataException(
                    "Truncation produced an isolated low surrogate.");
            }
        }

        AssertTrue(true, "Truncation preserves surrogate pairs");
    }

    private static void TestStructuredArchiveXmlSecurity(
        object extractor,
        MethodInfo extractFile,
        string root)
    {
        string dtdPath = Path.Combine(root, "dtd.xmind");
        CreateArchive(
            dtdPath,
            delegate(ZipArchive archive)
            {
                AddEntry(
                    archive,
                    "content.xml",
                    "<!DOCTYPE x [<!ENTITY payload 'blocked'>]>" +
                    "<xmap-content><sheet><topic><title>&payload;</title>" +
                    "</topic></sheet></xmap-content>");
            });
        AssertRejected(
            extractor,
            extractFile,
            dtdPath,
            delegate(Exception failure)
            {
                return failure is XmlException || failure is InvalidDataException;
            },
            "XMind DTD is rejected");

        string oversizedXmlPath = Path.Combine(root, "oversized-xml.xmind");
        CreateArchive(
            oversizedXmlPath,
            delegate(ZipArchive archive)
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    "content.xml",
                    CompressionLevel.NoCompression);
                using (Stream stream = entry.Open())
                using (StreamWriter writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(false)))
                {
                    writer.Write("<xmap-content><!--");
                    WriteRepeated(writer, 'x', 32 * 1024 * 1024);
                    writer.Write("--></xmap-content>");
                }
            });
        AssertRejected(
            extractor,
            extractFile,
            oversizedXmlPath,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "XMind XML over 32 MB is rejected");

        string pptxDtdPath = Path.Combine(root, "dtd.pptx");
        CreateArchive(
            pptxDtdPath,
            delegate(ZipArchive archive)
            {
                AddEntry(
                    archive,
                    "ppt/slides/slide1.xml",
                    "<!DOCTYPE p:sld [<!ENTITY payload 'blocked'>]>" +
                    "<p:sld xmlns:p=\"urn:p\" xmlns:a=\"urn:a\">" +
                    "<p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r>" +
                    "<a:t>&payload;</a:t></a:r></a:p></p:txBody></p:sp>" +
                    "</p:spTree></p:cSld></p:sld>");
            });
        AssertRejected(
            extractor,
            extractFile,
            pptxDtdPath,
            delegate(Exception failure)
            {
                return failure is XmlException || failure is InvalidDataException;
            },
            "PPTX DTD is rejected");
    }

    private static void TestStructuredArchiveLimits(
        object extractor,
        MethodInfo extractFile,
        string root)
    {
        string bombPath = Path.Combine(root, "compression-ratio.xmind");
        CreateArchive(
            bombPath,
            delegate(ZipArchive archive)
            {
                AddEntry(archive, "content.xml", MinimalXMindXml());
                ZipArchiveEntry bomb = archive.CreateEntry(
                    "resources/bomb.bin",
                    CompressionLevel.Fastest);
                using (Stream stream = bomb.Open())
                {
                    byte[] zeros = new byte[8192];
                    for (int index = 0; index < 256; index++)
                    {
                        stream.Write(zeros, 0, zeros.Length);
                    }
                }
            });
        AssertRejected(
            extractor,
            extractFile,
            bombPath,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "XMind ZIP compression bomb is rejected");

        string entryCountPath = Path.Combine(root, "too-many-entries.xmind");
        CreateArchive(
            entryCountPath,
            delegate(ZipArchive archive)
            {
                for (int index = 0; index < 4097; index++)
                {
                    archive.CreateEntry(
                        "items/item" + index.ToString("D4") + ".bin",
                        CompressionLevel.NoCompression);
                }
            });
        AssertRejected(
            extractor,
            extractFile,
            entryCountPath,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "XMind entry count over 4,096 is rejected");

        string totalSizePath = Path.Combine(root, "expanded-size.xmind");
        CreateArchive(
            totalSizePath,
            delegate(ZipArchive archive)
            {
                AddEntry(archive, "content.xml", MinimalXMindXml());
            });
        PatchFirstCentralDirectoryUncompressedSize(
            totalSizePath,
            256U * 1024U * 1024U + 1U);
        AssertRejected(
            extractor,
            extractFile,
            totalSizePath,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "XMind expanded size over 256 MB is rejected");

        string sourceSizePath = Path.Combine(root, "source-over-100mb.pptx");
        using (FileStream stream = new FileStream(
            sourceSizePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            stream.SetLength(100L * 1024L * 1024L + 1L);
        }
        AssertRejected(
            extractor,
            extractFile,
            sourceSizePath,
            delegate(Exception failure) { return failure is InvalidOperationException; },
            "PPTX source over 100 MB is rejected");
    }

    private static void TestStructuredArchivePaths(
        object extractor,
        MethodInfo extractFile,
        string root)
    {
        string missingXMind = Path.Combine(root, "missing-content.xmind");
        CreateArchive(
            missingXMind,
            delegate(ZipArchive archive)
            {
                AddEntry(archive, "manifest.xml", "<manifest/>");
            });
        AssertRejected(
            extractor,
            extractFile,
            missingXMind,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "XMind missing content.xml is rejected");

        string missingPptx = Path.Combine(root, "missing-slide.pptx");
        CreateArchive(
            missingPptx,
            delegate(ZipArchive archive)
            {
                AddEntry(archive, "ppt/presentation.xml", "<p:presentation xmlns:p=\"urn:p\"/>");
            });
        AssertRejected(
            extractor,
            extractFile,
            missingPptx,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "PPTX missing slide XML is rejected");

        string duplicatePath = Path.Combine(root, "duplicate.xmind");
        CreateArchive(
            duplicatePath,
            delegate(ZipArchive archive)
            {
                AddEntry(archive, "content.xml", MinimalXMindXml());
                AddEntry(archive, "CONTENT.XML", MinimalXMindXml());
            });
        AssertRejected(
            extractor,
            extractFile,
            duplicatePath,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "XMind duplicate canonical path is rejected");

        string dangerousPath = Path.Combine(root, "dangerous-path.pptx");
        CreateArchive(
            dangerousPath,
            delegate(ZipArchive archive)
            {
                AddEntry(
                    archive,
                    "ppt/slides/slide1.xml",
                    MinimalSlideXml());
                AddEntry(archive, "../escape.xml", "<escape/>");
            });
        AssertRejected(
            extractor,
            extractFile,
            dangerousPath,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "PPTX traversal path is rejected");

        string unnormalizedPath = Path.Combine(root, "unnormalized-path.pptx");
        CreateArchive(
            unnormalizedPath,
            delegate(ZipArchive archive)
            {
                AddEntry(
                    archive,
                    "ppt/slides/slide1.xml",
                    MinimalSlideXml());
                AddEntry(archive, "ppt//hidden.xml", "<hidden/>");
            });
        AssertRejected(
            extractor,
            extractFile,
            unnormalizedPath,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "PPTX unnormalized path is rejected");
    }

    private static void TestModernXMindJsonSecurity(
        object extractor,
        MethodInfo extractFile,
        string root)
    {
        string malformedPath = Path.Combine(root, "malformed-json.xmind");
        CreateArchive(
            malformedPath,
            delegate(ZipArchive archive)
            {
                AddEntry(archive, "content.json", "[{\"title\":]");
            });
        AssertRejected(
            extractor,
            extractFile,
            malformedPath,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "Malformed modern XMind JSON is rejected");

        string invalidUtf8Path = Path.Combine(root, "invalid-utf8.xmind");
        CreateArchive(
            invalidUtf8Path,
            delegate(ZipArchive archive)
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    "content.json",
                    CompressionLevel.NoCompression);
                using (Stream stream = entry.Open())
                {
                    stream.WriteByte(0xFF);
                    stream.WriteByte(0xFE);
                }
            });
        AssertRejected(
            extractor,
            extractFile,
            invalidUtf8Path,
            delegate(Exception failure) { return failure is InvalidDataException; },
            "Non-UTF8 modern XMind JSON is rejected");
    }

    private static void AssertRejected(
        object extractor,
        MethodInfo extractFile,
        string path,
        Func<Exception, bool> expected,
        string name)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception failure = InvokeForFailure(
            delegate
            {
                extractFile.Invoke(extractor, new object[] { path });
            });
        stopwatch.Stop();
        AssertTrue(failure != null && expected(failure), name);
        AssertTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), name + " quickly");
    }

    private static void CreateArchive(string path, Action<ZipArchive> write)
    {
        using (FileStream stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        using (ZipArchive archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            false,
            Encoding.UTF8))
        {
            write(archive);
        }
    }

    private static string MinimalXMindXml()
    {
        return "<xmap-content><sheet><topic><title>root</title>" +
            "</topic></sheet></xmap-content>";
    }

    private static string MinimalSlideXml()
    {
        return "<p:sld xmlns:p=\"urn:p\" xmlns:a=\"urn:a\"><p:cSld>" +
            "<p:spTree><p:sp><p:txBody><a:p><a:r><a:t>safe</a:t>" +
            "</a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";
    }

    private static void WriteRepeated(
        TextWriter writer,
        char value,
        int count)
    {
        char[] buffer = new char[8192];
        for (int index = 0; index < buffer.Length; index++)
        {
            buffer[index] = value;
        }

        while (count > 0)
        {
            int current = Math.Min(count, buffer.Length);
            writer.Write(buffer, 0, current);
            count -= current;
        }
    }

    private static void PatchFirstCentralDirectoryUncompressedSize(
        string path,
        uint value)
    {
        byte[] bytes = File.ReadAllBytes(path);
        for (int index = 0; index <= bytes.Length - 28; index++)
        {
            if (bytes[index] == 0x50 && bytes[index + 1] == 0x4B &&
                bytes[index + 2] == 0x01 && bytes[index + 3] == 0x02)
            {
                bytes[index + 24] = (byte)value;
                bytes[index + 25] = (byte)(value >> 8);
                bytes[index + 26] = (byte)(value >> 16);
                bytes[index + 27] = (byte)(value >> 24);
                File.WriteAllBytes(path, bytes);
                return;
            }
        }

        throw new InvalidDataException("ZIP central directory was not found.");
    }

    private static void CreateMinimalXlsx(
        string path,
        string cellReference,
        string value)
    {
        using (FileStream stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        using (ZipArchive archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            false,
            Encoding.UTF8))
        {
            AddEntry(
                archive,
                "xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/>" +
                "</sheets></workbook>");
            AddEntry(
                archive,
                "xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" " +
                "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
                "Target=\"worksheets/sheet1.xml\"/></Relationships>");
            AddEntry(
                archive,
                "xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<sheetData><row r=\"1\"><c r=\"" + cellReference +
                "\" t=\"inlineStr\"><is><t>" + value +
                "</t></is></c></row></sheetData></worksheet>");
        }
    }

    private static void AddEntry(
        ZipArchive archive,
        string name,
        string value)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            name,
            CompressionLevel.Fastest);
        using (Stream stream = entry.Open())
        using (StreamWriter writer = new StreamWriter(
            stream,
            new UTF8Encoding(false)))
        {
            writer.Write(value);
        }
    }

    private static void ConfigureAssemblyResolution(string applicationPath)
    {
        string directory = Path.GetDirectoryName(applicationPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(
            object sender,
            ResolveEventArgs args)
        {
            string candidate = Path.Combine(
                directory,
                new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(candidate)
                ? Assembly.LoadFrom(candidate)
                : null;
        };
    }

    private static Exception InvokeForFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return Unwrap(exception);
        }
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while (current is TargetInvocationException &&
            current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private static void AssertEqual(
        string expected,
        string actual,
        string name)
    {
        AssertTrue(
            string.Equals(expected, actual, StringComparison.Ordinal),
            name + " expected [" + expected + "] but got [" + actual + "]");
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " failed.");
        }
    }
}
