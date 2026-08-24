using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
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
            TestRoundTripAndIdCollisions(testDirectory);
            TestImportRemapsBranchProvenance(testDirectory);
            TestInvalidBackupsDoNotChangeStore(testDirectory);
            TestArchiveResolutionIsTransactional(testDirectory);
            TestCapacityLimitsAreTransactional(testDirectory);
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

    private static void TestRoundTripAndIdCollisions(string directory)
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
            "imported IDs remain distinct");
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

    private static void TestImportRemapsBranchProvenance(string directory)
    {
        string sourcePath = Path.Combine(directory, "branch-source.xml");
        string backupPath = Path.Combine(directory, "branch-source.fpc");
        ConversationStore source = new ConversationStore(sourcePath);
        ConversationSession sourceSession = source.CreateSession(
            "provenance source");
        source.AddMessage(sourceSession.Id, "user", "branch point");
        ConversationSession sourceBranch = source.CloneSessionFromMessage(
            sourceSession.Id,
            sourceSession.Messages[0].Id,
            "provenance branch");
        source.ExportBackup(backupPath);

        string targetPath = Path.Combine(directory, "branch-target.xml");
        ConversationStore target = new ConversationStore(targetPath);
        ConversationSession existing = target.CreateSession("existing");
        XDocument backup = XDocument.Load(backupPath);
        XElement sourceElement = backup.Root
            .Element("Sessions")
            .Elements("Session")
            .First(element =>
                (string)element.Attribute("id") == sourceSession.Id);
        XElement branchElement = backup.Root
            .Element("Sessions")
            .Elements("Session")
            .First(element =>
                (string)element.Attribute("id") == sourceBranch.Id);
        sourceElement.SetAttributeValue("id", existing.Id);
        branchElement.SetAttributeValue("sourceSessionId", existing.Id);
        backup.Save(backupPath);

        AssertEqual(2, target.ImportBackup(backupPath), "branch import count");
        ConversationSession firstSource = target.Sessions
            .Where(session => session.Title == "provenance source")
            .Last();
        ConversationSession firstBranch = target.Sessions
            .Where(session => session.Title == "provenance branch")
            .Last();
        AssertTrue(
            !string.Equals(
                existing.Id,
                firstSource.Id,
                StringComparison.OrdinalIgnoreCase),
            "imported source collision remap");
        AssertEqual(
            firstSource.Id,
            firstBranch.SourceSessionId,
            "first import branch provenance");
        AssertEqual(
            sourceSession.Messages[0].Id,
            firstBranch.SourceMessageId,
            "first import source message provenance");

        AssertEqual(
            2,
            target.ImportBackup(backupPath),
            "repeated branch import count");
        ConversationSession secondSource = target.Sessions
            .Where(session => session.Title == "provenance source")
            .Last();
        ConversationSession secondBranch = target.Sessions
            .Where(session => session.Title == "provenance branch")
            .Last();
        AssertTrue(
            !string.Equals(
                firstSource.Id,
                secondSource.Id,
                StringComparison.OrdinalIgnoreCase),
            "repeated source ID remap");
        AssertTrue(
            !string.Equals(
                firstBranch.Id,
                secondBranch.Id,
                StringComparison.OrdinalIgnoreCase),
            "repeated branch ID remap");
        AssertEqual(
            secondSource.Id,
            secondBranch.SourceSessionId,
            "repeated import branch provenance");

        ConversationStore reloaded = new ConversationStore(targetPath);
        ConversationSession reloadedBranch = reloaded.GetSession(
            secondBranch.Id);
        AssertTrue(reloadedBranch != null, "reloaded imported branch");
        AssertEqual(
            secondSource.Id,
            reloadedBranch.SourceSessionId,
            "reloaded branch provenance");

        XDocument duplicate = new XDocument(backup);
        IList<XElement> duplicateSessions = duplicate.Root
            .Element("Sessions")
            .Elements("Session")
            .ToList();
        duplicateSessions[1].SetAttributeValue(
            "id",
            ((string)duplicateSessions[0].Attribute("id")).ToUpperInvariant());
        AssertInvalidImportUnchanged(
            target,
            targetPath,
            Path.Combine(directory, "duplicate-session-ids.fpc"),
            duplicate.ToString(SaveOptions.DisableFormatting));
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

    private static void TestArchiveResolutionIsTransactional(string directory)
    {
        string sortingPath = Path.Combine(directory, "archive-sorting.xml");
        ConversationStore sortingStore = new ConversationStore(sortingPath);
        ConversationSession current = sortingStore.CreateSession("current");
        ConversationSession recentUnpinned = sortingStore.CreateSession(
            "recent unpinned");
        ConversationSession olderPinned = sortingStore.CreateSession(
            "older pinned");
        ConversationSession newerPinned = sortingStore.CreateSession(
            "newer pinned");
        DateTime now = DateTime.UtcNow;
        current.UpdatedAt = now;
        recentUnpinned.UpdatedAt = now.AddMinutes(-1);
        olderPinned.IsPinned = true;
        olderPinned.UpdatedAt = now.AddMinutes(-3);
        newerPinned.IsPinned = true;
        newerPinned.UpdatedAt = now.AddMinutes(-2);
        sortingStore.SelectSession(current.Id);

        AssertTrue(
            sortingStore.SetSessionArchivedAndResolveCurrent(
                current.Id,
                true,
                "unused replacement"),
            "archive current with fallback");
        AssertTrue(current.IsArchived, "archived current state");
        AssertEqual(
            newerPinned.Id,
            sortingStore.CurrentSessionId,
            "pinned and updated fallback sorting");
        AssertEqual(4, sortingStore.Sessions.Count, "fallback avoids creation");

        AssertTrue(
            sortingStore.SetSessionArchivedAndResolveCurrent(
                current.Id,
                false,
                "unused replacement"),
            "unarchive session");
        AssertTrue(!current.IsArchived, "unarchived state");
        AssertEqual(
            current.Id,
            sortingStore.CurrentSessionId,
            "unarchived session becomes current");
        ConversationStore sortingReloaded = new ConversationStore(sortingPath);
        AssertEqual(
            current.Id,
            sortingReloaded.CurrentSessionId,
            "unarchive current persistence");
        AssertTrue(
            !sortingReloaded.GetSession(current.Id).IsArchived,
            "unarchive state persistence");

        string uniquePath = Path.Combine(directory, "archive-unique.xml");
        ConversationStore uniqueStore = new ConversationStore(uniquePath);
        ConversationSession only = uniqueStore.CreateSession("only active");
        AssertTrue(
            uniqueStore.SetSessionArchivedAndResolveCurrent(
                only.Id,
                true,
                " replacement "),
            "archive only active session");
        ConversationSession replacement = uniqueStore.CurrentSession;
        AssertTrue(only.IsArchived, "only session archived");
        AssertTrue(replacement != null, "replacement current exists");
        AssertEqual("replacement", replacement.Title, "replacement title");
        AssertEqual(2, uniqueStore.Sessions.Count, "one replacement created");
        AssertEqual(
            1,
            uniqueStore.Sessions.Count(session => !session.IsArchived),
            "unique active replacement");
        ConversationStore uniqueReloaded = new ConversationStore(uniquePath);
        AssertEqual(2, uniqueReloaded.Sessions.Count, "replacement persistence");
        AssertEqual(
            replacement.Id,
            uniqueReloaded.CurrentSessionId,
            "replacement current persistence");

        string lockedPath = Path.Combine(directory, "archive-locked.xml");
        ConversationStore lockedStore = new ConversationStore(lockedPath);
        ConversationSession lockedOnly = lockedStore.CreateSession(
            "locked only");
        string lockedMemoryBefore = DescribeStore(lockedStore);
        string lockedFileBefore = Convert.ToBase64String(
            File.ReadAllBytes(lockedPath));
        DateTime lockedUpdatedAtBefore = lockedOnly.UpdatedAt;
        using (FileStream locked = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            ExpectIOException(
                delegate
                {
                    lockedStore.SetSessionArchivedAndResolveCurrent(
                        lockedOnly.Id,
                        true,
                        "must rollback");
                },
                "archive persistence failure");
        }

        AssertEqual(
            lockedMemoryBefore,
            DescribeStore(lockedStore),
            "archive persistence memory rollback");
        AssertEqual(
            lockedOnly.Id,
            lockedStore.CurrentSessionId,
            "archive persistence current rollback");
        AssertTrue(
            !lockedOnly.IsArchived,
            "archive persistence state rollback");
        AssertEqual(
            lockedUpdatedAtBefore,
            lockedOnly.UpdatedAt,
            "archive persistence timestamp rollback");
        AssertEqual(
            1,
            lockedStore.Sessions.Count,
            "archive persistence replacement rollback");
        AssertEqual(
            lockedFileBefore,
            Convert.ToBase64String(File.ReadAllBytes(lockedPath)),
            "archive persistence storage preservation");
    }

    private static void TestCapacityLimitsAreTransactional(string directory)
    {
        const int maximumSessions = 10000;
        const int maximumMessages = 100000;

        string fullStorePath = Path.Combine(directory, "full-store.xml");
        WriteFullConversationStore(fullStorePath, maximumSessions);
        ConversationStore fullStore = new ConversationStore(fullStorePath);
        AssertEqual(
            maximumSessions,
            fullStore.Sessions.Count,
            "session limit load");
        fullStore.Save();
        string fullMemoryBefore = DescribeStore(fullStore);
        string fullFileBefore = Convert.ToBase64String(
            File.ReadAllBytes(fullStorePath));
        string fullCurrentBefore = fullStore.CurrentSessionId;

        ExpectException<InvalidOperationException>(
            delegate { fullStore.CreateSession("one too many"); },
            "session limit create");
        AssertEqual(
            fullMemoryBefore,
            DescribeStore(fullStore),
            "session limit create rollback");
        AssertEqual(
            fullCurrentBefore,
            fullStore.CurrentSessionId,
            "session limit current session rollback");
        AssertEqual(
            fullFileBefore,
            Convert.ToBase64String(File.ReadAllBytes(fullStorePath)),
            "session limit create storage preservation");

        string oneSessionBackup = Path.Combine(
            directory,
            "one-session-over-limit.fpc");
        WriteSingleSessionBackup(oneSessionBackup);
        ExpectException<InvalidOperationException>(
            delegate { fullStore.ImportBackup(oneSessionBackup); },
            "aggregate import session limit");
        AssertEqual(
            fullMemoryBefore,
            DescribeStore(fullStore),
            "aggregate import rollback");
        AssertEqual(
            fullCurrentBefore,
            fullStore.CurrentSessionId,
            "aggregate import current session preservation");
        AssertEqual(
            fullFileBefore,
            Convert.ToBase64String(File.ReadAllBytes(fullStorePath)),
            "aggregate import storage preservation");

        ConversationSession fullCurrent = fullStore.CurrentSession;
        foreach (ConversationSession session in fullStore.Sessions)
        {
            if (!object.ReferenceEquals(session, fullCurrent))
            {
                session.IsArchived = true;
            }
        }

        string archiveCapacityMemoryBefore = DescribeStore(fullStore);
        DateTime archiveCapacityUpdatedAtBefore = fullCurrent.UpdatedAt;
        ExpectException<InvalidOperationException>(
            delegate
            {
                fullStore.SetSessionArchivedAndResolveCurrent(
                    fullCurrent.Id,
                    true,
                    "capacity replacement");
            },
            "archive replacement session limit");
        AssertEqual(
            archiveCapacityMemoryBefore,
            DescribeStore(fullStore),
            "archive capacity memory rollback");
        AssertEqual(
            maximumSessions,
            fullStore.Sessions.Count,
            "archive capacity replacement rollback");
        AssertEqual(
            fullCurrent.Id,
            fullStore.CurrentSessionId,
            "archive capacity current rollback");
        AssertTrue(
            !fullCurrent.IsArchived,
            "archive capacity state rollback");
        AssertEqual(
            archiveCapacityUpdatedAtBefore,
            fullCurrent.UpdatedAt,
            "archive capacity timestamp rollback");
        AssertEqual(
            fullFileBefore,
            Convert.ToBase64String(File.ReadAllBytes(fullStorePath)),
            "archive capacity storage preservation");

        string messageStorePath = Path.Combine(directory, "full-messages.xml");
        ConversationStore messageStore = new ConversationStore(
            messageStorePath);
        ConversationSession messageSession = messageStore.CreateSession(
            "message limit");
        DateTime messageTime = DateTime.UtcNow;
        List<ConversationMessage> messages =
            new List<ConversationMessage>(maximumMessages + 1);
        for (int index = 0; index < maximumMessages; index++)
        {
            messages.Add(new ConversationMessage(
                "user",
                string.Empty,
                messageTime,
                "message-" + index.ToString(CultureInfo.InvariantCulture),
                string.Empty,
                0));
        }

        messageSession.Messages = messages;
        messageStore.Save();
        string messageFileBefore = Convert.ToBase64String(
            File.ReadAllBytes(messageStorePath));
        messages.Add(new ConversationMessage("assistant", "direct overflow"));
        ExpectException<InvalidOperationException>(
            delegate { messageStore.Save(); },
            "message limit save");
        AssertEqual(
            messageFileBefore,
            Convert.ToBase64String(File.ReadAllBytes(messageStorePath)),
            "message limit save storage preservation");
        messages.RemoveAt(messages.Count - 1);

        DateTime updatedAtBefore = messageSession.UpdatedAt;
        ExpectException<InvalidOperationException>(
            delegate
            {
                messageStore.AddMessage(
                    messageSession.Id,
                    "assistant",
                    "one too many");
            },
            "message limit add");
        AssertTrue(
            object.ReferenceEquals(messages, messageSession.Messages),
            "message limit collection rollback");
        AssertEqual(
            maximumMessages,
            messageSession.Messages.Count,
            "message limit count rollback");
        AssertEqual(
            "message-99999",
            messageSession.Messages[maximumMessages - 1].Id,
            "message limit tail rollback");
        AssertEqual(
            updatedAtBefore,
            messageSession.UpdatedAt,
            "message limit timestamp rollback");
        AssertEqual(
            messageFileBefore,
            Convert.ToBase64String(File.ReadAllBytes(messageStorePath)),
            "message limit add storage preservation");
    }

    private static void WriteFullConversationStore(
        string path,
        int sessionCount)
    {
        string timestamp = DateTime.UtcNow.ToString(
            "o",
            CultureInfo.InvariantCulture);
        XmlWriterSettings settings = new XmlWriterSettings();
        settings.Encoding = new UTF8Encoding(false);
        settings.Indent = false;
        using (XmlWriter writer = XmlWriter.Create(path, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("ConversationStore");
            writer.WriteAttributeString("version", "1");
            writer.WriteAttributeString("currentSessionId", "session-0");
            for (int index = 0; index < sessionCount; index++)
            {
                writer.WriteStartElement("Session");
                writer.WriteAttributeString(
                    "id",
                    "session-" + index.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("title", "session");
                writer.WriteAttributeString("createdAt", timestamp);
                writer.WriteAttributeString("updatedAt", timestamp);
                writer.WriteStartElement("Messages");
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
    }

    private static void WriteSingleSessionBackup(string path)
    {
        string timestamp = DateTime.UtcNow.ToString(
            "o",
            CultureInfo.InvariantCulture);
        XDocument document = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(
                "FilePromptAIConversationBackup",
                new XAttribute("version", "1"),
                new XAttribute("exportedAt", timestamp),
                new XElement(
                    "Sessions",
                    new XElement(
                        "Session",
                        new XAttribute("id", "imported-session"),
                        new XAttribute("title", "imported"),
                        new XAttribute("createdAt", timestamp),
                        new XAttribute("updatedAt", timestamp),
                        new XElement("Messages")))));
        document.Save(path);
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
            value.Append('|').Append(session.IsPinned);
            value.Append('|').Append(session.IsArchived);
            value.Append('|').Append(session.SourceSessionId);
            value.Append('|').Append(session.SourceMessageId);
            if (session.Messages == null)
            {
                continue;
            }

            foreach (ConversationMessage message in session.Messages)
            {
                value.Append('|').Append(message.Id);
                value.Append('|').Append(message.ParentMessageId);
                value.Append('|').Append(message.VariantIndex);
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
