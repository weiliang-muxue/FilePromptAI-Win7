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
}
