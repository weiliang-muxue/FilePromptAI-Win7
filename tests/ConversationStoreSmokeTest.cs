using System;
using System.IO;
using System.Xml.Linq;
using FilePromptAIWin7;

internal static class ConversationStoreSmokeTest
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(
                "Usage: ConversationStoreSmokeTest <storage.xml>");
            return 2;
        }

        string path = Path.GetFullPath(args[0]);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            ConversationStore store = new ConversationStore(path);
            ConversationSession session = store.CreateSession("会话\u0001标题");
            if (!store.AddMessage(
                session.Id,
                "user",
                "用户问题\u0001和孤立代理项\uD800"))
            {
                throw new InvalidOperationException("User message was not added.");
            }

            if (!store.AddMessage(
                session.Id,
                "assistant",
                "模型回答和有效字符 \uD83D\uDE00"))
            {
                throw new InvalidOperationException(
                    "Assistant message was not added.");
            }

            XDocument.Load(path);
            ConversationStore reloaded = new ConversationStore(path);
            ConversationSession restored = reloaded.CurrentSession;
            if (restored == null || restored.Messages == null ||
                restored.Messages.Count != 2)
            {
                throw new InvalidDataException(
                    "Conversation did not survive reload.");
            }

            if (restored.Title.IndexOf('\u0001') >= 0 ||
                restored.Messages[0].Content.IndexOf('\u0001') >= 0 ||
                restored.Messages[0].Content.IndexOf('\uD800') >= 0)
            {
                throw new InvalidDataException(
                    "Invalid XML characters were persisted.");
            }

            if (restored.Messages[1].Content.IndexOf(
                "\uD83D\uDE00",
                StringComparison.Ordinal) < 0)
            {
                throw new InvalidDataException(
                    "Valid supplementary Unicode was lost.");
            }

            TestMessageGuardsAndNormalization(path, reloaded);
            TestAtomicTurnRollback(path, reloaded);
            TestSessionLifecycle(path, reloaded);
            TestCreateRollback(path);
            TestUnpreservedDamageBlocksWrites(path);

            File.WriteAllText(path, "<broken", new System.Text.UTF8Encoding(true));
            ConversationStore damaged = new ConversationStore(path);
            if (damaged.Sessions.Count != 0 ||
                string.IsNullOrWhiteSpace(damaged.LoadWarning))
            {
                throw new InvalidDataException(
                    "Damaged conversation store was not reported.");
            }

            string directory = Path.GetDirectoryName(path);
            string pattern = Path.GetFileName(path) + ".damaged-*.bak";
            string[] preserved = Directory.GetFiles(directory, pattern);
            if (preserved.Length == 0 ||
                File.ReadAllText(preserved[0]).IndexOf(
                    "<broken",
                    StringComparison.Ordinal) < 0)
            {
                throw new InvalidDataException(
                    "Damaged conversation store was not preserved.");
            }

            File.WriteAllText(
                path,
                "<NotAConversationStore version=\"1\" />",
                new System.Text.UTF8Encoding(true));
            ConversationStore wrongRoot = new ConversationStore(path);
            if (wrongRoot.Sessions.Count != 0 ||
                string.IsNullOrWhiteSpace(wrongRoot.LoadWarning))
            {
                throw new InvalidDataException(
                    "A well-formed store with the wrong root was not rejected.");
            }

            File.WriteAllText(
                path,
                "<ConversationStore version=\"99\" currentSessionId=\"\" />",
                new System.Text.UTF8Encoding(true));
            ConversationStore wrongVersion = new ConversationStore(path);
            if (wrongVersion.Sessions.Count != 0 ||
                string.IsNullOrWhiteSpace(wrongVersion.LoadWarning))
            {
                throw new InvalidDataException(
                    "An unsupported conversation version was not rejected.");
            }

            Console.WriteLine("PASS | conversation persistence and recovery");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | " + exception);
            return 1;
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                string directory = Path.GetDirectoryName(path);
                string pattern = Path.GetFileName(path) + ".damaged-*.bak";
                foreach (string preserved in Directory.GetFiles(
                    directory,
                    pattern))
                {
                    File.Delete(preserved);
                }
            }
            catch
            {
                // A leftover test file is harmless and can be overwritten next run.
            }
        }
    }

    private static void TestMessageGuardsAndNormalization(
        string path,
        ConversationStore store)
    {
        ConversationSession session = store.CurrentSession;
        int previousCount = session.Messages.Count;
        if (store.AddMessage(
                "missing-session",
                "user",
                "must not be added") ||
            store.AddMessage(session.Id, (ConversationMessage)null) ||
            store.AddTurn(
                session.Id,
                null,
                new ConversationMessage("assistant", "unused"),
                null) ||
            session.Messages.Count != previousCount)
        {
            throw new InvalidDataException(
                "Invalid message operations changed the conversation.");
        }

        if (!store.AddMessage(
            session.Id,
            new ConversationMessage("unexpected-role", "normalized role")))
        {
            throw new InvalidOperationException(
                "A valid message was not added.");
        }

        ConversationStore saved = new ConversationStore(path);
        ConversationMessage restored = saved.CurrentSession.Messages[
            saved.CurrentSession.Messages.Count - 1];
        if (restored.Role != "user" || restored.Content != "normalized role")
        {
            throw new InvalidDataException(
                "Message role normalization did not survive reload.");
        }
    }

    private static void TestAtomicTurnRollback(
        string path,
        ConversationStore store)
    {
        ConversationSession session = store.CurrentSession;
        int previousCount = session.Messages.Count;
        string previousTitle = session.Title;
        bool failed = false;
        using (FileStream locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            try
            {
                store.AddTurn(
                    session.Id,
                    new ConversationMessage("user", "must rollback"),
                    new ConversationMessage("assistant", "must rollback"),
                    "must rollback title");
            }
            catch (IOException)
            {
                failed = true;
            }
        }

        if (!failed ||
            session.Messages.Count != previousCount ||
            session.Title != previousTitle)
        {
            throw new InvalidDataException(
                "A failed turn save did not roll back memory state.");
        }

        ConversationStore diskState = new ConversationStore(path);
        if (diskState.CurrentSession.Messages.Count != previousCount)
        {
            throw new InvalidDataException(
                "A failed turn save modified the on-disk state.");
        }

        if (!store.AddTurn(
            session.Id,
            new ConversationMessage("user", "atomic user"),
            new ConversationMessage("assistant", "atomic assistant"),
            "原子会话"))
        {
            throw new InvalidOperationException("Atomic turn was not added.");
        }

        ConversationStore saved = new ConversationStore(path);
        if (saved.CurrentSession.Messages.Count != previousCount + 2 ||
            saved.CurrentSession.Title != "原子会话")
        {
            throw new InvalidDataException(
                "A successful turn was not saved atomically.");
        }
    }

    private static void TestSessionLifecycle(
        string path,
        ConversationStore store)
    {
        ConversationSession first = store.CurrentSession;
        ConversationSession second = store.CreateSession(" second ");
        ConversationSession third = store.CreateSession("third");
        if (store.Sessions.Count != 3 ||
            store.CurrentSessionId != third.Id ||
            second.Title != "second")
        {
            throw new InvalidDataException(
                "Session creation did not update the active conversation.");
        }

        TestSessionMutationRollback(path, store, first, second, third);

        if (!store.SelectSession(first.Id) ||
            !store.RenameSession(first.Id, " renamed session ") ||
            store.CurrentSessionId != first.Id ||
            first.Title != "renamed session")
        {
            throw new InvalidDataException(
                "Session selection or rename failed.");
        }

        ConversationStore selected = new ConversationStore(path);
        ConversationSession selectedFirst = selected.GetSession(first.Id);
        if (selected.CurrentSessionId != first.Id ||
            selectedFirst == null ||
            selectedFirst.Title != "renamed session")
        {
            throw new InvalidDataException(
                "Session selection or rename did not survive reload.");
        }

        if (store.SelectSession("missing-session") ||
            store.RenameSession("missing-session", "unused") ||
            store.DeleteSession("missing-session") ||
            store.Sessions.Count != 3 ||
            store.CurrentSessionId != first.Id)
        {
            throw new InvalidDataException(
                "Missing-session operations changed store state.");
        }

        if (!store.DeleteSession(second.Id) ||
            store.CurrentSessionId != first.Id ||
            store.GetSession(second.Id) != null)
        {
            throw new InvalidDataException(
                "Deleting an inactive session changed the active session.");
        }

        if (!store.DeleteSession(first.Id) ||
            store.CurrentSession == null ||
            store.CurrentSessionId == first.Id ||
            store.Sessions.Count != 1)
        {
            throw new InvalidDataException(
                "Deleting the active session did not select a survivor.");
        }

        ConversationStore deleted = new ConversationStore(path);
        if (deleted.Sessions.Count != 1 ||
            deleted.GetSession(first.Id) != null ||
            deleted.GetSession(second.Id) != null ||
            deleted.CurrentSession == null)
        {
            throw new InvalidDataException(
                "Session deletion did not survive reload.");
        }
    }

    private static void TestSessionMutationRollback(
        string path,
        ConversationStore store,
        ConversationSession first,
        ConversationSession second,
        ConversationSession third)
    {
        string secondTitle = second.Title;
        using (FileStream locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            ExpectIOException(delegate { store.SelectSession(first.Id); });
            if (store.CurrentSessionId != third.Id)
            {
                throw new InvalidDataException(
                    "A failed selection did not roll back memory state.");
            }

            ExpectIOException(delegate
            {
                store.RenameSession(second.Id, "must rollback");
            });
            if (second.Title != secondTitle)
            {
                throw new InvalidDataException(
                    "A failed rename did not roll back memory state.");
            }

            ExpectIOException(delegate { store.DeleteSession(second.Id); });
            if (store.GetSession(second.Id) == null ||
                store.Sessions.Count != 3 ||
                store.CurrentSessionId != third.Id)
            {
                throw new InvalidDataException(
                    "A failed deletion did not roll back memory state.");
            }
        }

        ConversationStore diskState = new ConversationStore(path);
        ConversationSession diskSecond = diskState.GetSession(second.Id);
        if (diskState.Sessions.Count != 3 ||
            diskState.CurrentSessionId != third.Id ||
            diskSecond == null ||
            diskSecond.Title != secondTitle)
        {
            throw new InvalidDataException(
                "A failed session mutation modified the on-disk state.");
        }
    }

    private static void ExpectIOException(Action action)
    {
        bool failed = false;
        try
        {
            action();
        }
        catch (IOException)
        {
            failed = true;
        }

        if (!failed)
        {
            throw new InvalidDataException(
                "A locked store unexpectedly accepted a mutation.");
        }
    }

    private static void TestCreateRollback(string path)
    {
        string blocker = path + ".blocked-parent";
        string blockedStore = Path.Combine(blocker, "conversations.xml");
        if (Directory.Exists(blocker))
        {
            Directory.Delete(blocker, true);
        }

        File.WriteAllText(blocker, "block directory creation");
        ConversationStore store = new ConversationStore(blockedStore);
        bool failed = false;
        try
        {
            store.CreateSession("must rollback");
        }
        catch (IOException)
        {
            failed = true;
        }

        if (!failed || store.Sessions.Count != 0 ||
            !string.IsNullOrEmpty(store.CurrentSessionId))
        {
            throw new InvalidDataException(
                "A failed session creation did not roll back memory state.");
        }

        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        store.Save();
        ConversationStore reloaded = new ConversationStore(blockedStore);
        if (reloaded.Sessions.Count != 0)
        {
            throw new InvalidDataException(
                "A rolled-back session was persisted by a later save.");
        }

        Directory.Delete(blocker, true);
    }

    private static void TestUnpreservedDamageBlocksWrites(string path)
    {
        const string damagedContent = "<locked-broken";
        string importSourcePath = path + ".blocked-import-source.xml";
        string importBackupPath = path + ".blocked-import.fpc";
        try
        {
            ConversationStore importSource =
                new ConversationStore(importSourcePath);
            importSource.CreateSession("blocked import source");
            importSource.ExportBackup(importBackupPath);

            byte[] damagedBytes = new System.Text.UTF8Encoding(true).GetBytes(
                damagedContent);
            File.WriteAllBytes(path, damagedBytes);
            ConversationStore protectedStore;
            using (FileStream locked = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                protectedStore = new ConversationStore(path);
                if (!protectedStore.IsWriteBlocked ||
                    protectedStore.LoadWarning.IndexOf(
                        "禁止会话写入",
                        StringComparison.Ordinal) < 0)
                {
                    throw new InvalidDataException(
                        "Unpreserved damaged store did not enter write protection.");
                }

                ExpectWriteBlocked(
                    delegate { protectedStore.Save(); },
                    "explicit save");
                ExpectWriteBlocked(
                    delegate
                    {
                        protectedStore.CreateSession(
                            "must not overwrite damage");
                    },
                    "session creation");
                ExpectWriteBlocked(
                    delegate
                    {
                        protectedStore.ImportBackup(importBackupPath);
                    },
                    "backup import");

                if (protectedStore.Sessions.Count != 0 ||
                    !string.IsNullOrEmpty(protectedStore.CurrentSessionId))
                {
                    throw new InvalidDataException(
                        "A rejected write did not roll back memory state.");
                }

                AssertFileBytesEqual(
                    path,
                    damagedBytes,
                    "Write protection changed the locked damaged source.");
            }

            if (!protectedStore.IsWriteBlocked)
            {
                throw new InvalidDataException(
                    "Write protection was cleared when the file lock ended.");
            }

            ExpectWriteBlocked(
                delegate { protectedStore.Save(); },
                "save after lock release");
            AssertFileBytesEqual(
                path,
                damagedBytes,
                "Write protection changed the unlocked damaged source.");
        }
        finally
        {
            if (File.Exists(importSourcePath))
            {
                File.Delete(importSourcePath);
            }

            if (File.Exists(importBackupPath))
            {
                File.Delete(importBackupPath);
            }
        }
    }

    private static void ExpectWriteBlocked(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException exception)
        {
            if (exception.Message.IndexOf(
                "只读保护状态",
                StringComparison.Ordinal) >= 0)
            {
                return;
            }

            throw new InvalidDataException(
                operation + " failed for an unexpected reason.",
                exception);
        }

        throw new InvalidDataException(
            operation + " bypassed conversation write protection.");
    }

    private static void AssertFileBytesEqual(
        string path,
        byte[] expected,
        string message)
    {
        byte[] actual = File.ReadAllBytes(path);
        if (actual.Length != expected.Length)
        {
            throw new InvalidDataException(message);
        }

        for (int index = 0; index < actual.Length; index++)
        {
            if (actual[index] != expected[index])
            {
                throw new InvalidDataException(message);
            }
        }
    }
}
