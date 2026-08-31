using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using FilePromptAIWin7;
using Microsoft.Win32.SafeHandles;

internal static class CodeWorkspaceSmokeTest
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: CodeWorkspaceSmokeTest <test-root>");
            return 2;
        }

        string testRoot = Path.GetFullPath(args[0]);
        try
        {
            ResetDirectory(testRoot);
            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                Path.Combine(testRoot, "data"));
            RunWorkspaceTests(testRoot);
            Console.WriteLine(
                "PASS | restricted code workspace paths, encoding, stale checks and undo");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | " + exception);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("FILEPROMPTAI_DATA_ROOT", null);
            try
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, true);
                }
            }
            catch
            {
            }
        }
    }

    private static void RunWorkspaceTests(string testRoot)
    {
        string project = Path.Combine(testRoot, "project");
        string source = Path.Combine(project, "src");
        Directory.CreateDirectory(source);
        string selected = Path.Combine(project, "main.cs");
        byte[] original = WithPreamble(
            new UTF8Encoding(true, true),
            "class Main\r\n{\r\n}\r\n");
        File.WriteAllBytes(selected, original);
        File.WriteAllText(
            Path.Combine(source, "helper.cs"),
            "class Helper\n{\n    // target marker\n}\n",
            new UTF8Encoding(false, true));
        string[] ignoredDirectories = new string[]
        {
            "node_modules", "bin", "obj", ".vs", "packages", "vendor"
        };
        for (int index = 0; index < ignoredDirectories.Length; index++)
        {
            string ignored = Path.Combine(project, ignoredDirectories[index]);
            Directory.CreateDirectory(ignored);
            File.WriteAllText(
                Path.Combine(ignored, "hidden-" + index + ".txt"),
                "dependency hidden marker",
                new UTF8Encoding(false, true));
        }

        ExpectFailure(
            delegate { CodeWorkspace.OpenFromSelectedFile(@"\\server\share\a.cs"); },
            "UNC selection");
        ExpectFailure(
            delegate { CodeWorkspace.OpenFromSelectedFile(@"\\?\C:\a.cs"); },
            "device selection");

        using (CodeWorkspace workspace =
            CodeWorkspace.OpenFromSelectedFile(selected))
        {
            AssertEqual(project, workspace.RootPath, "workspace root");
            AssertEqual("main.cs", workspace.SelectedRelativePath, "selected path");
            AssertTrue(!workspace.CanUndo, "initial undo state");

            IList<WorkspaceFileInfo> files = workspace.ListFiles("", 20);
            AssertContains(files, "main.cs", "root listing");
            AssertContains(files, @"src\helper.cs", "recursive listing");
            for (int index = 0; index < ignoredDirectories.Length; index++)
            {
                AssertNotContainsDirectory(
                    files,
                    ignoredDirectories[index],
                    "ignored directory listing " + ignoredDirectories[index]);
            }
            IList<WorkspaceSearchResult> matches =
                workspace.Search("src", "TARGET MARKER", 20);
            AssertTrue(
                matches.Count == 1 &&
                matches[0].RelativePath == @"src\helper.cs" &&
                matches[0].LineNumber == 3,
                "case-insensitive text search");
            IList<WorkspaceSearchResult> ignoredMatches =
                workspace.Search("", "dependency hidden marker", 20);
            AssertTrue(ignoredMatches.Count == 0,
                "ignored directories are excluded from search");

            WorkspaceReadResult read = workspace.ReadFile("main.cs");
            AssertEqual("utf-8", read.EncodingName, "UTF-8 encoding");
            AssertTrue(read.HasBom, "UTF-8 BOM");
            AssertEqual("\r\n", read.NewLine, "CRLF detection");
            AssertTrue(read.HasFinalNewLine, "final newline detection");

            string[] dangerous = new string[]
            {
                @"..\outside.cs", @"src\..\main.cs", @".\main.cs",
                @"C:\outside.cs", @"C:outside.cs", @"\outside.cs",
                @"main.cs:stream", @"main.cs::$DATA", @"src\\helper.cs",
                @"CON.cs", @"LPT1.txt", @"name.", @".git\config",
                @".ssh\id_rsa", @".aws\credentials", @".npmrc",
                @".env.production", @"server.pem", @"signing.pfx",
                @"node_modules\module.js", @"bin\output.dll",
                @"obj\cache.bin", @".vs\settings.json",
                @"packages\package.txt", @"vendor\library.php"
            };
            for (int index = 0; index < dangerous.Length; index++)
            {
                string candidate = dangerous[index];
                ExpectFailure(
                    delegate { workspace.ReadFile(candidate); },
                    "unsafe relative path " + candidate);
            }

            WorkspaceEditResult edited = workspace.ApplyEdit(
                new WorkspaceEdit(
                    "main.cs",
                    read.Sha256,
                    "class Main\n{\n    static int Value = 1;\n}"));
            AssertTrue(
                edited.BackupCreated && edited.UndoAvailable &&
                workspace.CanUndo &&
                workspace.LastEditedRelativePath == "main.cs",
                "edit creates one undo record");
            byte[] editedBytes = File.ReadAllBytes(selected);
            AssertTrue(StartsWith(editedBytes, new byte[] { 0xEF, 0xBB, 0xBF }),
                "edit retains UTF-8 BOM");
            AssertEqual(
                "class Main\r\n{\r\n    static int Value = 1;\r\n}\r\n",
                workspace.ReadFile("main.cs").Text,
                "edit retains CRLF and final-newline style");

            WorkspaceEditResult undone = workspace.Undo("main.cs");
            AssertTrue(!undone.UndoAvailable && !workspace.CanUndo,
                "undo is consumed once");
            AssertBytesEqual(original, File.ReadAllBytes(selected),
                "undo restores exact original bytes");
            ExpectFailure(delegate { workspace.Undo("main.cs"); }, "second undo");

            TestStaleEdit(workspace, selected);
            TestSameContentReplacement(workspace, selected);
            TestUndoConflict(workspace, selected);
            TestSameContentUndoReplacement(workspace, selected);
            TestEncodings(workspace, project);
            TestMixedNewLines(workspace, project);
            TestTrailingBlankLines(workspace, project);
            TestMetadataPreservation(workspace, project);
            TestReadOnlyRejection(workspace, project);
            TestBinaryRejection(workspace, project);
            TestMaximumTextFileSize(workspace, project);
            TestHardLinkRejection(workspace, project);
            TestReparseRejection(workspace, project, testRoot);
        }

        TestDisposeSerializesWithMutation(project);
        TestDisposeSerializesWithReaders(project);
        TestReplaceFileFailureStates(project);
    }

    private static void TestMetadataPreservation(
        CodeWorkspace workspace,
        string project)
    {
        string relative = "metadata.cs";
        string path = Path.Combine(project, relative);
        File.WriteAllText(
            path,
            "class Metadata\r\n{\r\n}\r\n",
            new UTF8Encoding(false, true));
        FileAttributes expectedAttributes =
            FileAttributes.Hidden | FileAttributes.Archive;
        File.SetAttributes(path, expectedAttributes);

        FileSecurity security = File.GetAccessControl(path);
        security.SetAccessRuleProtection(true, true);
        File.SetAccessControl(path, security);
        string expectedDacl = File.GetAccessControl(path).GetSecurityDescriptorSddlForm(
            AccessControlSections.Access);

        string streamPath = path + ":FilePromptAI.Metadata";
        WriteNativeTextStream(streamPath, "named stream retained");

        WorkspaceReadResult read = workspace.ReadFile(relative);
        workspace.ApplyEdit(new WorkspaceEdit(
            relative,
            read.Sha256,
            "class Metadata\n{\n    static int Value = 19;\n}"));

        AssertEqual(
            expectedDacl,
            File.GetAccessControl(path).GetSecurityDescriptorSddlForm(
                AccessControlSections.Access),
            "explicit DACL preservation");
        AssertTrue(
            (File.GetAttributes(path) & expectedAttributes) == expectedAttributes,
            "file attribute preservation");
        AssertEqual(
            "named stream retained",
            ReadNativeTextStream(streamPath),
            "named stream preservation");
        workspace.Undo(relative);
        AssertEqual(
            expectedDacl,
            File.GetAccessControl(path).GetSecurityDescriptorSddlForm(
                AccessControlSections.Access),
            "undo DACL preservation");
        AssertEqual(
            "named stream retained",
            ReadNativeTextStream(streamPath),
            "undo named stream preservation");
    }

    private static void TestReadOnlyRejection(
        CodeWorkspace workspace,
        string project)
    {
        string relative = "readonly.cs";
        string path = Path.Combine(project, relative);
        const string original = "class ReadOnly\r\n{\r\n}\r\n";
        File.WriteAllText(path, original, new UTF8Encoding(false, true));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            WorkspaceReadResult read = workspace.ReadFile(relative);
            ExpectFailure(
                delegate
                {
                    workspace.ApplyEdit(new WorkspaceEdit(
                        relative,
                        read.Sha256,
                        "class Changed {}"));
                },
                "read-only edit rejection");
            AssertEqual(original, File.ReadAllText(path),
                "read-only file remains unchanged");
            AssertTrue(
                (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0,
                "read-only attribute remains set");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    private static void TestDisposeSerializesWithMutation(string project)
    {
        string path = Path.Combine(project, "dispose-race.cs");
        File.WriteAllText(
            path,
            "class DisposeRace\r\n{\r\n}\r\n",
            new UTF8Encoding(false, true));
        CodeWorkspace workspace = CodeWorkspace.OpenFromSelectedFile(path);
        WorkspaceReadResult read = workspace.ReadFile("dispose-race.cs");
        FieldInfo lockField = typeof(CodeWorkspace).GetField(
            "mutationLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        AssertTrue(lockField != null, "mutation lock is present");
        object mutationLock = lockField.GetValue(workspace);
        Exception editFailure = null;
        ManualResetEvent editStarted = new ManualResetEvent(false);
        ManualResetEvent disposeStarted = new ManualResetEvent(false);
        Thread editThread = new Thread(delegate()
        {
            editStarted.Set();
            try
            {
                workspace.ApplyEdit(new WorkspaceEdit(
                    "dispose-race.cs",
                    read.Sha256,
                    "class DisposeRace { static int Value = 19; }"));
            }
            catch (Exception exception)
            {
                editFailure = exception;
            }
        });
        Thread disposeThread = new Thread(delegate()
        {
            disposeStarted.Set();
            workspace.Dispose();
        });

        Monitor.Enter(mutationLock);
        try
        {
            editThread.Start();
            disposeThread.Start();
            AssertTrue(editStarted.WaitOne(5000), "concurrent edit started");
            AssertTrue(disposeStarted.WaitOne(5000), "concurrent dispose started");
            AssertTrue(!disposeThread.Join(150),
                "dispose waits for the mutation boundary");
        }
        finally
        {
            Monitor.Exit(mutationLock);
        }

        AssertTrue(disposeThread.Join(10000), "dispose completes");
        byte[] atDisposeReturn = File.ReadAllBytes(path);
        AssertTrue(editThread.Join(10000), "concurrent edit completes");
        AssertBytesEqual(
            atDisposeReturn,
            File.ReadAllBytes(path),
            "no write completes after Dispose returns");
        AssertTrue(
            editFailure == null || editFailure is ObjectDisposedException,
            "concurrent edit either completes first or is rejected as disposed");
        ExpectFailure(
            delegate { workspace.ReadFile("dispose-race.cs"); },
            "read after dispose");
        editStarted.Dispose();
        disposeStarted.Dispose();
    }

    private static void TestDisposeSerializesWithReaders(string project)
    {
        string path = Path.Combine(project, "dispose-reader.cs");
        File.WriteAllText(
            path,
            "class DisposeReader { }\r\n",
            new UTF8Encoding(false, true));
        Action<CodeWorkspace>[] operations = new Action<CodeWorkspace>[]
        {
            delegate(CodeWorkspace value) { value.ListFiles("", 10); },
            delegate(CodeWorkspace value) { value.Search("", "DisposeReader", 10); },
            delegate(CodeWorkspace value) { value.ReadFile("dispose-reader.cs"); }
        };
        string[] names = new string[] { "list", "search", "read" };
        FieldInfo lockField = typeof(CodeWorkspace).GetField(
            "mutationLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        for (int index = 0; index < operations.Length; index++)
        {
            CodeWorkspace workspace = CodeWorkspace.OpenFromSelectedFile(path);
            object mutationLock = lockField.GetValue(workspace);
            Exception operationFailure = null;
            ManualResetEvent operationStarted = new ManualResetEvent(false);
            ManualResetEvent disposeStarted = new ManualResetEvent(false);
            Thread operationThread = new Thread(delegate()
            {
                operationStarted.Set();
                try
                {
                    operations[index](workspace);
                }
                catch (Exception exception)
                {
                    operationFailure = exception;
                }
            });
            Thread disposeThread = new Thread(delegate()
            {
                disposeStarted.Set();
                workspace.Dispose();
            });

            Monitor.Enter(mutationLock);
            try
            {
                operationThread.Start();
                disposeThread.Start();
                AssertTrue(operationStarted.WaitOne(5000),
                    names[index] + " operation started");
                AssertTrue(disposeStarted.WaitOne(5000),
                    names[index] + " dispose started");
                AssertTrue(!operationThread.Join(150),
                    names[index] + " waits for lifecycle boundary");
                AssertTrue(!disposeThread.Join(150),
                    names[index] + " dispose waits for lifecycle boundary");
            }
            finally
            {
                Monitor.Exit(mutationLock);
            }

            AssertTrue(operationThread.Join(10000),
                names[index] + " operation completes");
            AssertTrue(disposeThread.Join(10000),
                names[index] + " dispose completes");
            AssertTrue(
                operationFailure == null ||
                    operationFailure is ObjectDisposedException,
                names[index] + " completes before dispose or is rejected");
            operationStarted.Dispose();
            disposeStarted.Dispose();
        }
    }

    private static void TestReplaceFileFailureStates(string project)
    {
        TestUnchangedReplaceFailure(project, 1175, "1175");
        TestUnchangedReplaceFailure(project, 1176, "1176");
        TestInitial1177Recovery(project);
        TestInitial1177RecoveryInSubdirectory(project);
        TestInitial1177RecoveryNearPathLimit(project);
        TestReverseUnchangedFailure(project, 1175, "1175");
        TestReverseUnchangedFailure(project, 1176, "1176");
        TestReverse1177Recovery(project);
        TestTemporarySentinelIsNotDeleted(project);
    }

    private static void TestUnchangedReplaceFailure(
        string project,
        int errorCode,
        string name)
    {
        string relative = "replace-" + name + ".cs";
        string path = Path.Combine(project, relative);
        byte[] original = Encoding.UTF8.GetBytes("class Before" + name + " { }\r\n");
        File.WriteAllBytes(path, original);
        CodeWorkspace.ReplaceFileInvokerForTest = delegate(
            string replaced,
            string replacement,
            string backup)
        {
            return new ReplaceFileCallResult(false, errorCode);
        };
        try
        {
            using (CodeWorkspace workspace = CodeWorkspace.OpenFromSelectedFile(path))
            {
                WorkspaceReadResult read = workspace.ReadFile(relative);
                ExpectFailure(
                    delegate
                    {
                        workspace.ApplyEdit(new WorkspaceEdit(
                            relative,
                            read.Sha256,
                            "class After" + name + " { }"));
                    },
                    name + " is never reported as success");
            }
            AssertBytesEqual(original, File.ReadAllBytes(path),
                name + " leaves target unchanged");
        }
        finally
        {
            CodeWorkspace.ReplaceFileInvokerForTest = null;
        }
    }

    private static void TestInitial1177Recovery(string project)
    {
        TestInitial1177RecoveryAtPath(
            project,
            "replace-initial-1177.cs",
            "initial 1177");
    }

    private static void TestInitial1177RecoveryInSubdirectory(string project)
    {
        string directory = Path.Combine(project, "nested-1177");
        Directory.CreateDirectory(directory);
        TestInitial1177RecoveryAtPath(
            project,
            @"nested-1177\replace-initial-1177.cs",
            "nested initial 1177");
    }

    private static void TestInitial1177RecoveryNearPathLimit(string project)
    {
        const int targetPathLength = 196;
        const string directoryName = "limit1177";
        string directory = Path.Combine(project, directoryName);
        Directory.CreateDirectory(directory);
        int relativeLength = targetPathLength - project.Length - 1;
        int leafLength = relativeLength - directoryName.Length - 1;
        if (leafLength < 4 || leafLength > 240)
        {
            throw new InvalidOperationException(
                "Test root is too long for the near-limit recovery fixture.");
        }
        string leaf = new string('f', leafLength - 3) + ".cs";
        TestInitial1177RecoveryAtPath(
            project,
            directoryName + "\\" + leaf,
            "near-limit initial 1177");
        AssertTrue(
            Path.Combine(project, directoryName, leaf).Length == targetPathLength,
            "near-limit recovery fixture reaches the intended path length");
    }

    private static void TestInitial1177RecoveryAtPath(
        string project,
        string relative,
        string description)
    {
        string path = Path.Combine(project, relative);
        byte[] original = Encoding.UTF8.GetBytes("class Initial1177 { }\r\n");
        File.WriteAllBytes(path, original);
        string rollbackPath = null;
        CodeWorkspace.ReplaceFileInvokerForTest = delegate(
            string replaced,
            string replacement,
            string backup)
        {
            rollbackPath = backup;
            File.Move(replaced, backup);
            return new ReplaceFileCallResult(false, 1177);
        };
        try
        {
            string authorizationFile = Path.Combine(project, "main.cs");
            using (CodeWorkspace workspace =
                CodeWorkspace.OpenFromSelectedFile(authorizationFile))
            {
                WorkspaceReadResult read = workspace.ReadFile(relative);
                Exception failure = CaptureFailure(delegate
                {
                    workspace.ApplyEdit(new WorkspaceEdit(
                        relative,
                        read.Sha256,
                        "class Initial1177Changed { }"));
                });
                AssertTrue(failure != null,
                    description + " remains a failed edit");
                AssertTrue(File.Exists(path),
                    description + " restores a target path: " + failure);
                AssertTrue(
                    !string.IsNullOrEmpty(rollbackPath) &&
                    !File.Exists(rollbackPath),
                    description + " consumes the rollback path after restoration");
            }
            AssertBytesEqual(original, File.ReadAllBytes(path),
                description + " restores exact original object");
        }
        finally
        {
            CodeWorkspace.ReplaceFileInvokerForTest = null;
        }
    }

    private static void TestReverse1177Recovery(string project)
    {
        string relative = "replace-reverse-1177.cs";
        string path = Path.Combine(project, relative);
        const string unexpectedText = "class ConcurrentReplacement { }\r\n";
        File.WriteAllText(path, "class Original { }\r\n", new UTF8Encoding(false, true));
        int calls = 0;
        string reverseRollbackPath = null;
        CodeWorkspace.ReplaceFileInvokerForTest = delegate(
            string replaced,
            string replacement,
            string backup)
        {
            calls++;
            if (calls == 1)
            {
                // Simulate the path race without invalidating the baseline
                // before ReplaceFile: move the verified original to the
                // rollback name, then publish the staged object at target.
                File.Move(replaced, backup);
                File.Delete(backup);
                File.WriteAllText(
                    backup,
                    unexpectedText,
                    new UTF8Encoding(false, true));
                File.Move(replacement, replaced);
                return new ReplaceFileCallResult(true, 0);
            }

            // ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: the replaced object has
            // moved to the backup name, the replacement retains its name,
            // and the destination name is empty.
            reverseRollbackPath = backup;
            File.Move(replaced, backup);
            return new ReplaceFileCallResult(false, 1177);
        };
        try
        {
            using (CodeWorkspace workspace = CodeWorkspace.OpenFromSelectedFile(path))
            {
                WorkspaceReadResult read = workspace.ReadFile(relative);
                Exception failure = CaptureFailure(delegate
                {
                    workspace.ApplyEdit(new WorkspaceEdit(
                        relative,
                        read.Sha256,
                        "class Proposed { }"));
                });
                AssertTrue(failure != null,
                    "reverse 1177 remains a failed edit");
                AssertTrue(File.Exists(path),
                    "reverse 1177 restores a target path: " + failure);
                AssertTrue(
                    !string.IsNullOrEmpty(reverseRollbackPath) &&
                    !File.Exists(reverseRollbackPath),
                    "reverse 1177 consumes the rollback path after restoration");
            }
            AssertTrue(calls == 2, "reverse recovery invokes ReplaceFile twice");
            AssertEqual(unexpectedText, File.ReadAllText(path),
                "reverse 1177 restores the unexpected object from rollback");
        }
        finally
        {
            CodeWorkspace.ReplaceFileInvokerForTest = null;
        }
    }

    private static void TestReverseUnchangedFailure(
        string project,
        int reverseError,
        string name)
    {
        string relative = "replace-reverse-" + name + ".cs";
        string path = Path.Combine(project, relative);
        const string unexpectedText = "class ConcurrentPreserved { }\r\n";
        File.WriteAllText(path, "class Original { }\r\n", new UTF8Encoding(false, true));
        int calls = 0;
        string rollbackPath = null;
        CodeWorkspace.ReplaceFileInvokerForTest = delegate(
            string replaced,
            string replacement,
            string backup)
        {
            calls++;
            if (calls == 1)
            {
                File.Move(replaced, backup);
                File.Delete(backup);
                File.WriteAllText(
                    backup,
                    unexpectedText,
                    new UTF8Encoding(false, true));
                rollbackPath = backup;
                File.Move(replacement, replaced);
                return new ReplaceFileCallResult(true, 0);
            }
            return new ReplaceFileCallResult(false, reverseError);
        };
        try
        {
            using (CodeWorkspace workspace = CodeWorkspace.OpenFromSelectedFile(path))
            {
                WorkspaceReadResult read = workspace.ReadFile(relative);
                ExpectFailure(
                    delegate
                    {
                        workspace.ApplyEdit(new WorkspaceEdit(
                            relative,
                            read.Sha256,
                            "class Proposed" + name + " { }"));
                    },
                    "reverse " + name + " remains a failed edit");
            }
            AssertTrue(calls == 2, "reverse " + name + " invokes recovery");
            AssertTrue(File.Exists(path),
                "reverse " + name + " preserves staged target");
            AssertTrue(!string.IsNullOrEmpty(rollbackPath) &&
                File.Exists(rollbackPath),
                "reverse " + name + " preserves concurrent rollback object");
            AssertEqual(unexpectedText, File.ReadAllText(rollbackPath),
                "reverse " + name + " does not delete concurrent object");
        }
        finally
        {
            CodeWorkspace.ReplaceFileInvokerForTest = null;
            if (!string.IsNullOrEmpty(rollbackPath) && File.Exists(rollbackPath))
            {
                File.Delete(rollbackPath);
            }
        }
    }

    private static void TestTemporarySentinelIsNotDeleted(string project)
    {
        string relative = "temporary-sentinel.cs";
        string path = Path.Combine(project, relative);
        File.WriteAllText(path, "class Sentinel { }\r\n", new UTF8Encoding(false, true));
        string sentinelPath = null;
        CodeWorkspace.ReplaceFileInvokerForTest = delegate(
            string replaced,
            string replacement,
            string backup)
        {
            return new ReplaceFileCallResult(false, 1175);
        };
        CodeWorkspace.BeforeOwnedTemporaryDeleteForTest = delegate(string candidate)
        {
            if (sentinelPath != null)
            {
                return;
            }
            File.Delete(candidate);
            File.WriteAllText(
                candidate,
                "sentinel must survive",
                new UTF8Encoding(false, true));
            sentinelPath = candidate;
        };
        try
        {
            using (CodeWorkspace workspace = CodeWorkspace.OpenFromSelectedFile(path))
            {
                WorkspaceReadResult read = workspace.ReadFile(relative);
                ExpectFailure(
                    delegate
                    {
                        workspace.ApplyEdit(new WorkspaceEdit(
                            relative,
                            read.Sha256,
                            "class Changed { }"));
                    },
                    "sentinel cleanup race");
            }
            AssertTrue(!string.IsNullOrEmpty(sentinelPath) && File.Exists(sentinelPath),
                "identity-mismatched sentinel is retained");
            AssertEqual("sentinel must survive", File.ReadAllText(sentinelPath),
                "identity-mismatched sentinel content survives");
        }
        finally
        {
            CodeWorkspace.ReplaceFileInvokerForTest = null;
            CodeWorkspace.BeforeOwnedTemporaryDeleteForTest = null;
            if (!string.IsNullOrEmpty(sentinelPath) && File.Exists(sentinelPath))
            {
                File.Delete(sentinelPath);
            }
        }
    }

    private static void TestStaleEdit(CodeWorkspace workspace, string path)
    {
        File.WriteAllText(path, "before\r\n", new UTF8Encoding(false, true));
        WorkspaceReadResult baseline = workspace.ReadFile("main.cs");
        File.WriteAllText(path, "manual\r\n", new UTF8Encoding(false, true));
        ExpectFailure(
            delegate
            {
                workspace.ApplyEdit(new WorkspaceEdit(
                    "main.cs", baseline.Sha256, "model"));
            },
            "stale SHA-256");
        AssertEqual("manual\r\n", File.ReadAllText(path),
            "stale edit leaves manual content");
    }

    private static void TestUndoConflict(CodeWorkspace workspace, string path)
    {
        WorkspaceReadResult baseline = workspace.ReadFile("main.cs");
        workspace.ApplyEdit(new WorkspaceEdit(
            "main.cs", baseline.Sha256, "model edit"));
        File.WriteAllText(path, "manual after model", new UTF8Encoding(false, true));
        ExpectFailure(delegate { workspace.Undo("main.cs"); }, "undo conflict");
        AssertEqual("manual after model", File.ReadAllText(path),
            "conflicting undo leaves manual content");
    }

    private static void TestSameContentReplacement(
        CodeWorkspace workspace,
        string path)
    {
        string identityPath = Path.Combine(
            Path.GetDirectoryName(path),
            "identity.cs");
        File.WriteAllText(
            identityPath,
            "same bytes\r\n",
            new UTF8Encoding(false, true));
        WorkspaceReadResult baseline = workspace.ReadFile("identity.cs");
        byte[] sameBytes = File.ReadAllBytes(identityPath);
        File.Delete(identityPath);
        File.WriteAllBytes(identityPath, sameBytes);
        WorkspaceReadResult repeated = workspace.ReadFile("identity.cs");
        AssertEqual(baseline.Sha256, repeated.Sha256,
            "same-content replacement hash");
        ExpectFailure(
            delegate
            {
                workspace.ApplyEdit(new WorkspaceEdit(
                    "identity.cs", baseline.Sha256, "must not write"));
            },
            "same-content identity replacement");
        AssertBytesEqual(sameBytes, File.ReadAllBytes(identityPath),
            "identity conflict leaves replacement untouched");
    }

    private static void TestSameContentUndoReplacement(
        CodeWorkspace workspace,
        string path)
    {
        WorkspaceReadResult baseline = workspace.ReadFile("main.cs");
        workspace.ApplyEdit(new WorkspaceEdit(
            "main.cs", baseline.Sha256, "model identity edit"));
        byte[] sameBytes = File.ReadAllBytes(path);
        File.Delete(path);
        File.WriteAllBytes(path, sameBytes);
        ExpectFailure(
            delegate { workspace.Undo("main.cs"); },
            "same-content undo identity replacement");
        AssertBytesEqual(sameBytes, File.ReadAllBytes(path),
            "undo identity conflict leaves replacement untouched");
    }

    private static void TestEncodings(CodeWorkspace workspace, string project)
    {
        Encoding[] encodings = new Encoding[]
        {
            new UTF8Encoding(false, true),
            new UnicodeEncoding(false, true, true),
            new UnicodeEncoding(true, true, true),
            new UTF32Encoding(false, true, true),
            new UTF32Encoding(true, true, true),
            Encoding.GetEncoding(
                54936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback)
        };
        string[] names = new string[]
        {
            "utf-8", "utf-16-le", "utf-16-be",
            "utf-32-le", "utf-32-be", "gb18030"
        };
        for (int index = 0; index < encodings.Length; index++)
        {
            string relative = "encoding-" + index + ".txt";
            string fullPath = Path.Combine(project, relative);
            byte[] before = WithPreamble(encodings[index], "中文\nline\n");
            File.WriteAllBytes(fullPath, before);
            WorkspaceReadResult read = workspace.ReadFile(relative);
            AssertEqual(names[index], read.EncodingName,
                "encoding detection " + names[index]);
            workspace.ApplyEdit(new WorkspaceEdit(
                relative, read.Sha256, "中文\r\nchanged"));
            WorkspaceReadResult changed = workspace.ReadFile(relative);
            AssertEqual(names[index], changed.EncodingName,
                "encoding retention " + names[index]);
            AssertEqual("中文\nchanged\n", changed.Text,
                "newline retention " + names[index]);
            workspace.Undo(relative);
            AssertBytesEqual(before, File.ReadAllBytes(fullPath),
                "byte-exact undo " + names[index]);
        }
    }

    private static void TestBinaryRejection(CodeWorkspace workspace, string project)
    {
        string path = Path.Combine(project, "binary.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 0, 3, 4 });
        ExpectFailure(delegate { workspace.ReadFile("binary.bin"); },
            "binary NUL rejection");
    }

    private static void TestMaximumTextFileSize(
        CodeWorkspace workspace,
        string project)
    {
        string oversizedRelative = "oversized.txt";
        string oversizedPath = Path.Combine(project, oversizedRelative);
        File.WriteAllBytes(oversizedPath, new byte[(256 * 1024) + 1]);
        ExpectFailure(
            delegate { workspace.ReadFile(oversizedRelative); },
            "256 KiB plus one byte read rejection");

        string writableRelative = "size-limit.txt";
        string writablePath = Path.Combine(project, writableRelative);
        File.WriteAllText(
            writablePath,
            "before\r\n",
            new UTF8Encoding(false, true));
        WorkspaceReadResult baseline = workspace.ReadFile(writableRelative);
        string oversizedText = new string('x', (256 * 1024) + 1);
        ExpectFailure(
            delegate
            {
                workspace.ApplyEdit(new WorkspaceEdit(
                    writableRelative,
                    baseline.Sha256,
                    oversizedText));
            },
            "256 KiB plus one byte write rejection");
        AssertEqual(
            "before\r\n",
            File.ReadAllText(writablePath),
            "oversized write leaves original unchanged");
    }

    private static void TestMixedNewLines(
        CodeWorkspace workspace,
        string project)
    {
        const string original = "first\r\nsecond\nthird\r\nfourth\n";
        string path = Path.Combine(project, "mixed-lines.cs");
        File.WriteAllText(path, original, new UTF8Encoding(false, true));
        WorkspaceReadResult baseline = workspace.ReadFile("mixed-lines.cs");
        workspace.ApplyEdit(new WorkspaceEdit(
            "mixed-lines.cs",
            baseline.Sha256,
            "first\r\nchanged\nthird\r\nfourth\n"));
        AssertEqual(
            "first\r\nchanged\nthird\r\nfourth\n",
            workspace.ReadFile("mixed-lines.cs").Text,
            "mixed newlines are not globally normalized");
        workspace.Undo("mixed-lines.cs");
        AssertEqual(original, workspace.ReadFile("mixed-lines.cs").Text,
            "mixed newline undo");
    }

    private static void TestTrailingBlankLines(
        CodeWorkspace workspace,
        string project)
    {
        string path = Path.Combine(project, "blank-lines.cs");
        File.WriteAllText(
            path,
            "first\r\n\r\n\r\n",
            new UTF8Encoding(false, true));
        WorkspaceReadResult baseline = workspace.ReadFile("blank-lines.cs");
        workspace.ApplyEdit(new WorkspaceEdit(
            "blank-lines.cs",
            baseline.Sha256,
            "changed\n\n\n"));
        AssertEqual(
            "changed\r\n\r\n\r\n",
            workspace.ReadFile("blank-lines.cs").Text,
            "trailing blank lines are retained");
        workspace.Undo("blank-lines.cs");
    }

    private static void TestHardLinkRejection(CodeWorkspace workspace, string project)
    {
        string original = Path.Combine(project, "hard-source.cs");
        string link = Path.Combine(project, "hard-link.cs");
        File.WriteAllText(original, "hard link", new UTF8Encoding(false, true));
        if (!CreateHardLink(link, original, IntPtr.Zero))
        {
            throw new InvalidOperationException(
                "Test hard link could not be created. Win32=" +
                Marshal.GetLastWin32Error());
        }
        ExpectFailure(delegate { workspace.ReadFile("hard-link.cs"); },
            "hard-link rejection");
    }

    private static void TestReparseRejection(
        CodeWorkspace workspace,
        string project,
        string testRoot)
    {
        string outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(
            Path.Combine(outside, "escape.cs"),
            "outside",
            new UTF8Encoding(false, true));
        string junction = Path.Combine(project, "linked");
        ProcessStartInfo start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            "/d /c mklink /J \"" + junction + "\" \"" + outside + "\"");
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        using (Process process = Process.Start(start))
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Test junction could not be created.");
            }
        }
        ExpectFailure(
            delegate { workspace.ReadFile(@"linked\escape.cs"); },
            "junction escape rejection");
    }

    private static byte[] WithPreamble(Encoding encoding, string text)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] content = encoding.GetBytes(text);
        byte[] result = new byte[preamble.Length + content.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(content, 0, result, preamble.Length, content.Length);
        return result;
    }

    private static void WriteNativeTextStream(string path, string value)
    {
        using (SafeFileHandle handle = CreateFileForTest(
            path,
            0x40000000,
            2))
        using (FileStream stream = new FileStream(
            handle,
            FileAccess.Write,
            4096,
            false))
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
    }

    private static string ReadNativeTextStream(string path)
    {
        using (SafeFileHandle handle = CreateFileForTest(
            path,
            0x80000000,
            3))
        using (FileStream stream = new FileStream(
            handle,
            FileAccess.Read,
            4096,
            false))
        using (StreamReader reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            false))
        {
            return reader.ReadToEnd();
        }
    }

    private static SafeFileHandle CreateFileForTest(
        string path,
        uint access,
        uint creationDisposition)
    {
        SafeFileHandle handle = CreateFile(
            path,
            access,
            0,
            IntPtr.Zero,
            creationDisposition,
            0x00000080,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "Native test stream open failed. Win32=" + error);
        }
        return handle;
    }

    private static bool StartsWith(byte[] value, byte[] prefix)
    {
        if (value.Length < prefix.Length)
        {
            return false;
        }
        for (int index = 0; index < prefix.Length; index++)
        {
            if (value[index] != prefix[index])
            {
                return false;
            }
        }
        return true;
    }

    private static void AssertContains(
        IList<WorkspaceFileInfo> files,
        string path,
        string name)
    {
        for (int index = 0; index < files.Count; index++)
        {
            if (files[index].RelativePath == path)
            {
                return;
            }
        }
        throw new InvalidDataException(name + " failed.");
    }

    private static void AssertNotContainsDirectory(
        IList<WorkspaceFileInfo> files,
        string directory,
        string name)
    {
        string prefix = directory + "\\";
        for (int index = 0; index < files.Count; index++)
        {
            if (files[index].RelativePath.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(name + " failed.");
            }
        }
    }

    private static void ExpectFailure(Action action, string name)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }
        throw new InvalidDataException(name + " did not fail.");
    }

    private static Exception CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string name)
    {
        if (expected.Length != actual.Length)
        {
            throw new InvalidDataException(name + " length mismatch.");
        }
        for (int index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index])
            {
                throw new InvalidDataException(name + " byte mismatch.");
            }
        }
    }

    private static void AssertEqual(string expected, string actual, string name)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                name + " expected [" + expected + "] actual [" + actual + "].");
        }
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidDataException(name + " failed.");
        }
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
        Directory.CreateDirectory(path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
