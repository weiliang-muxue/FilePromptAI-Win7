using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

internal static class ExtractorHardeningSmokeTest
{
    private static int Main(string[] args)
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "FilePromptExtractorHardening-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (args.Length != 1)
            {
                throw new ArgumentException(
                    "Usage: ExtractorHardeningSmokeTest <FilePrompt.exe>");
            }

            string applicationPath = Path.GetFullPath(args[0]);
            ConfigureAssemblyResolution(applicationPath);
            Assembly application = Assembly.LoadFrom(applicationPath);
            Type extractorType = application.GetType(
                "FilePromptWin7.FileContentExtractor",
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
