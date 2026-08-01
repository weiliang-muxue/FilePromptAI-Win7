using System;
using System.IO;
using System.Xml.Linq;
using FilePromptWin7;

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

            TestAtomicTurnRollback(path, reloaded);
            TestCreateRollback(path);

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
}
