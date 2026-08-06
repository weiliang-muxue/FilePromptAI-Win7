using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

using FilePromptAIWin7;

internal static class ConversationBackupSmokeTest
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(
                "Usage: ConversationBackupSmokeTest <artifact-directory>");
            return 2;
        }

        string testDirectory = Path.Combine(
            Path.GetFullPath(args[0]),
            "conversation-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(testDirectory);
            TestRoundTripAndDuplicateIds(testDirectory);
            TestInvalidBackupsDoNotChangeStore(testDirectory);
            TestAtomicWriteAndRollback(testDirectory);
            Console.WriteLine("PASS | conversation backup");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | conversation backup");
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, true);
                }
            }
            catch
            {
                // Test artifacts use a unique directory and are safe to overwrite later.
            }
        }
    }

    private static void TestRoundTripAndDuplicateIds(string directory)
    {
        string sourcePath = Path.Combine(directory, "source.xml");
        string backupPath = Path.Combine(directory, "round-trip.fpc");
        ConversationStore source = new ConversationStore(sourcePath);
        ConversationSession first = source.CreateSession("\u7B2C\u4E00\u4E2A\u4F1A\u8BDD");
        source.AddMessage(
            first.Id,
            "user",
            "\u4E2D\u6587\u95EE\u9898 <tag> & \u7B26\u53F7");
        source.AddMessage(
            first.Id,
            "assistant",
            "\u4E2D\u6587\u56DE\u7B54 \uD83D\uDE00");
        ConversationSession second = source.CreateSession("\u7B2C\u4E8C\u4E2A\u4F1A\u8BDD");
        source.AddMessage(second.Id, "system", "system prompt");
        source.AddMessage(second.Id, "tool", "tool result");
        source.SelectSession(first.Id);

        AssertEqual(2, source.ExportBackup(backupPath), "exported count");
        XDocument backup = XDocument.Load(backupPath);
        AssertEqual(
            "FilePromptAIConversationBackup",
            backup.Root.Name.LocalName,
            "backup root");
        string rawBackup = File.ReadAllText(backupPath, Encoding.UTF8);
        AssertNotContains(rawBackup, "EndpointUrl", "endpoint setting exclusion");
        AssertNotContains(rawBackup, "ApiKey", "key setting exclusion");
        AssertNotContains(rawBackup, "ModelName", "model setting exclusion");
        AssertNotContains(
            rawBackup,
            "currentSessionId",
            "current selection exclusion");

        string targetPath = Path.Combine(directory, "target.xml");
        ConversationStore target = new ConversationStore(targetPath);
        ConversationSession existing = target.CreateSession("\u73B0\u6709\u4F1A\u8BDD");
        target.AddMessage(existing.Id, "user", "keep this message");
        string currentBeforeImport = target.CurrentSessionId;

        IList<XElement> exportedSessions = backup.Root
            .Element("Sessions")
            .Elements("Session")
            .ToList();
        exportedSessions[0].SetAttributeValue("id", existing.Id);
        exportedSessions[1].SetAttributeValue("id", existing.Id.ToUpperInvariant());
        backup.Save(backupPath);

        AssertEqual(2, target.ImportBackup(backupPath), "first imported count");
        AssertEqual(3, target.Sessions.Count, "session count after import");
        AssertEqual(
            currentBeforeImport,
            target.CurrentSessionId,
            "current session preservation");
        AssertUniqueIds(target.Sessions);

        ConversationSession importedFirst = FindByTitle(
            target.Sessions,
            "\u7B2C\u4E00\u4E2A\u4F1A\u8BDD");
        ConversationSession importedSecond = FindByTitle(
            target.Sessions,
            "\u7B2C\u4E8C\u4E2A\u4F1A\u8BDD");
        AssertTrue(importedFirst != null, "first imported session");
        AssertTrue(importedSecond != null, "second imported session");
        AssertTrue(
            !string.Equals(importedFirst.Id, existing.Id, StringComparison.OrdinalIgnoreCase),
            "existing ID collision remapped");
        AssertTrue(
            !string.Equals(
                importedFirst.Id,
                importedSecond.Id,
                StringComparison.OrdinalIgnoreCase),
            "duplicate backup IDs remapped");
        AssertEqual(2, importedFirst.Messages.Count, "first message count");
        AssertEqual(
            "\u4E2D\u6587\u56DE\u7B54 \uD83D\uDE00",
            importedFirst.Messages[1].Content,
            "Unicode message round trip");
        AssertEqual("tool", importedSecond.Messages[1].Role, "tool role round trip");

        ConversationStore reloaded = new ConversationStore(targetPath);
        AssertEqual(3, reloaded.Sessions.Count, "reloaded session count");
        AssertEqual(
            currentBeforeImport,
            reloaded.CurrentSessionId,
            "reloaded current session");
        AssertUniqueIds(reloaded.Sessions);

        AssertEqual(2, target.ImportBackup(backupPath), "repeated imported count");
        AssertEqual(5, target.Sessions.Count, "repeated import session count");
        AssertUniqueIds(target.Sessions);

        string replacementPath = Path.Combine(directory, "replace-existing.fpc");
        File.WriteAllText(replacementPath, "old content", Encoding.UTF8);
        AssertEqual(2, source.ExportBackup(replacementPath), "atomic replacement count");
        XDocument.Load(replacementPath);
    }

    private static void TestInvalidBackupsDoNotChangeStore(string directory)
    {
        string storagePath = Path.Combine(directory, "guarded.xml");
        ConversationStore store = new ConversationStore(storagePath);
        ConversationSession session = store.CreateSession("guarded");
        store.AddMessage(session.Id, "user", "original content");
        string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        string validPrefix =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<FilePromptAIConversationBackup version=\"1\" exportedAt=\"" +
            now + "\"><Sessions>";
        string validSession =
            "<Session id=\"source-id\" title=\"source\" createdAt=\"" + now +
            "\" updatedAt=\"" + now + "\"><Messages>" +
            "<Message role=\"user\" createdAt=\"" + now +
            "\">content</Message></Messages></Session>";
        string validSuffix = "</Sessions></FilePromptAIConversationBackup>";

        AssertInvalidImportUnchanged(
            store,
            storagePath,
            Path.Combine(directory, "malformed.fpc"),
            "<FilePromptAIConversationBackup");
        AssertInvalidImportUnchanged(
            store,
            storagePath,
            Path.Combine(directory, "wrong-root.fpc"),
            "<ConversationStore version=\"1\" />");
        AssertInvalidImportUnchanged(
            store,
            storagePath,
            Path.Combine(directory, "unknown-setting.fpc"),
            validPrefix.Replace(
                " exportedAt=",
                " apiKey=\"must-not-import\" exportedAt=") +
            validSession + validSuffix);
        AssertInvalidImportUnchanged(
            store,
            storagePath,
            Path.Combine(directory, "invalid-role.fpc"),
            validPrefix +
            validSession.Replace("role=\"user\"", "role=\"owner\"") +
            validSuffix);
        AssertInvalidImportUnchanged(
            store,
            storagePath,
            Path.Combine(directory, "invalid-date.fpc"),
            validPrefix +
            validSession.Replace(
                "createdAt=\"" + now + "\"",
                "createdAt=\"not-a-date\"") +
            validSuffix);
        AssertInvalidImportUnchanged(
            store,
            storagePath,
            Path.Combine(directory, "nested-message.fpc"),
            validPrefix +
            validSession.Replace("content</Message>", "<b>content</b></Message>") +
            validSuffix);
        AssertInvalidImportUnchanged(
            store,
            storagePath,
            Path.Combine(directory, "dtd.fpc"),
            "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM " +
            "\"file:///does-not-exist\">]>" +
            validPrefix.Substring(validPrefix.IndexOf('<', 1)) +
            validSession.Replace("content", "&e;") + validSuffix);
        AssertInvalidImportUnchanged(
            store,
            storagePath,
            Path.Combine(directory, "active-store.fpc"),
            File.ReadAllText(storagePath, Encoding.UTF8));

        string emptyBackup = Path.Combine(directory, "empty.fpc");
        File.WriteAllText(
            emptyBackup,
            validPrefix + validSuffix,
            new UTF8Encoding(false));
        string snapshot = DescribeStore(store);
        string storageSnapshot = Convert.ToBase64String(File.ReadAllBytes(storagePath));
        AssertEqual(0, store.ImportBackup(emptyBackup), "empty backup import count");
        AssertEqual(snapshot, DescribeStore(store), "empty import memory preservation");
        AssertEqual(
            storageSnapshot,
            Convert.ToBase64String(File.ReadAllBytes(storagePath)),
            "empty import storage preservation");
    }

    private static void TestAtomicWriteAndRollback(string directory)
    {
        string sourcePath = Path.Combine(directory, "atomic-source.xml");
        ConversationStore source = new ConversationStore(sourcePath);
        ConversationSession sourceSession = source.CreateSession("source");
        source.AddMessage(sourceSession.Id, "assistant", "source message");

        string lockedBackup = Path.Combine(directory, "locked.fpc");
        byte[] originalBackup = Encoding.UTF8.GetBytes("original backup bytes");
        File.WriteAllBytes(lockedBackup, originalBackup);
        using (FileStream locked = new FileStream(
            lockedBackup,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            ExpectIOException(
                delegate { source.ExportBackup(lockedBackup); },
                "locked backup replacement");
        }

        AssertEqual(
            Convert.ToBase64String(originalBackup),
            Convert.ToBase64String(File.ReadAllBytes(lockedBackup)),
            "locked backup remains unchanged");
        AssertEqual(
            0,
            Directory.GetFiles(
                directory,
                ".locked.fpc.*.tmp",
                SearchOption.TopDirectoryOnly).Length,
            "temporary backup cleanup");

        string samePathSnapshot = Convert.ToBase64String(
            File.ReadAllBytes(sourcePath));
        ExpectException<InvalidOperationException>(
            delegate { source.ExportBackup(sourcePath); },
            "active store overwrite protection");
        AssertEqual(
            samePathSnapshot,
            Convert.ToBase64String(File.ReadAllBytes(sourcePath)),
            "active store remains unchanged");

        string validBackup = Path.Combine(directory, "valid-for-rollback.fpc");
        source.ExportBackup(validBackup);
        string targetPath = Path.Combine(directory, "rollback-target.xml");
        ConversationStore target = new ConversationStore(targetPath);
        ConversationSession targetSession = target.CreateSession("target");
        target.AddMessage(targetSession.Id, "user", "target message");
        string memoryBefore = DescribeStore(target);
        string fileBefore = Convert.ToBase64String(File.ReadAllBytes(targetPath));
        using (FileStream locked = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            ExpectIOException(
                delegate { target.ImportBackup(validBackup); },
                "failed import persistence");
        }

        AssertEqual(memoryBefore, DescribeStore(target), "failed import rollback");
        AssertEqual(
            fileBefore,
            Convert.ToBase64String(File.ReadAllBytes(targetPath)),
            "failed import storage preservation");
        AssertEqual(
            0,
            Directory.GetFiles(
                directory,
                ".rollback-target.xml.*.tmp",
                SearchOption.TopDirectoryOnly).Length,
            "temporary storage cleanup");
    }

    private static void AssertInvalidImportUnchanged(
        ConversationStore store,
        string storagePath,
        string backupPath,
        string content)
    {
        File.WriteAllText(backupPath, content, new UTF8Encoding(false));
        string memoryBefore = DescribeStore(store);
        string fileBefore = Convert.ToBase64String(File.ReadAllBytes(storagePath));
        ExpectException<InvalidDataException>(
            delegate { store.ImportBackup(backupPath); },
            "invalid backup rejection");
        AssertEqual(memoryBefore, DescribeStore(store), "invalid import memory");
        AssertEqual(
            fileBefore,
            Convert.ToBase64String(File.ReadAllBytes(storagePath)),
            "invalid import storage");
    }

    private static string DescribeStore(ConversationStore store)
    {
        StringBuilder value = new StringBuilder();
        value.Append(store.CurrentSessionId);
        foreach (ConversationSession session in store.Sessions)
        {
            value.Append('|').Append(session.Id);
            value.Append('|').Append(session.Title);
            value.Append('|').Append(session.CreatedAt.ToString("o"));
            value.Append('|').Append(session.UpdatedAt.ToString("o"));
            if (session.Messages == null)
            {
                continue;
            }

            foreach (ConversationMessage message in session.Messages)
            {
                value.Append('|').Append(message.Role);
                value.Append('|').Append(message.CreatedAt.ToString("o"));
                value.Append('|').Append(message.Content);
            }
        }

        return Convert.ToBase64String(
            Encoding.UTF8.GetBytes(value.ToString()));
    }

    private static ConversationSession FindByTitle(
        IList<ConversationSession> sessions,
        string title)
    {
        return sessions.FirstOrDefault(
            delegate(ConversationSession session)
            {
                return session != null &&
                    string.Equals(session.Title, title, StringComparison.Ordinal);
            });
    }

    private static void AssertUniqueIds(IList<ConversationSession> sessions)
    {
        HashSet<string> ids = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ConversationSession session in sessions)
        {
            AssertTrue(session != null, "non-null session");
            AssertTrue(ids.Add(session.Id), "unique session ID");
        }
    }

    private static void ExpectIOException(Action action, string name)
    {
        ExpectException<IOException>(action, name);
    }

    private static void ExpectException<T>(Action action, string name)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(name + " did not throw " + typeof(T).Name + ".");
    }

    private static void AssertNotContains(
        string actual,
        string unexpected,
        string name)
    {
        if (actual.IndexOf(unexpected, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidOperationException(
                name + " unexpectedly contained [" + unexpected + "].");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                name + " mismatch. Expected [" + expected + "] but got [" + actual + "].");
        }
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " failed.");
        }
    }
}
