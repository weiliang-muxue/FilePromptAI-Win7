using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FilePromptAIWin7
{
    internal struct ReplaceFileCallResult
    {
        public bool Succeeded;
        public int ErrorCode;

        public ReplaceFileCallResult(bool succeeded, int errorCode)
        {
            Succeeded = succeeded;
            ErrorCode = errorCode;
        }
    }

    internal delegate ReplaceFileCallResult ReplaceFileTestInvoker(
        string replacedFileName,
        string replacementFileName,
        string backupFileName);

    internal sealed class WorkspaceFileInfo
    {
        public string RelativePath { get; internal set; }
        public long Length { get; internal set; }
        public DateTime LastWriteUtc { get; internal set; }
    }

    internal sealed class WorkspaceSearchResult
    {
        public string RelativePath { get; internal set; }
        public int LineNumber { get; internal set; }
        public string LineText { get; internal set; }
    }

    internal sealed class WorkspaceReadResult
    {
        public string RelativePath { get; internal set; }
        public string Text { get; internal set; }
        public string Sha256 { get; internal set; }
        public string EncodingName { get; internal set; }
        public bool HasBom { get; internal set; }
        public string NewLine { get; internal set; }
        public bool HasFinalNewLine { get; internal set; }
        public long Length { get; internal set; }
    }

    internal sealed class WorkspaceEdit
    {
        public string RelativePath { get; private set; }
        public string BaseSha256 { get; private set; }
        public string NewText { get; private set; }

        public WorkspaceEdit(
            string relativePath,
            string baseSha256,
            string newText)
        {
            RelativePath = relativePath;
            BaseSha256 = baseSha256;
            NewText = newText;
        }
    }

    internal sealed class WorkspaceEditResult
    {
        public string RelativePath { get; internal set; }
        public string Sha256 { get; internal set; }
        public bool BackupCreated { get; internal set; }
        public bool UndoAvailable { get; internal set; }
    }

    internal sealed class WorkspaceEditPreview
    {
        public string RelativePath { get; internal set; }
        public string ExpectedSha256 { get; internal set; }
        public string OriginalText { get; internal set; }
        public string NewText { get; internal set; }
        public string NewSha256 { get; internal set; }

        public bool HasChanges
        {
            get
            {
                return !string.Equals(
                    ExpectedSha256,
                    NewSha256,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // This is an application-level capability boundary. Every path supplied by
    // a model is relative to the directory selected by the user and is checked
    // again immediately before a read or replacement.
    internal sealed class CodeWorkspace : IDisposable
    {
        // Leave enough room below legacy MAX_PATH for the same-directory
        // temporary file used by an atomic replacement on Windows 7.
        private const int MaximumPathCharacters = 200;
        private const long MaximumTextFileBytes = 256L * 1024L;
        private const int MaximumEnumeratedFiles = 10000;
        private const int MaximumDirectoryDepth = 32;
        private const int MaximumSearchLineCharacters = 1000;
        private const int MaximumQueryCharacters = 512;
        private const int MaximumResultCount = 500;
        private const long MaximumSearchBytes = 64L * 1024L * 1024L;
        private const int MaximumConcurrentBaselines = 2048;

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeReadOnly = 0x00000001;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint ReplaceFileNoFlags = 0;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorUnableToRemoveReplaced = 1175;
        private const int ErrorUnableToMoveReplacement = 1176;
        private const int ErrorUnableToMoveReplacement2 = 1177;
        private const int FileRenameInfo = 3;
        private const int FileDispositionInfo = 4;

        private static readonly byte[] BackupEntropy = Encoding.UTF8.GetBytes(
            "FilePromptAI.CodeWorkspace.Backup.v1");

        // Test-only seams. Production code leaves both delegates null.
        internal static ReplaceFileTestInvoker ReplaceFileInvokerForTest;
        internal static Action<string> BeforeOwnedTemporaryDeleteForTest;

        private readonly object mutationLock = new object();
        private readonly string rootPath;
        private readonly string rootPrefix;
        private readonly NativeIdentity rootIdentity;
        private readonly Dictionary<string, BaselineRecord> baselines;
        private SafeFileHandle rootHandle;
        private UndoRecord undoRecord;
        private bool disposed;

        public string RootPath
        {
            get { return rootPath; }
        }

        public string SelectedRelativePath { get; private set; }

        public string LastEditedRelativePath
        {
            get
            {
                lock (mutationLock)
                {
                    return undoRecord == null
                        ? string.Empty
                        : undoRecord.RelativePath;
                }
            }
        }

        public bool CanUndo
        {
            get
            {
                lock (mutationLock)
                {
                    return undoRecord != null;
                }
            }
        }

        private CodeWorkspace(
            string normalizedRoot,
            string selectedRelativePath,
            SafeFileHandle verifiedRootHandle,
            NativeIdentity verifiedRootIdentity)
        {
            rootPath = normalizedRoot;
            rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
            SelectedRelativePath = selectedRelativePath;
            rootHandle = verifiedRootHandle;
            rootIdentity = verifiedRootIdentity;
            baselines = new Dictionary<string, BaselineRecord>(
                StringComparer.OrdinalIgnoreCase);
        }

        public static CodeWorkspace OpenFromSelectedFile(
            string selectedFilePath)
        {
            if (string.IsNullOrWhiteSpace(selectedFilePath))
            {
                throw new ArgumentException("必须先选择一个代码文件。", "selectedFilePath");
            }

            string rawPath = selectedFilePath.Trim();
            RejectNonLocalAbsolutePath(rawPath);
            if (!IsDriveLetterAbsolutePath(rawPath))
            {
                throw new UnauthorizedAccessException(
                    "必须选择本机固定磁盘上的绝对文件路径。");
            }
            string fullPath = Path.GetFullPath(rawPath);
            if (!IsDriveLetterAbsolutePath(fullPath) ||
                fullPath.Length > MaximumPathCharacters)
            {
                throw new InvalidOperationException("所选文件路径不受支持。");
            }

            string root = TrimTrailingSeparators(Path.GetDirectoryName(fullPath));
            string volumeRoot = TrimTrailingSeparators(Path.GetPathRoot(fullPath));
            if (string.IsNullOrEmpty(root) ||
                string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "不能把整个磁盘根目录授权给代码工作区。请选择子文件夹中的文件。");
            }

            DriveInfo drive = new DriveInfo(Path.GetPathRoot(fullPath));
            if (drive.DriveType != DriveType.Fixed)
            {
                throw new InvalidOperationException(
                    "代码工作区只支持本机固定磁盘，拒绝网络盘和移动盘。");
            }

            ValidateAbsoluteComponents(fullPath);
            string selectedRelative = GetStrictRelativePath(root, fullPath);
            ValidateRelativePath(selectedRelative, false);

            SafeFileHandle verifiedRoot = null;
            try
            {
                NativeEntry rootEntry = OpenAndVerifyNativeEntry(root, true, false);
                verifiedRoot = rootEntry.Handle;
                rootEntry.Handle = null;
                TextSnapshot selectedSnapshot;
                NativeEntry selected = OpenAndVerifyNativeEntry(fullPath, false, true);
                try
                {
                    RejectUnsafeFileInformation(selected.Information);
                    selectedSnapshot = ReadSnapshotFromHandle(
                        selectedRelative,
                        selected.Handle,
                        selected.Information);
                }
                finally
                {
                    selected.Dispose();
                }

                CodeWorkspace workspace = new CodeWorkspace(
                    root,
                    selectedRelative,
                    verifiedRoot,
                    NativeIdentity.From(rootEntry.Information));
                workspace.RememberBaseline(selectedSnapshot);
                return workspace;
            }
            catch
            {
                if (verifiedRoot != null)
                {
                    verifiedRoot.Dispose();
                }
                throw;
            }
        }

        public IList<WorkspaceFileInfo> ListFiles(
            string relativeDirectory,
            int maxResults)
        {
            lock (mutationLock)
            {
                ThrowIfDisposed();
                int limit = NormalizeResultLimit(maxResults);
                string normalizedDirectory = NormalizeRelativeDirectory(
                    relativeDirectory);
                string directoryPath = ResolveRelativeDirectory(
                    normalizedDirectory);
                AssertRootIdentity();

                List<WorkspaceFileInfo> result = new List<WorkspaceFileInfo>();
                int visited = 0;
                EnumerateFilesSafely(
                    directoryPath,
                    normalizedDirectory,
                    0,
                    delegate(
                        string fullPath,
                        string relativePath,
                        EnumeratedFileInfo info)
                    {
                        visited++;
                        if (visited > MaximumEnumeratedFiles)
                        {
                            throw new InvalidOperationException(
                                "工作区文件过多，请选择更小的子目录。");
                        }
                        if (result.Count >= limit)
                        {
                            return false;
                        }
                        result.Add(new WorkspaceFileInfo
                        {
                            RelativePath = relativePath,
                            Length = info.Length,
                            LastWriteUtc = info.LastWriteUtc
                        });
                        return result.Count < limit;
                    });
                return result;
            }
        }

        public IList<WorkspaceSearchResult> Search(
            string relativeDirectory,
            string query,
            int maxResults)
        {
            lock (mutationLock)
            {
                ThrowIfDisposed();
                if (string.IsNullOrEmpty(query) ||
                    query.Length > MaximumQueryCharacters)
                {
                    throw new ArgumentException(
                        "搜索文字必须为 1 到 512 个字符。",
                        "query");
                }

                int limit = NormalizeResultLimit(maxResults);
                string normalizedDirectory = NormalizeRelativeDirectory(
                    relativeDirectory);
                string directoryPath = ResolveRelativeDirectory(
                    normalizedDirectory);
                AssertRootIdentity();

                List<WorkspaceSearchResult> result =
                    new List<WorkspaceSearchResult>();
                int visited = 0;
                long searchedBytes = 0;
                EnumerateFilesSafely(
                    directoryPath,
                    normalizedDirectory,
                    0,
                    delegate(
                        string fullPath,
                        string relativePath,
                        EnumeratedFileInfo info)
                    {
                        visited++;
                        if (visited > MaximumEnumeratedFiles)
                        {
                            throw new InvalidOperationException(
                                "工作区文件过多，请选择更小的子目录。");
                        }
                        if (info.Length > MaximumTextFileBytes)
                        {
                            return true;
                        }
                        if (searchedBytes + info.Length > MaximumSearchBytes)
                        {
                            return false;
                        }
                        searchedBytes += info.Length;

                        TextSnapshot snapshot;
                        try
                        {
                            snapshot = ReadSnapshot(relativePath);
                        }
                        catch (InvalidDataException)
                        {
                            return true;
                        }
                        catch (NotSupportedException)
                        {
                            return true;
                        }

                        AppendMatches(
                            result,
                            snapshot.Text,
                            relativePath,
                            query,
                            limit);
                        return result.Count < limit;
                    });
                return result;
            }
        }

        public WorkspaceReadResult ReadFile(string relativePath)
        {
            lock (mutationLock)
            {
                ThrowIfDisposed();
                TextSnapshot snapshot = ReadSnapshot(relativePath);
                RememberBaseline(snapshot);
                return snapshot.ToResult();
            }
        }

        public WorkspaceEditPreview PreviewEdit(WorkspaceEdit edit)
        {
            ValidateEdit(edit);

            lock (mutationLock)
            {
                ThrowIfDisposed();
                PreparedEdit prepared = PrepareEditLocked(edit);
                return new WorkspaceEditPreview
                {
                    RelativePath = prepared.Current.RelativePath,
                    ExpectedSha256 = prepared.Current.Sha256,
                    OriginalText = prepared.Current.Text,
                    NewText = prepared.NormalizedText,
                    NewSha256 = prepared.ReplacementHash
                };
            }
        }

        public WorkspaceEditResult ApplyEdit(WorkspaceEdit edit)
        {
            ValidateEdit(edit);

            lock (mutationLock)
            {
                ThrowIfDisposed();
                PreparedEdit prepared = PrepareEditLocked(edit);
                TextSnapshot current = prepared.Current;
                byte[] replacement = prepared.Replacement;
                string replacementHash = prepared.ReplacementHash;
                if (string.Equals(
                    replacementHash,
                    current.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return new WorkspaceEditResult
                    {
                        RelativePath = current.RelativePath,
                        Sha256 = current.Sha256,
                        BackupCreated = false,
                        UndoAvailable = undoRecord != null
                    };
                }

                UndoRecord previous = undoRecord;
                string backupPath = CreateEncryptedBackup(current.RawBytes);
                bool replacementOccurred = false;
                NativeIdentity replacementIdentity = new NativeIdentity();
                try
                {
                    ReplaceExistingFile(
                        current.RelativePath,
                        current.Identity,
                        current.Sha256,
                        replacement,
                        replacementHash,
                        out replacementOccurred,
                        out replacementIdentity);
                }
                catch
                {
                    // If replacement completed but post-write verification
                    // failed, retain the encrypted original for recovery.
                    if (!replacementOccurred)
                    {
                        DeleteBackupQuietly(backupPath);
                    }
                    else
                    {
                        undoRecord = new UndoRecord
                        {
                            RelativePath = current.RelativePath,
                            BeforeSha256 = current.Sha256,
                            AfterSha256 = replacementHash,
                            AfterIdentity = replacementIdentity,
                            BackupPath = backupPath
                        };
                        DeleteUndoRecordQuietly(previous);
                    }
                    throw;
                }

                undoRecord = new UndoRecord
                {
                    RelativePath = current.RelativePath,
                    BeforeSha256 = current.Sha256,
                    AfterSha256 = replacementHash,
                    AfterIdentity = replacementIdentity,
                    BackupPath = backupPath
                };
                baselines.Remove(current.RelativePath);
                DeleteUndoRecordQuietly(previous);
                return new WorkspaceEditResult
                {
                    RelativePath = current.RelativePath,
                    Sha256 = replacementHash,
                    BackupCreated = true,
                    UndoAvailable = true
                };
            }
        }

        private static void ValidateEdit(WorkspaceEdit edit)
        {
            if (edit == null)
            {
                throw new ArgumentNullException("edit");
            }
            if (!IsSha256(edit.BaseSha256))
            {
                throw new ArgumentException(
                    "编辑请求缺少有效的基准 SHA-256。",
                    "edit");
            }
            if (edit.NewText == null)
            {
                throw new ArgumentException(
                    "编辑后的文本不能为空引用。",
                    "edit");
            }
        }

        private PreparedEdit PrepareEditLocked(WorkspaceEdit edit)
        {
            string normalizedRelative = NormalizeRelativeFile(
                edit.RelativePath);
            BaselineRecord baseline;
            if (!baselines.TryGetValue(normalizedRelative, out baseline) ||
                !string.Equals(
                    baseline.Sha256,
                    edit.BaseSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "必须先重新读取文件，再应用基于该版本的修改。");
            }
            TextSnapshot current = ReadSnapshot(normalizedRelative);
            if (!string.Equals(
                current.Sha256,
                edit.BaseSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "文件已被其他程序修改。请重新读取后再编辑。");
            }
            if (!current.Identity.Equals(baseline.Identity))
            {
                throw new InvalidOperationException(
                    "文件已被替换。请重新读取后再编辑。");
            }

            string normalizedText = NormalizeEditedText(
                edit.NewText,
                current.NewLine,
                current.HasFinalNewLine,
                current.NormalizeNewLines);
            byte[] replacement = current.Codec.Encode(normalizedText);
            if (replacement.LongLength > MaximumTextFileBytes)
            {
                throw new InvalidOperationException(
                    "编辑后的文件超过 256 KiB，已拒绝写入。");
            }
            return new PreparedEdit
            {
                Current = current,
                NormalizedText = normalizedText,
                Replacement = replacement,
                ReplacementHash = ComputeSha256(replacement)
            };
        }

        public WorkspaceEditResult Undo(string relativePath)
        {
            lock (mutationLock)
            {
                ThrowIfDisposed();
                if (undoRecord == null)
                {
                    throw new InvalidOperationException("当前没有可撤销的代码修改。");
                }
                string normalized = NormalizeRelativeFile(relativePath);
                if (!string.Equals(
                    normalized,
                    undoRecord.RelativePath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "只能撤销最近一次修改的文件。");
                }

                TextSnapshot current = ReadSnapshot(normalized);
                if (!string.Equals(
                    current.Sha256,
                    undoRecord.AfterSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "文件在修改后又发生了变化，为避免覆盖新内容，已拒绝撤销。");
                }
                if (!current.Identity.Equals(undoRecord.AfterIdentity))
                {
                    throw new InvalidOperationException(
                        "文件在修改后已被替换，为避免覆盖新文件，已拒绝撤销。");
                }

                byte[] original = ReadEncryptedBackup(undoRecord.BackupPath);
                if (!string.Equals(
                    ComputeSha256(original),
                    undoRecord.BeforeSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("代码备份校验失败，已拒绝撤销。");
                }

                bool replacementOccurred;
                NativeIdentity replacementIdentity;
                ReplaceExistingFile(
                    current.RelativePath,
                    current.Identity,
                    current.Sha256,
                    original,
                    undoRecord.BeforeSha256,
                    out replacementOccurred,
                    out replacementIdentity);
                UndoRecord completed = undoRecord;
                undoRecord = null;
                baselines.Remove(current.RelativePath);
                DeleteUndoRecordQuietly(completed);
                return new WorkspaceEditResult
                {
                    RelativePath = current.RelativePath,
                    Sha256 = completed.BeforeSha256,
                    BackupCreated = false,
                    UndoAvailable = false
                };
            }
        }

        public void Dispose()
        {
            lock (mutationLock)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                if (rootHandle != null)
                {
                    rootHandle.Dispose();
                    rootHandle = null;
                }
                DeleteUndoRecordQuietly(undoRecord);
                undoRecord = null;
                baselines.Clear();
            }
        }

        private TextSnapshot ReadSnapshot(string relativePath)
        {
            string normalized = NormalizeRelativeFile(relativePath);
            string fullPath = ResolveRelativeFile(normalized);
            AssertRootIdentity();
            using (DirectoryLease lease = OpenDirectoryLease(normalized))
            using (NativeEntry entry = OpenAndVerifyNativeEntry(fullPath, false, true))
            {
                RejectUnsafeFileInformation(entry.Information);
                return ReadSnapshotFromHandle(
                    normalized,
                    entry.Handle,
                    entry.Information);
            }
        }

        private static TextSnapshot ReadSnapshotFromHandle(
            string relativePath,
            SafeFileHandle handle,
            ByHandleFileInformation information)
        {
            long length = CombineUnsigned(
                information.FileSizeHigh,
                information.FileSizeLow);
            if (length > MaximumTextFileBytes)
            {
                throw new InvalidOperationException(
                    "代码文件超过 256 KiB，已拒绝读取。");
            }

            byte[] raw = new byte[(int)length];
            using (FileStream stream = new FileStream(
                handle,
                FileAccess.Read,
                4096,
                false))
            {
                int offset = 0;
                while (offset < raw.Length)
                {
                    int count = stream.Read(raw, offset, raw.Length - offset);
                    if (count == 0)
                    {
                        throw new EndOfStreamException(
                            "读取代码文件时文件长度发生变化。");
                    }
                    offset += count;
                }
                if (stream.ReadByte() >= 0)
                {
                    throw new IOException("读取代码文件时文件长度发生变化。");
                }
            }

            TextCodec codec = TextCodec.Detect(raw);
            string text = codec.Decode(raw);
            if (text.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException("文件包含二进制 NUL 字节，不作为代码文本处理。");
            }
            for (int index = 0; index < text.Length; index++)
            {
                char value = text[index];
                if (value < 32 && value != '\t' && value != '\r' &&
                    value != '\n' && value != '\f')
                {
                    throw new InvalidDataException(
                        "文件包含二进制控制字符，不作为代码文本处理。");
                }
            }

            NewLineProfile newLine = DetectNewLine(text);
            return new TextSnapshot
            {
                RelativePath = relativePath,
                Text = text,
                RawBytes = raw,
                Sha256 = ComputeSha256(raw),
                Codec = codec,
                NewLine = newLine.Value,
                NormalizeNewLines = !newLine.IsMixed,
                HasFinalNewLine = EndsWithNewLine(text),
                Identity = NativeIdentity.From(information)
            };
        }

        private void ReplaceExistingFile(
            string relativePath,
            NativeIdentity expectedIdentity,
            string expectedHash,
            byte[] replacement,
            string replacementHash,
            out bool replacementOccurred,
            out NativeIdentity replacementIdentity)
        {
            replacementOccurred = false;
            replacementIdentity = new NativeIdentity();
            string fullPath = ResolveRelativeFile(relativePath);
            string directory = Path.GetDirectoryName(fullPath);
            OwnedTemporaryFile staging = new OwnedTemporaryFile(
                CreateTemporaryPath(directory));
            OwnedTemporaryFile rollback = new OwnedTemporaryFile(
                CreateTemporaryPath(directory));
            try
            {
                using (DirectoryLease lease = OpenDirectoryLease(relativePath))
                {
                    // Create the staging file only after all parent directory
                    // handles are leased, so a checked subdirectory cannot be
                    // swapped for a junction between validation and staging.
                    NativeIdentity stagedIdentity;
                    using (FileStream stagedStream = new FileStream(
                        staging.Path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.WriteThrough))
                    {
                        stagedStream.Write(replacement, 0, replacement.Length);
                        stagedStream.Flush(true);
                        ByHandleFileInformation createdInformation =
                            GetInformation(stagedStream.SafeFileHandle);
                        RejectUnsafeFileInformation(createdInformation);
                        if (!string.Equals(
                            TrimTrailingSeparators(Path.GetFullPath(staging.Path)),
                            GetFinalHandlePath(stagedStream.SafeFileHandle),
                            StringComparison.OrdinalIgnoreCase))
                        {
                            throw new UnauthorizedAccessException(
                                "工作区临时文件解析到了其他位置。");
                        }
                        stagedIdentity = NativeIdentity.From(createdInformation);
                        staging.Remember(stagedIdentity);
                    }

                    using (NativeEntry staged = OpenAndVerifyNativeEntry(
                        staging.Path,
                        false,
                        true,
                        0))
                    {
                        RejectUnsafeFileInformation(staged.Information);
                        if (!NativeIdentity.From(staged.Information).Equals(
                            stagedIdentity))
                        {
                            throw new IOException(
                                "临时代码文件在创建后已被替换，已拒绝写入。");
                        }
                        byte[] stagedBytes = ReadAllBytesFromHandle(
                            staged.Handle,
                            staged.Information);
                        if (!string.Equals(
                            ComputeSha256(stagedBytes),
                            replacementHash,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException("临时代码文件写入校验失败。");
                        }
                    }

                    using (NativeEntry current = OpenAndVerifyNativeEntry(
                        fullPath,
                        false,
                        true,
                        0))
                    {
                        RejectUnsafeFileInformation(current.Information);
                        if ((current.Information.FileAttributes &
                            FileAttributeReadOnly) != 0)
                        {
                            throw new UnauthorizedAccessException(
                                "目标代码文件是只读文件，已拒绝修改。" +
                                "请先由用户解除只读属性，再重新读取和确认修改。");
                        }
                        NativeIdentity actualIdentity =
                            NativeIdentity.From(current.Information);
                        if (!actualIdentity.Equals(expectedIdentity))
                        {
                            throw new InvalidOperationException(
                                "文件已被替换。请重新读取后再编辑。");
                        }
                        byte[] currentBytes = ReadAllBytesFromHandle(
                            current.Handle,
                            current.Information);
                        if (!string.Equals(
                            ComputeSha256(currentBytes),
                            expectedHash,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "文件已被其他程序修改。请重新读取后再编辑。");
                        }
                    }

                    // The file handle denies delete sharing and must close
                    // before replacement. Parent directory handles remain
                    // leased across the atomic operation. ReplaceFile preserves
                    // the target metadata that Windows can merge without using
                    // an ignore-errors flag. Its rollback file identifies exactly
                    // which object was replaced across the unavoidable path API
                    // race between closing the verified handle and replacement.
                    ReplaceFileCallResult replaceResult = InvokeReplaceFile(
                        fullPath,
                        staging.Path,
                        rollback.Path);
                    if (!replaceResult.Succeeded)
                    {
                        // Until the documented post-failure naming state is
                        // proven, the staged object may be the only intact
                        // recovery copy of the proposed bytes.
                        staging.Preserve = true;
                        try
                        {
                            HandleInitialReplaceFailure(
                                replaceResult.ErrorCode,
                                fullPath,
                                lease.ParentHandle,
                                staging,
                                rollback,
                                stagedIdentity,
                                replacementHash,
                                expectedIdentity,
                                expectedHash);
                        }
                        catch
                        {
                            // The native state could not be proven or restored.
                            // Retain the encrypted backup owned by the caller.
                            replacementOccurred = true;
                            throw;
                        }
                        staging.Preserve = false;
                        throw CreateNativeIOException(
                            replaceResult.ErrorCode,
                            "无法原子替换代码文件；原文件未被修改或已经恢复");
                    }
                    replacementOccurred = true;
                    replacementIdentity = stagedIdentity;

                    bool rollbackMatchesExpected;
                    NativeIdentity rollbackIdentity;
                    using (NativeEntry rollbackEntry = OpenAndVerifyNativeEntry(
                        rollback.Path,
                        false,
                        true,
                        0))
                    {
                        RejectUnsafeFileInformation(rollbackEntry.Information);
                        byte[] replacedBytes = ReadAllBytesFromHandle(
                            rollbackEntry.Handle,
                            rollbackEntry.Information);
                        rollbackIdentity = NativeIdentity.From(
                            rollbackEntry.Information);
                        rollbackMatchesExpected =
                            rollbackIdentity.Equals(expectedIdentity) &&
                            string.Equals(
                                ComputeSha256(replacedBytes),
                                expectedHash,
                                StringComparison.OrdinalIgnoreCase);
                        if (rollbackMatchesExpected)
                        {
                            // The API moved the exact verified original object
                            // to the rollback name. It is now safe to claim that
                            // temporary name for identity-checked cleanup.
                            rollback.Remember(rollbackIdentity);
                            rollback.Preserve = true;
                        }
                    }

                    if (!rollbackMatchesExpected)
                    {
                        rollback.Preserve = true;
                        RestoreUnexpectedReplacement(
                            fullPath,
                            lease.ParentHandle,
                            rollback,
                            stagedIdentity,
                            replacementHash,
                            directory);
                        rollback.Preserve = false;
                        replacementOccurred = false;
                        throw new InvalidOperationException(
                            "文件在最终替换前已被其他程序换入。已恢复该文件，" +
                            "本次修改没有写入；请重新读取后再编辑。");
                    }

                    using (NativeEntry writtenEntry = OpenAndVerifyNativeEntry(
                        fullPath,
                        false,
                        true,
                        0))
                    {
                        RejectUnsafeFileInformation(writtenEntry.Information);
                        byte[] writtenBytes = ReadAllBytesFromHandle(
                            writtenEntry.Handle,
                            writtenEntry.Information);
                        if (!NativeIdentity.From(writtenEntry.Information).Equals(
                                stagedIdentity) ||
                            !string.Equals(
                                ComputeSha256(writtenBytes),
                                replacementHash,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException(
                                "写入后的代码文件身份或内容校验失败。");
                        }
                        replacementIdentity = NativeIdentity.From(
                            writtenEntry.Information);
                    }

                    // The rollback copy is deleted only after the final target
                    // has been proven to be the staged object with exact bytes.
                    rollback.Preserve = false;
                    if (!DeleteOwnedTemporaryFile(rollback))
                    {
                        throw new IOException(
                            "原文件回滚副本的身份已变化，已保留该路径且拒绝误删。");
                    }
                }
            }
            finally
            {
                DeleteOwnedTemporaryFileQuietly(staging);
                DeleteOwnedTemporaryFileQuietly(rollback);
            }
        }

        private static void HandleInitialReplaceFailure(
            int error,
            string fullPath,
            SafeFileHandle parentDirectoryHandle,
            OwnedTemporaryFile staging,
            OwnedTemporaryFile rollback,
            NativeIdentity stagedIdentity,
            string stagedHash,
            NativeIdentity expectedIdentity,
            string expectedHash)
        {
            if (error == ErrorUnableToMoveReplacement2)
            {
                staging.Preserve = true;
                rollback.Preserve = true;
                try
                {
                    VerifyOwnedFileIdentityAndHash(
                        staging,
                        stagedIdentity,
                        stagedHash,
                        "原子替换部分失败后暂存代码文件状态异常。");
                    RememberAndVerifyOwnedFile(
                        rollback,
                        expectedIdentity,
                        expectedHash,
                        "原子替换发生部分失败，回滚文件身份不匹配。");
                    RestoreOwnedFileToEmptyTarget(
                        rollback,
                        fullPath,
                        parentDirectoryHandle,
                        expectedIdentity,
                        expectedHash);
                    // Clear preservation only after the original object is
                    // back at the target name and has passed identity/hash
                    // verification. Any exception leaves both copies intact.
                    rollback.Preserve = false;
                    staging.Preserve = false;
                }
                catch
                {
                    staging.Preserve = true;
                    rollback.Preserve = true;
                    throw;
                }
                return;
            }

            // With a backup name supplied, Windows documents 1175 and 1176 as
            // leaving both original names intact. Other errors have the same
            // documented naming state. Prove it before claiming no change.
            VerifyFileIdentityAndHash(
                fullPath,
                expectedIdentity,
                expectedHash,
                error == ErrorUnableToRemoveReplaced ||
                    error == ErrorUnableToMoveReplacement
                    ? "原子替换失败后目标文件状态与系统返回值不一致。"
                    : "原子替换失败后无法证明目标文件仍保持原状。");
            VerifyOwnedFileIdentityAndHash(
                staging,
                stagedIdentity,
                stagedHash,
                error == ErrorUnableToRemoveReplaced ||
                    error == ErrorUnableToMoveReplacement
                    ? "原子替换失败后暂存文件状态与系统返回值不一致。"
                    : "原子替换失败后无法证明暂存文件仍保持原状。");
        }

        private static void RestoreOwnedFileToEmptyTarget(
            OwnedTemporaryFile source,
            string fullPath,
            SafeFileHandle parentDirectoryHandle,
            NativeIdentity expectedIdentity,
            string expectedHash)
        {
            using (NativeEntry owned = OpenOwnedFileForMutation(
                source.Path,
                true))
            {
                RejectUnsafeFileInformation(owned.Information);
                NativeIdentity actual = NativeIdentity.From(owned.Information);
                if (!source.HasIdentity ||
                    !actual.Equals(source.Identity) ||
                    !actual.Equals(expectedIdentity) ||
                    !string.Equals(
                        ComputeSha256(ReadAllBytesFromHandle(
                            owned.Handle,
                            owned.Information)),
                        expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "恢复文件在移动前发生变化，已保留该文件并停止恢复。");
                }
                // FILE_RENAME_INFO uses ReplaceIfExists=false, so an object
                // arriving at the target path cannot be overwritten.
                RenameFileByHandle(
                    owned.Handle,
                    parentDirectoryHandle,
                    fullPath);
            }
            VerifyFileIdentityAndHash(
                fullPath,
                expectedIdentity,
                expectedHash,
                "原子替换失败后的原文件恢复校验失败。");
        }

        private static void RestoreUnexpectedReplacement(
            string fullPath,
            SafeFileHandle parentDirectoryHandle,
            OwnedTemporaryFile rollback,
            NativeIdentity stagedIdentity,
            string stagedHash,
            string directory)
        {
            OwnedTemporaryFile rejected = new OwnedTemporaryFile(
                CreateTemporaryPath(directory));
            rejected.Preserve = true;
            try
            {
                VerifyFileIdentityAndHash(
                    fullPath,
                    stagedIdentity,
                    stagedHash,
                    "检测到文件竞态后暂存代码文件状态异常。");

                NativeIdentity unexpectedIdentity;
                string unexpectedHash;
                using (NativeEntry unexpected = OpenAndVerifyNativeEntry(
                    rollback.Path,
                    false,
                    true,
                    0))
                {
                    RejectUnsafeFileInformation(unexpected.Information);
                    unexpectedIdentity = NativeIdentity.From(
                        unexpected.Information);
                    unexpectedHash = ComputeSha256(ReadAllBytesFromHandle(
                        unexpected.Handle,
                        unexpected.Information));
                }
                rollback.Remember(unexpectedIdentity);
                rollback.Preserve = true;

                ReplaceFileCallResult reverseResult = InvokeReplaceFile(
                    fullPath,
                    rollback.Path,
                    rejected.Path);
                if (!reverseResult.Succeeded)
                {
                    if (reverseResult.ErrorCode == ErrorUnableToMoveReplacement2)
                    {
                        RememberAndVerifyOwnedFile(
                            rejected,
                            stagedIdentity,
                            stagedHash,
                            "反向恢复部分失败，暂存代码副本身份不匹配。");
                        RememberAndVerifyOwnedFile(
                            rollback,
                            unexpectedIdentity,
                            unexpectedHash,
                            "反向恢复部分失败，用户文件副本身份不匹配。");
                        RestoreOwnedFileToEmptyTarget(
                            rollback,
                            fullPath,
                            parentDirectoryHandle,
                            unexpectedIdentity,
                            unexpectedHash);
                        rollback.Preserve = false;
                        rejected.Preserve = false;
                        DeleteOwnedTemporaryFileQuietly(rejected);
                        return;
                    }

                    // 1175 and 1176 explicitly retain both original names;
                    // other errors have the same documented naming state.
                    // Prove both objects before leaving the user's file in the
                    // preserved rollback path for manual recovery.
                    VerifyFileIdentityAndHash(
                        fullPath,
                        stagedIdentity,
                        stagedHash,
                        "反向恢复失败后暂存代码文件状态异常。");
                    RememberAndVerifyOwnedFile(
                        rollback,
                        unexpectedIdentity,
                        unexpectedHash,
                        "反向恢复失败后用户文件副本状态异常。");
                    throw CreateNativeIOException(
                        reverseResult.ErrorCode,
                        "检测到文件竞态，但无法自动恢复被换入的文件");
                }

                RememberAndVerifyOwnedFile(
                    rejected,
                    stagedIdentity,
                    stagedHash,
                    "反向恢复生成的暂存副本身份不匹配。");
                VerifyFileIdentityAndHash(
                    fullPath,
                    unexpectedIdentity,
                    unexpectedHash,
                    "反向恢复后的用户文件身份或内容校验失败。");
                rollback.Preserve = false;
                rejected.Preserve = false;
                DeleteOwnedTemporaryFileQuietly(rejected);
            }
            finally
            {
                DeleteOwnedTemporaryFileQuietly(rejected);
            }
        }

        private static void VerifyOwnedFileIdentityAndHash(
            OwnedTemporaryFile file,
            NativeIdentity expectedIdentity,
            string expectedHash,
            string message)
        {
            using (NativeEntry entry = OpenAndVerifyNativeEntry(
                file.Path,
                false,
                true,
                0))
            {
                RejectUnsafeFileInformation(entry.Information);
                NativeIdentity identity = NativeIdentity.From(entry.Information);
                if (!file.HasIdentity ||
                    !identity.Equals(file.Identity) ||
                    !identity.Equals(expectedIdentity) ||
                    !string.Equals(
                        ComputeSha256(ReadAllBytesFromHandle(
                            entry.Handle,
                            entry.Information)),
                        expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(message);
                }
            }
        }

        private static void RememberAndVerifyOwnedFile(
            OwnedTemporaryFile file,
            NativeIdentity expectedIdentity,
            string expectedHash,
            string message)
        {
            using (NativeEntry entry = OpenAndVerifyNativeEntry(
                file.Path,
                false,
                true,
                0))
            {
                RejectUnsafeFileInformation(entry.Information);
                NativeIdentity identity = NativeIdentity.From(entry.Information);
                file.Remember(identity);
                if (!identity.Equals(expectedIdentity) ||
                    !string.Equals(
                        ComputeSha256(ReadAllBytesFromHandle(
                            entry.Handle,
                            entry.Information)),
                        expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(message + "已保留恢复文件。");
                }
            }
        }

        private static void VerifyFileIdentityAndHash(
            string path,
            NativeIdentity expectedIdentity,
            string expectedHash,
            string message)
        {
            using (NativeEntry entry = OpenAndVerifyNativeEntry(
                path,
                false,
                true,
                0))
            {
                RejectUnsafeFileInformation(entry.Information);
                if (!NativeIdentity.From(entry.Information).Equals(expectedIdentity) ||
                    !string.Equals(
                        ComputeSha256(ReadAllBytesFromHandle(
                            entry.Handle,
                            entry.Information)),
                        expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(message);
                }
            }
        }

        private static bool DeleteOwnedTemporaryFile(OwnedTemporaryFile file)
        {
            if (file == null || !file.HasIdentity || file.Preserve)
            {
                return false;
            }
            Action<string> hook = BeforeOwnedTemporaryDeleteForTest;
            if (hook != null)
            {
                hook(file.Path);
            }

            NativeEntry entry = null;
            try
            {
                entry = OpenOwnedFileForMutation(file.Path, false);
            }
            catch (IOException exception)
            {
                Win32Exception native = exception.InnerException as Win32Exception;
                if (native != null &&
                    (native.NativeErrorCode == ErrorFileNotFound ||
                     native.NativeErrorCode == ErrorPathNotFound))
                {
                    return true;
                }
                throw;
            }
            using (entry)
            {
                RejectUnsafeFileInformation(entry.Information);
                if (!NativeIdentity.From(entry.Information).Equals(file.Identity))
                {
                    return false;
                }
                FileDispositionInformation disposition =
                    new FileDispositionInformation { DeleteFile = 1 };
                if (!SetFileInformationByHandle(
                    entry.Handle,
                    FileDispositionInfo,
                    ref disposition,
                    (uint)Marshal.SizeOf(typeof(FileDispositionInformation))))
                {
                    throw CreateNativeIOException(
                        Marshal.GetLastWin32Error(),
                        "无法清理工作区临时文件");
                }
                return true;
            }
        }

        private static void DeleteOwnedTemporaryFileQuietly(
            OwnedTemporaryFile file)
        {
            try
            {
                DeleteOwnedTemporaryFile(file);
            }
            catch
            {
            }
        }

        private static ReplaceFileCallResult InvokeReplaceFile(
            string replacedFileName,
            string replacementFileName,
            string backupFileName)
        {
            ReplaceFileTestInvoker testInvoker = ReplaceFileInvokerForTest;
            if (testInvoker != null)
            {
                return testInvoker(
                    replacedFileName,
                    replacementFileName,
                    backupFileName);
            }

            bool succeeded = ReplaceFile(
                replacedFileName,
                replacementFileName,
                backupFileName,
                ReplaceFileNoFlags,
                IntPtr.Zero,
                IntPtr.Zero);
            return new ReplaceFileCallResult(
                succeeded,
                succeeded ? 0 : Marshal.GetLastWin32Error());
        }

        private static NativeEntry OpenOwnedFileForMutation(
            string path,
            bool readContent)
        {
            uint access = DeleteAccess | FileReadAttributes;
            if (readContent)
            {
                access |= GenericRead;
            }
            SafeFileHandle handle = CreateFile(
                path,
                access,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw CreateNativeIOException(
                    error,
                    "无法安全打开工作区临时文件");
            }

            try
            {
                ByHandleFileInformation information = GetInformation(handle);
                if ((information.FileAttributes &
                        (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                {
                    throw new UnauthorizedAccessException(
                        "工作区拒绝通过重解析点清理临时文件。");
                }
                string expected = TrimTrailingSeparators(Path.GetFullPath(path));
                string final = GetFinalHandlePath(handle);
                if (!string.Equals(
                    expected,
                    final,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException(
                        "工作区临时文件解析到了其他位置，已拒绝修改。");
                }
                return new NativeEntry(handle, information);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static void RenameFileByHandle(
            SafeFileHandle handle,
            SafeFileHandle parentDirectoryHandle,
            string destinationPath)
        {
            string nativeDestination = Path.GetFullPath(destinationPath);
            string nativeParent = TrimTrailingSeparators(
                Path.GetDirectoryName(nativeDestination));
            string verifiedParent = GetFinalHandlePath(parentDirectoryHandle);
            if (!string.Equals(
                nativeParent,
                verifiedParent,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    "恢复目标的父目录与已核验目录不一致。");
            }

            string leafName = Path.GetFileName(nativeDestination);
            if (string.IsNullOrEmpty(leafName) ||
                leafName.IndexOfAny(new char[] { '\\', '/' }) >= 0)
            {
                throw new UnauthorizedAccessException("恢复目标文件名无效。");
            }

            byte[] name = Encoding.Unicode.GetBytes(nativeDestination);
            int rootOffset = IntPtr.Size == 8 ? 8 : 4;
            int lengthOffset = rootOffset + IntPtr.Size;
            int nameOffset = lengthOffset + 4;
            int bufferLength = nameOffset + name.Length + 2;
            IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                for (int index = 0; index < bufferLength; index++)
                {
                    Marshal.WriteByte(buffer, index, 0);
                }
                Marshal.WriteByte(buffer, 0, 0); // ReplaceIfExists = FALSE
                // The Win32 contract accepts an absolute DOS path when
                // RootDirectory is NULL. FileNameLength excludes the required
                // UTF-16 terminator, which remains zeroed after the copied name.
                Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
                Marshal.WriteInt32(buffer, lengthOffset, name.Length);
                Marshal.Copy(name, 0, new IntPtr(buffer.ToInt64() + nameOffset),
                    name.Length);
                if (!SetFileInformationByHandleBuffer(
                    handle,
                    FileRenameInfo,
                    buffer,
                    (uint)bufferLength))
                {
                    throw CreateNativeIOException(
                        Marshal.GetLastWin32Error(),
                        "无法把恢复文件移回原路径");
                }
                if (!string.Equals(
                    nativeDestination,
                    GetFinalHandlePath(handle),
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "系统报告恢复成功，但文件句柄未指向预期目标路径。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static byte[] ReadAllBytesFromHandle(
            SafeFileHandle handle,
            ByHandleFileInformation information)
        {
            long length = CombineUnsigned(
                information.FileSizeHigh,
                information.FileSizeLow);
            if (length > MaximumTextFileBytes)
            {
                throw new InvalidOperationException("代码文件超过 256 KiB。");
            }
            byte[] result = new byte[(int)length];
            using (SafeFileHandle borrowed = new SafeFileHandle(
                handle.DangerousGetHandle(),
                false))
            using (FileStream stream = new FileStream(
                borrowed,
                FileAccess.Read,
                4096,
                false))
            {
                int offset = 0;
                while (offset < result.Length)
                {
                    int count = stream.Read(result, offset, result.Length - offset);
                    if (count <= 0)
                    {
                        throw new EndOfStreamException("文件读取不完整。");
                    }
                    offset += count;
                }
            }
            return result;
        }

        private string CreateEncryptedBackup(byte[] original)
        {
            byte[] encrypted = ProtectedData.Protect(
                original,
                BackupEntropy,
                DataProtectionScope.CurrentUser);
            string directory = Path.Combine(
                AppDataPath.Root,
                "WorkspaceBackups");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                Guid.NewGuid().ToString("N") + ".fpwbak");
            AtomicFile.WriteAllBytes(path, encrypted);
            return path;
        }

        private static byte[] ReadEncryptedBackup(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("找不到代码撤销备份。", path);
            }
            return ProtectedData.Unprotect(
                File.ReadAllBytes(path),
                BackupEntropy,
                DataProtectionScope.CurrentUser);
        }

        private static void DeleteUndoRecordQuietly(UndoRecord record)
        {
            if (record != null)
            {
                DeleteBackupQuietly(record.BackupPath);
            }
        }

        private static void DeleteBackupQuietly(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private bool EnumerateFilesSafely(
            string directoryPath,
            string relativeDirectory,
            int depth,
            Func<string, string, EnumeratedFileInfo, bool> visitor)
        {
            if (depth > MaximumDirectoryDepth)
            {
                throw new InvalidOperationException("工作区目录层级超过安全上限。");
            }

            string[] files;
            string[] directories;
            using (DirectoryLease lease = OpenDirectoryTreeLease(
                relativeDirectory))
            {
                files = Directory.GetFiles(directoryPath);
                directories = Directory.GetDirectories(directoryPath);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);

                for (int index = 0; index < files.Length; index++)
                {
                    string name = Path.GetFileName(files[index]);
                    string relative = CombineRelative(relativeDirectory, name);
                    try
                    {
                        ValidateRelativePath(relative, false);
                        EnumeratedFileInfo safeInfo;
                        using (NativeEntry entry = OpenAndVerifyNativeEntry(
                            files[index],
                            false,
                            false))
                        {
                            RejectUnsafeFileInformation(entry.Information);
                            safeInfo = new EnumeratedFileInfo
                            {
                                Length = CombineUnsigned(
                                    entry.Information.FileSizeHigh,
                                    entry.Information.FileSizeLow),
                                LastWriteUtc = NativeFileTimeToUtc(
                                    entry.Information.LastWriteTime)
                            };
                        }
                        if (!visitor(files[index], relative, safeInfo))
                        {
                            return false;
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }

                for (int index = 0; index < directories.Length; index++)
                {
                    string name = Path.GetFileName(directories[index]);
                    string relative = CombineRelative(relativeDirectory, name);
                    try
                    {
                        ValidateRelativePath(relative, true);
                        using (NativeEntry entry = OpenAndVerifyNativeEntry(
                            directories[index],
                            true,
                            false))
                        {
                        }
                        if (!EnumerateFilesSafely(
                            directories[index],
                            relative,
                            depth + 1,
                            visitor))
                        {
                            return false;
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            return true;
        }

        private static void AppendMatches(
            IList<WorkspaceSearchResult> output,
            string text,
            string relativePath,
            string query,
            int limit)
        {
            using (StringReader reader = new StringReader(text))
            {
                int lineNumber = 0;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    string display = line.Length > MaximumSearchLineCharacters
                        ? line.Substring(0, MaximumSearchLineCharacters)
                        : line;
                    output.Add(new WorkspaceSearchResult
                    {
                        RelativePath = relativePath,
                        LineNumber = lineNumber,
                        LineText = display
                    });
                    if (output.Count >= limit)
                    {
                        return;
                    }
                }
            }
        }

        private void RememberBaseline(TextSnapshot snapshot)
        {
            lock (mutationLock)
            {
                ThrowIfDisposed();
                BaselineRecord existing;
                if (baselines.TryGetValue(snapshot.RelativePath, out existing) &&
                    string.Equals(
                        existing.Sha256,
                        snapshot.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // A second read of identical bytes must not bless a file
                    // that was deleted and recreated between the model's read
                    // and the confirmation flow. A changed hash establishes a
                    // new explicit baseline; an unchanged hash retains the
                    // first observed Windows file identity.
                    return;
                }
                if (!baselines.ContainsKey(snapshot.RelativePath) &&
                    baselines.Count >= MaximumConcurrentBaselines)
                {
                    throw new InvalidOperationException(
                        "本次代码工作区读取的文件过多。请关闭工作区后重新选择文件。");
                }
                baselines[snapshot.RelativePath] = new BaselineRecord
                {
                    Sha256 = snapshot.Sha256,
                    Identity = snapshot.Identity
                };
            }
        }

        private DirectoryLease OpenDirectoryLease(string relativeFile)
        {
            string normalized = NormalizeRelativeFile(relativeFile);
            string parent = Path.GetDirectoryName(normalized) ?? string.Empty;
            List<SafeFileHandle> handles = new List<SafeFileHandle>();
            try
            {
                NativeEntry root = OpenAndVerifyNativeEntry(rootPath, true, false);
                handles.Add(root.Handle);
                root.Handle = null;
                if (!NativeIdentity.From(root.Information).Equals(rootIdentity))
                {
                    throw new InvalidOperationException(
                        "工作区根目录已经发生变化，请重新选择文件。");
                }

                if (!string.IsNullOrEmpty(parent))
                {
                    string current = rootPath;
                    string[] parts = parent.Split('\\');
                    for (int index = 0; index < parts.Length; index++)
                    {
                        current = Path.Combine(current, parts[index]);
                        NativeEntry entry = OpenAndVerifyNativeEntry(
                            current,
                            true,
                            false);
                        handles.Add(entry.Handle);
                        entry.Handle = null;
                    }
                }
                return new DirectoryLease(handles);
            }
            catch
            {
                for (int index = handles.Count - 1; index >= 0; index--)
                {
                    handles[index].Dispose();
                }
                throw;
            }
        }

        private DirectoryLease OpenDirectoryTreeLease(
            string relativeDirectory)
        {
            string normalized = NormalizeRelativeDirectory(relativeDirectory);
            List<SafeFileHandle> handles = new List<SafeFileHandle>();
            try
            {
                NativeEntry root = OpenAndVerifyNativeEntry(rootPath, true, false);
                handles.Add(root.Handle);
                root.Handle = null;
                if (!NativeIdentity.From(root.Information).Equals(rootIdentity))
                {
                    throw new InvalidOperationException(
                        "工作区根目录已经发生变化，请重新选择文件。");
                }

                if (!string.IsNullOrEmpty(normalized))
                {
                    string current = rootPath;
                    string[] parts = normalized.Split('\\');
                    for (int index = 0; index < parts.Length; index++)
                    {
                        current = Path.Combine(current, parts[index]);
                        NativeEntry entry = OpenAndVerifyNativeEntry(
                            current,
                            true,
                            false);
                        handles.Add(entry.Handle);
                        entry.Handle = null;
                    }
                }
                return new DirectoryLease(handles);
            }
            catch
            {
                for (int index = handles.Count - 1; index >= 0; index--)
                {
                    handles[index].Dispose();
                }
                throw;
            }
        }

        private void AssertRootIdentity()
        {
            ThrowIfDisposed();
            ByHandleFileInformation information = GetInformation(rootHandle);
            if ((information.FileAttributes & FileAttributeReparsePoint) != 0 ||
                !NativeIdentity.From(information).Equals(rootIdentity))
            {
                throw new InvalidOperationException(
                    "工作区根目录已经发生变化，请重新选择文件。");
            }
        }

        private string ResolveRelativeFile(string relativePath)
        {
            string normalized = NormalizeRelativeFile(relativePath);
            string fullPath = Path.GetFullPath(Path.Combine(rootPath, normalized));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                fullPath.Length > MaximumPathCharacters)
            {
                throw new UnauthorizedAccessException("路径超出已授权的代码目录。");
            }
            return fullPath;
        }

        private string ResolveRelativeDirectory(string relativeDirectory)
        {
            if (string.IsNullOrEmpty(relativeDirectory))
            {
                return rootPath;
            }
            string fullPath = Path.GetFullPath(
                Path.Combine(rootPath, relativeDirectory));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                fullPath.Length > MaximumPathCharacters)
            {
                throw new UnauthorizedAccessException("目录超出已授权的代码目录。");
            }
            return fullPath;
        }

        private static string NormalizeRelativeFile(string relativePath)
        {
            ValidateRelativePath(relativePath, false);
            return relativePath.Replace('/', '\\');
        }

        private static string NormalizeRelativeDirectory(string relativeDirectory)
        {
            if (string.IsNullOrWhiteSpace(relativeDirectory))
            {
                return string.Empty;
            }
            ValidateRelativePath(relativeDirectory, true);
            return relativeDirectory.Replace('/', '\\');
        }

        private static void ValidateRelativePath(string value, bool directory)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (directory)
                {
                    return;
                }
                throw new ArgumentException("必须提供相对文件路径。", "value");
            }
            if (value.Length > MaximumPathCharacters ||
                Path.IsPathRooted(value) ||
                value.StartsWith("\\", StringComparison.Ordinal) ||
                value.StartsWith("/", StringComparison.Ordinal) ||
                value.IndexOf(':') >= 0)
            {
                throw new UnauthorizedAccessException("只允许工作区内的相对路径。");
            }

            string normalized = value.Replace('/', '\\');
            string[] parts = normalized.Split('\\');
            for (int index = 0; index < parts.Length; index++)
            {
                ValidatePathSegment(parts[index]);
            }
        }

        private static void ValidatePathSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == ".." ||
                segment.EndsWith(" ", StringComparison.Ordinal) ||
                segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new UnauthorizedAccessException("相对路径包含不安全的名称。");
            }
            for (int index = 0; index < segment.Length; index++)
            {
                if (segment[index] < 32)
                {
                    throw new UnauthorizedAccessException("相对路径包含控制字符。");
                }
            }
            if (IsReservedDeviceName(segment) ||
                IsProtectedWorkspaceName(segment) ||
                IsInternalWorkspaceFileName(segment))
            {
                throw new UnauthorizedAccessException("相对路径包含受保护的系统名称。");
            }
        }

        private static bool IsInternalWorkspaceFileName(string segment)
        {
            string lower = segment.ToLowerInvariant();
            return lower.EndsWith(".fpw.tmp", StringComparison.Ordinal) ||
                lower.EndsWith(".fpwbak", StringComparison.Ordinal);
        }

        private static bool IsProtectedWorkspaceName(string segment)
        {
            string lower = segment.ToLowerInvariant();
            return string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".svn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".hg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".vs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "packages", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "vendor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".ssh", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".gnupg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".aws", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".azure", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".kube", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".npmrc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".pypirc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".netrc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, ".git-credentials", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "credentials", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "credentials.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "secrets.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "id_rsa", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "id_dsa", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "id_ed25519", StringComparison.OrdinalIgnoreCase) ||
                lower == ".env" || lower.StartsWith(".env.", StringComparison.Ordinal) ||
                lower.EndsWith(".pem", StringComparison.Ordinal) ||
                lower.EndsWith(".key", StringComparison.Ordinal) ||
                lower.EndsWith(".pfx", StringComparison.Ordinal) ||
                lower.EndsWith(".p12", StringComparison.Ordinal) ||
                lower.EndsWith(".ppk", StringComparison.Ordinal) ||
                lower.EndsWith(".kdbx", StringComparison.Ordinal) ||
                lower.EndsWith(".jks", StringComparison.Ordinal) ||
                lower.EndsWith(".keystore", StringComparison.Ordinal);
        }

        private static bool IsReservedDeviceName(string segment)
        {
            string baseName = segment;
            int dot = baseName.IndexOf('.');
            if (dot >= 0)
            {
                baseName = baseName.Substring(0, dot);
            }
            string upper = baseName.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" ||
                upper == "NUL" || upper == "CLOCK$" || upper == "CONIN$" ||
                upper == "CONOUT$")
            {
                return true;
            }
            if (upper.Length == 4 &&
                (upper.StartsWith("COM", StringComparison.Ordinal) ||
                 upper.StartsWith("LPT", StringComparison.Ordinal)))
            {
                char suffix = upper[3];
                return (suffix >= '1' && suffix <= '9') ||
                    suffix == '\u00B9' || suffix == '\u00B2' || suffix == '\u00B3';
            }
            return false;
        }

        private static void RejectNonLocalAbsolutePath(string path)
        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal) ||
                path.StartsWith("\\?\\", StringComparison.Ordinal) ||
                path.StartsWith("\\.\\", StringComparison.Ordinal) ||
                path.StartsWith("\\??\\", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "不支持网络路径、设备路径或扩展路径。");
            }
        }

        private static bool IsDriveLetterAbsolutePath(string path)
        {
            return path != null && path.Length >= 3 &&
                char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/');
        }

        private static void ValidateAbsoluteComponents(string fullFilePath)
        {
            string volumeRoot = Path.GetPathRoot(fullFilePath);
            string current = TrimTrailingSeparators(volumeRoot);
            string remaining = fullFilePath.Substring(volumeRoot.Length);
            string[] parts = remaining.Split(new char[] { '\\', '/' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                ValidatePathSegment(parts[index]);
                current = Path.Combine(current + Path.DirectorySeparatorChar, parts[index]);
                bool directory = index < parts.Length - 1;
                using (NativeEntry entry = OpenAndVerifyNativeEntry(
                    current,
                    directory,
                    !directory))
                {
                    if (!directory)
                    {
                        RejectUnsafeFileInformation(entry.Information);
                    }
                }
            }
        }

        private static NativeEntry OpenAndVerifyNativeEntry(
            string path,
            bool directory,
            bool readContent)
        {
            return OpenAndVerifyNativeEntry(
                path,
                directory,
                readContent,
                FileShareRead | FileShareWrite);
        }

        private static NativeEntry OpenAndVerifyNativeEntry(
            string path,
            bool directory,
            bool readContent,
            uint shareMode)
        {
            uint desiredAccess = FileReadAttributes;
            if (readContent)
            {
                desiredAccess |= GenericRead;
            }
            uint flags = FileFlagOpenReparsePoint;
            flags |= directory ? FileFlagBackupSemantics : FileFlagSequentialScan;
            SafeFileHandle handle = CreateFile(
                path,
                desiredAccess,
                shareMode,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw CreateNativeIOException(error, "无法安全打开工作区路径");
            }

            try
            {
                ByHandleFileInformation information = GetInformation(handle);
                bool actualDirectory =
                    (information.FileAttributes & FileAttributeDirectory) != 0;
                if (actualDirectory != directory ||
                    (information.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException(
                        "工作区拒绝目录联接、符号链接和其他重解析点。");
                }
                string expected = TrimTrailingSeparators(Path.GetFullPath(path));
                string final = GetFinalHandlePath(handle);
                if (!string.Equals(expected, final, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException(
                        "工作区路径解析到了其他位置，已拒绝访问。");
                }
                return new NativeEntry(handle, information);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static void RejectUnsafeFileInformation(
            ByHandleFileInformation information)
        {
            if ((information.FileAttributes & FileAttributeDirectory) != 0 ||
                (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("目标不是普通文件。");
            }
            if (information.NumberOfLinks != 1)
            {
                throw new UnauthorizedAccessException(
                    "为防止越过目录边界，工作区不处理硬链接文件。");
            }
        }

        private static ByHandleFileInformation GetInformation(
            SafeFileHandle handle)
        {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                throw CreateNativeIOException(
                    Marshal.GetLastWin32Error(),
                    "无法读取工作区文件身份");
            }
            return information;
        }

        private static string GetFinalHandlePath(SafeFileHandle handle)
        {
            uint capacity = 512;
            StringBuilder value = new StringBuilder((int)capacity);
            uint length = GetFinalPathNameByHandle(handle, value, capacity, 0);
            if (length == 0)
            {
                throw CreateNativeIOException(
                    Marshal.GetLastWin32Error(),
                    "无法解析工作区最终路径");
            }
            if (length >= capacity)
            {
                capacity = length + 1;
                value = new StringBuilder((int)capacity);
                length = GetFinalPathNameByHandle(handle, value, capacity, 0);
                if (length == 0 || length >= capacity)
                {
                    throw CreateNativeIOException(
                        Marshal.GetLastWin32Error(),
                        "无法解析完整工作区路径");
                }
            }

            string path = value.ToString();
            if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            {
                path = "\\\\" + path.Substring(8);
            }
            else if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(4);
            }
            return TrimTrailingSeparators(Path.GetFullPath(path));
        }

        private static string GetStrictRelativePath(string root, string fullPath)
        {
            string prefix = root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("文件不在已授权目录内。");
            }
            return fullPath.Substring(prefix.Length);
        }

        private static string CombineRelative(string directory, string name)
        {
            return string.IsNullOrEmpty(directory)
                ? name
                : directory + "\\" + name;
        }

        private static string CreateTemporaryPath(string directory)
        {
            const string suffix = ".fpw.tmp";
            string path = Path.Combine(
                directory,
                ".fpw-" + Guid.NewGuid().ToString("N") + suffix);
            if (path.Length >= 260)
            {
                throw new PathTooLongException(
                    "代码文件路径过长，无法在 Windows 7 上安全创建临时文件。");
            }
            return path;
        }

        private static string TrimTrailingSeparators(string path)
        {
            return path == null
                ? string.Empty
                : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static int NormalizeResultLimit(int value)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException("maxResults");
            }
            return Math.Min(value, MaximumResultCount);
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (!((current >= '0' && current <= '9') ||
                    (current >= 'a' && current <= 'f') ||
                    (current >= 'A' && current <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(bytes);
                StringBuilder value = new StringBuilder(64);
                for (int index = 0; index < digest.Length; index++)
                {
                    value.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                }
                return value.ToString();
            }
        }

        private static NewLineProfile DetectNewLine(string text)
        {
            int crlf = 0;
            int lf = 0;
            int cr = 0;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] == '\r')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        crlf++;
                        index++;
                    }
                    else
                    {
                        cr++;
                    }
                }
                else if (text[index] == '\n')
                {
                    lf++;
                }
            }
            if (crlf == 0 && lf == 0 && cr == 0)
            {
                return new NewLineProfile(Environment.NewLine, false);
            }
            if (crlf >= lf && crlf >= cr)
            {
                return new NewLineProfile(
                    "\r\n",
                    (lf > 0 || cr > 0));
            }
            return new NewLineProfile(
                lf >= cr ? "\n" : "\r",
                (crlf > 0 || (lf > 0 && cr > 0)));
        }

        private static bool EndsWithNewLine(string text)
        {
            return text.Length > 0 &&
                (text[text.Length - 1] == '\r' || text[text.Length - 1] == '\n');
        }

        private static string NormalizeEditedText(
            string text,
            string newLine,
            bool preserveFinalNewLine,
            bool normalizeNewLines)
        {
            string normalized = text;
            if (normalizeNewLines)
            {
                normalized = normalized.Replace("\r\n", "\n").Replace("\r", "\n");
                if (newLine != "\n")
                {
                    normalized = normalized.Replace("\n", newLine);
                }
            }
            if (preserveFinalNewLine)
            {
                if (!EndsWithNewLine(normalized))
                {
                    normalized += newLine;
                }
            }
            else
            {
                normalized = TrimTrailingNewLines(normalized);
            }
            return normalized;
        }

        private static string TrimTrailingNewLines(string value)
        {
            int length = value.Length;
            while (length > 0 &&
                (value[length - 1] == '\r' || value[length - 1] == '\n'))
            {
                length--;
            }
            return length == value.Length ? value : value.Substring(0, length);
        }

        private static long CombineUnsigned(uint high, uint low)
        {
            return ((long)high << 32) | low;
        }

        private static DateTime NativeFileTimeToUtc(NativeFileTime value)
        {
            long fileTime = ((long)value.HighDateTime << 32) |
                value.LowDateTime;
            return DateTime.FromFileTimeUtc(fileTime);
        }

        private static IOException CreateNativeIOException(
            int error,
            string action)
        {
            return new IOException(
                action + "。Win32=" + error.ToString(CultureInfo.InvariantCulture),
                new Win32Exception(error));
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("CodeWorkspace");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true,
            EntryPoint = "ReplaceFileW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReplaceFile(
            string replacedFileName,
            string replacementFileName,
            string backupFileName,
            uint replaceFlags,
            IntPtr exclude,
            IntPtr reserved);

        [DllImport(
            "kernel32.dll",
            SetLastError = true,
            EntryPoint = "SetFileInformationByHandle")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            ref FileDispositionInformation fileInformation,
            uint bufferSize);

        [DllImport(
            "kernel32.dll",
            SetLastError = true,
            EntryPoint = "SetFileInformationByHandle")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandleBuffer(
            SafeFileHandle file,
            int fileInformationClass,
            IntPtr fileInformation,
            uint bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public NativeFileTime CreationTime;
            public NativeFileTime LastAccessTime;
            public NativeFileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformation
        {
            public byte DeleteFile;
        }

        private struct NativeIdentity
        {
            private uint volume;
            private uint high;
            private uint low;

            public static NativeIdentity From(ByHandleFileInformation information)
            {
                NativeIdentity result = new NativeIdentity();
                result.volume = information.VolumeSerialNumber;
                result.high = information.FileIndexHigh;
                result.low = information.FileIndexLow;
                return result;
            }

            public override bool Equals(object value)
            {
                if (!(value is NativeIdentity))
                {
                    return false;
                }
                NativeIdentity other = (NativeIdentity)value;
                return volume == other.volume && high == other.high && low == other.low;
            }

            public override int GetHashCode()
            {
                return (int)(volume ^ high ^ low);
            }
        }

        private sealed class NativeEntry : IDisposable
        {
            public SafeFileHandle Handle;
            public ByHandleFileInformation Information;

            public NativeEntry(
                SafeFileHandle handle,
                ByHandleFileInformation information)
            {
                Handle = handle;
                Information = information;
            }

            public void Dispose()
            {
                if (Handle != null)
                {
                    Handle.Dispose();
                    Handle = null;
                }
            }
        }

        private sealed class OwnedTemporaryFile
        {
            public readonly string Path;
            public bool HasIdentity;
            public NativeIdentity Identity;
            public bool Preserve;

            public OwnedTemporaryFile(string path)
            {
                Path = path;
            }

            public void Remember(NativeIdentity identity)
            {
                Identity = identity;
                HasIdentity = true;
            }
        }

        private sealed class DirectoryLease : IDisposable
        {
            private List<SafeFileHandle> handles;

            public SafeFileHandle ParentHandle
            {
                get
                {
                    if (handles == null || handles.Count == 0)
                    {
                        throw new ObjectDisposedException("DirectoryLease");
                    }
                    return handles[handles.Count - 1];
                }
            }

            public DirectoryLease(List<SafeFileHandle> values)
            {
                handles = values;
            }

            public void Dispose()
            {
                if (handles == null)
                {
                    return;
                }
                for (int index = handles.Count - 1; index >= 0; index--)
                {
                    handles[index].Dispose();
                }
                handles = null;
            }
        }

        private sealed class TextSnapshot
        {
            public string RelativePath;
            public string Text;
            public byte[] RawBytes;
            public string Sha256;
            public TextCodec Codec;
            public string NewLine;
            public bool NormalizeNewLines;
            public bool HasFinalNewLine;
            public NativeIdentity Identity;

            public WorkspaceReadResult ToResult()
            {
                return new WorkspaceReadResult
                {
                    RelativePath = RelativePath,
                    Text = Text,
                    Sha256 = Sha256,
                    EncodingName = Codec.Name,
                    HasBom = Codec.Preamble.Length > 0,
                    NewLine = NewLine,
                    HasFinalNewLine = HasFinalNewLine,
                    Length = RawBytes.LongLength
                };
            }
        }

        private sealed class PreparedEdit
        {
            public TextSnapshot Current;
            public string NormalizedText;
            public byte[] Replacement;
            public string ReplacementHash;
        }

        private sealed class NewLineProfile
        {
            public string Value;
            public bool IsMixed;

            public NewLineProfile(string value, bool isMixed)
            {
                Value = value;
                IsMixed = isMixed;
            }
        }

        private sealed class UndoRecord
        {
            public string RelativePath;
            public string BeforeSha256;
            public string AfterSha256;
            public NativeIdentity AfterIdentity;
            public string BackupPath;
        }

        private sealed class BaselineRecord
        {
            public string Sha256;
            public NativeIdentity Identity;
        }

        private sealed class EnumeratedFileInfo
        {
            public long Length;
            public DateTime LastWriteUtc;
        }

        private sealed class TextCodec
        {
            public string Name;
            public Encoding Encoding;
            public byte[] Preamble;

            public static TextCodec Detect(byte[] bytes)
            {
                if (StartsWith(bytes, new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
                {
                    return Create("utf-32-be", StrictUtf32(true), 4);
                }
                if (StartsWith(bytes, new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
                {
                    return Create("utf-32-le", StrictUtf32(false), 4);
                }
                if (StartsWith(bytes, new byte[] { 0xEF, 0xBB, 0xBF }))
                {
                    return Create("utf-8", StrictUtf8(), 3);
                }
                if (StartsWith(bytes, new byte[] { 0xFE, 0xFF }))
                {
                    return Create("utf-16-be", StrictUtf16(true), 2);
                }
                if (StartsWith(bytes, new byte[] { 0xFF, 0xFE }))
                {
                    return Create("utf-16-le", StrictUtf16(false), 2);
                }

                try
                {
                    TextCodec utf8 = Create("utf-8", StrictUtf8(), 0);
                    utf8.Decode(bytes);
                    return utf8;
                }
                catch (DecoderFallbackException)
                {
                    try
                    {
                        Encoding gb18030 = Encoding.GetEncoding(
                            54936,
                            EncoderFallback.ExceptionFallback,
                            DecoderFallback.ExceptionFallback);
                        TextCodec legacy = Create("gb18030", gb18030, 0);
                        legacy.Decode(bytes);
                        return legacy;
                    }
                    catch (DecoderFallbackException failure)
                    {
                        throw new NotSupportedException(
                            "文件不是受支持的 UTF 或 GB18030 文本。",
                            failure);
                    }
                }
            }

            public string Decode(byte[] bytes)
            {
                int offset = Preamble.Length;
                if (bytes.Length < offset)
                {
                    throw new InvalidDataException("文本 BOM 不完整。");
                }
                return Encoding.GetString(bytes, offset, bytes.Length - offset);
            }

            public byte[] Encode(string text)
            {
                byte[] content = Encoding.GetBytes(text);
                byte[] result = new byte[Preamble.Length + content.Length];
                Buffer.BlockCopy(Preamble, 0, result, 0, Preamble.Length);
                Buffer.BlockCopy(content, 0, result, Preamble.Length, content.Length);
                return result;
            }

            private static TextCodec Create(
                string name,
                Encoding encoding,
                int preambleLength)
            {
                TextCodec result = new TextCodec();
                result.Name = name;
                result.Encoding = encoding;
                byte[] nativePreamble = encoding.GetPreamble();
                result.Preamble = new byte[preambleLength];
                if (preambleLength > 0)
                {
                    Buffer.BlockCopy(nativePreamble, 0, result.Preamble, 0, preambleLength);
                }
                return result;
            }

            private static bool StartsWith(byte[] bytes, byte[] prefix)
            {
                if (bytes.Length < prefix.Length)
                {
                    return false;
                }
                for (int index = 0; index < prefix.Length; index++)
                {
                    if (bytes[index] != prefix[index])
                    {
                        return false;
                    }
                }
                return true;
            }

            private static Encoding StrictUtf8()
            {
                // GetBytes never emits a preamble by itself. Keeping the
                // canonical preamble here lets the codec preserve an existing
                // UTF-8 BOM while a zero-length Preamble still represents a
                // BOM-less source file.
                return new UTF8Encoding(true, true);
            }

            private static Encoding StrictUtf16(bool bigEndian)
            {
                return new UnicodeEncoding(
                    bigEndian,
                    true,
                    true);
            }

            private static Encoding StrictUtf32(bool bigEndian)
            {
                return new UTF32Encoding(
                    bigEndian,
                    true,
                    true);
            }
        }
    }
}
