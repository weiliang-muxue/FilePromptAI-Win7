using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FilePromptWin7
{
    internal static class AtomicFile
    {
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        public static void WriteAllText(
            string path,
            string value,
            Encoding encoding)
        {
            if (encoding == null)
            {
                throw new ArgumentNullException("encoding");
            }

            byte[] preamble = encoding.GetPreamble();
            byte[] content = encoding.GetBytes(value ?? string.Empty);
            byte[] bytes = new byte[preamble.Length + content.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(
                content,
                0,
                bytes,
                preamble.Length,
                content.Length);
            WriteAllBytes(path, bytes);
        }

        public static void WriteAllBytes(string path, byte[] value)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "An output path is required.",
                    "path");
            }

            string outputPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(outputPath) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = value ?? new byte[0];
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (!MoveFileEx(
                    temporaryPath,
                    outputPath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The output file could not be replaced atomically.",
                        new Win32Exception(error));
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch
                    {
                        // Do not mask the original write error.
                    }
                }
            }
        }

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            int flags);
    }
}
