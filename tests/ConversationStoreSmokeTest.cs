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
            TestSessionStatePersistence(path, reloaded);
            TestSessionBranching(path, reloaded);
            TestMessageSuffixReplacement(path, reloaded);
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

    private static void TestSessionStatePersistence(
        string path,
        ConversationStore store)
    {
        ConversationSession session = store.CurrentSession;
        if (session == null ||
            !store.SetSessionPinned(session.Id, true) ||
            !store.SetSessionArchived(session.Id, true) ||
            !session.IsPinned ||
            !session.IsArchived)
        {
            throw new InvalidDataException(
                "Session pin or archive state was not updated.");
        }

        if (store.SetSessionPinned("missing-session", true) ||
            store.SetSessionArchived("missing-session", true) ||
            store.Sessions.Count != 1 ||
            store.CurrentSessionId != session.Id)
        {
            throw new InvalidDataException(
                "A missing-session state change modified the store.");
        }

        DateTime pinnedUpdatedAt = session.UpdatedAt;
        using (FileStream locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            ExpectIOException(delegate
            {
                store.SetSessionPinned(session.Id, false);
            });
            if (!session.IsPinned || session.UpdatedAt != pinnedUpdatedAt)
            {
                throw new InvalidDataException(
                    "A failed pin change did not roll back memory state.");
            }

            ExpectIOException(delegate
            {
                store.SetSessionArchived(session.Id, false);
            });
            if (!session.IsArchived || session.UpdatedAt != pinnedUpdatedAt)
            {
                throw new InvalidDataException(
                    "A failed archive change did not roll back memory state.");
            }
        }

        ConversationStore enabled = new ConversationStore(path);
        ConversationSession enabledSession = enabled.GetSession(session.Id);
        if (enabledSession == null ||
            !enabledSession.IsPinned ||
            !enabledSession.IsArchived)
        {
            throw new InvalidDataException(
                "Session pin or archive state did not survive reload.");
        }

        if (!store.SetSessionPinned(session.Id, false) ||
            !store.SetSessionArchived(session.Id, false))
        {
            throw new InvalidOperationException(
                "Session pin or archive state was not cleared.");
        }

        ConversationStore cleared = new ConversationStore(path);
        ConversationSession clearedSession = cleared.GetSession(session.Id);
        if (clearedSession == null ||
            clearedSession.IsPinned ||
            clearedSession.IsArchived)
        {
            throw new InvalidDataException(
                "Cleared session state did not survive reload.");
        }
    }

    private static void TestSessionBranching(
        string path,
        ConversationStore store)
    {
        ConversationSession source = store.CurrentSession;
        ConversationMessage parent = source.Messages[
            source.Messages.Count - 1];
        ConversationMessage branchPoint = new ConversationMessage(
            "assistant",
            "branch boundary",
            DateTime.UtcNow.AddMinutes(-1),
            null,
            parent.Id,
            2);
        if (!store.AddMessage(source.Id, branchPoint))
        {
            throw new InvalidOperationException(
                "Branch boundary message was not added.");
        }

        int sessionCount = store.Sessions.Count;
        string currentSessionId = store.CurrentSessionId;
        if (store.CloneSessionFromMessage(
                "missing-session",
                source.Messages[0].Id,
                "unused") != null ||
            store.CloneSessionFromMessage(
                source.Id,
                "missing-message",
                "unused") != null ||
            store.CloneSessionFromMessage(source.Id, null, "unused") != null ||
            store.Sessions.Count != sessionCount ||
            store.CurrentSessionId != currentSessionId)
        {
            throw new InvalidDataException(
                "An invalid branch request changed the conversation store.");
        }

        using (FileStream locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            ExpectIOException(delegate
            {
                store.CloneSessionFromMessage(
                    source.Id,
                    source.Messages[0].Id,
                    "must rollback");
            });
        }

        if (store.Sessions.Count != sessionCount ||
            store.CurrentSessionId != currentSessionId)
        {
            throw new InvalidDataException(
                "A failed branch save did not roll back memory state.");
        }

        ConversationStore unchanged = new ConversationStore(path);
        if (unchanged.Sessions.Count != sessionCount ||
            unchanged.CurrentSessionId != currentSessionId)
        {
            throw new InvalidDataException(
                "A failed branch save modified the on-disk state.");
        }

        TestBranchBoundary(
            path,
            store,
            source,
            0,
            "   ",
            source.Title);
        TestBranchBoundary(
            path,
            store,
            source,
            source.Messages.Count - 1,
            " last branch ",
            "last branch");
    }

    private static void TestMessageSuffixReplacement(
        string path,
        ConversationStore store)
    {
        int previousSessionCount = store.Sessions.Count;
        string previousCurrentSessionId = store.CurrentSessionId;
        ConversationSession session = store.CreateSession(
            "suffix replacement");
        ConversationMessage firstUser = new ConversationMessage(
            "user",
            "first user",
            new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            "suffix-user-1",
            null,
            0);
        ConversationMessage firstAssistant = new ConversationMessage(
            "assistant",
            "first assistant",
            new DateTime(2025, 1, 2, 3, 5, 6, DateTimeKind.Utc),
            "suffix-assistant-1",
            firstUser.Id,
            1);
        ConversationMessage latestUser = new ConversationMessage(
            "user",
            "latest user",
            new DateTime(2025, 1, 2, 3, 6, 7, DateTimeKind.Utc),
            "suffix-user-2",
            firstAssistant.Id,
            0);
        ConversationMessage oldAssistant = new ConversationMessage(
            "assistant",
            "old assistant suffix",
            new DateTime(2025, 1, 2, 3, 7, 8, DateTimeKind.Utc),
            "suffix-assistant-old",
            latestUser.Id,
            2);
        if (!store.AddMessage(session.Id, firstUser) ||
            !store.AddMessage(session.Id, firstAssistant) ||
            !store.AddMessage(session.Id, latestUser) ||
            !store.AddMessage(session.Id, oldAssistant))
        {
            throw new InvalidOperationException(
                "The suffix replacement fixture was not created.");
        }

        ConversationMessage replacementAssistant = new ConversationMessage(
            "assistant",
            "replacement assistant",
            new DateTime(2025, 1, 2, 3, 8, 9, DateTimeKind.Utc),
            "suffix-assistant-new",
            latestUser.Id,
            7);
        if (!store.ReplaceMessageSuffix(
                session.Id,
                latestUser.Id,
                new ConversationMessage[] { replacementAssistant }))
        {
            throw new InvalidOperationException(
                "The assistant suffix was not replaced.");
        }

        ConversationMessage[] expectedMessages = new ConversationMessage[]
        {
            firstUser,
            firstAssistant,
            latestUser,
            replacementAssistant
        };
        AssertMessageSequence(
            expectedMessages,
            session,
            "The successful suffix replacement changed message metadata.");
        for (int index = 0; index < session.Messages.Count; index++)
        {
            if (session.Messages[index].Id == oldAssistant.Id ||
                session.Messages[index].Content == oldAssistant.Content)
            {
                throw new InvalidDataException(
                    "The old assistant suffix was not truncated.");
            }
        }

        ConversationStore saved = new ConversationStore(path);
        ConversationSession restored = saved.GetSession(session.Id);
        if (restored == null ||
            restored.CreatedAt != session.CreatedAt ||
            restored.UpdatedAt != session.UpdatedAt)
        {
            throw new InvalidDataException(
                "Suffix replacement session timestamps did not survive reload.");
        }

        AssertMessageSequence(
            expectedMessages,
            restored,
            "Suffix replacement message metadata did not survive reload.");

        object stableMessages = session.Messages;
        DateTime stableUpdatedAt = session.UpdatedAt;
        if (store.ReplaceMessageSuffix(
                "missing-session",
                latestUser.Id,
                new ConversationMessage[] { replacementAssistant }) ||
            store.ReplaceMessageSuffix(
                session.Id,
                "missing-anchor",
                new ConversationMessage[] { replacementAssistant }) ||
            store.ReplaceMessageSuffix(session.Id, latestUser.Id, null) ||
            store.ReplaceMessageSuffix(
                session.Id,
                latestUser.Id,
                new ConversationMessage[] { null }) ||
            store.ReplaceMessageSuffix(
                session.Id,
                latestUser.Id,
                new ConversationMessage[]
                {
                    new ConversationMessage(
                        "assistant",
                        "unknown parent",
                        DateTime.UtcNow,
                        "suffix-unknown-parent",
                        "missing-parent",
                        0)
                }) ||
            store.ReplaceMessageSuffix(
                session.Id,
                latestUser.Id,
                new ConversationMessage[]
                {
                    new ConversationMessage(
                        "assistant",
                        "duplicate identifier",
                        DateTime.UtcNow,
                        firstUser.Id,
                        latestUser.Id,
                        0)
                }))
        {
            throw new InvalidDataException(
                "An invalid suffix replacement was accepted.");
        }

        if (!object.ReferenceEquals(session.Messages, stableMessages) ||
            session.UpdatedAt != stableUpdatedAt)
        {
            throw new InvalidDataException(
                "An invalid suffix replacement changed memory state.");
        }

        AssertMessageSequence(
            expectedMessages,
            session,
            "An invalid suffix replacement changed message state.");
        ConversationStore unchanged = new ConversationStore(path);
        ConversationSession unchangedSession = unchanged.GetSession(session.Id);
        if (unchangedSession == null ||
            unchangedSession.UpdatedAt != stableUpdatedAt)
        {
            throw new InvalidDataException(
                "An invalid suffix replacement changed persisted timestamps.");
        }

        AssertMessageSequence(
            expectedMessages,
            unchangedSession,
            "An invalid suffix replacement changed the on-disk state.");

        ConversationMessage lockedReplacement = new ConversationMessage(
            "assistant",
            "must roll back",
            new DateTime(2025, 1, 2, 3, 9, 10, DateTimeKind.Utc),
            "suffix-assistant-locked",
            latestUser.Id,
            8);
        using (FileStream locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            ExpectIOException(delegate
            {
                store.ReplaceMessageSuffix(
                    session.Id,
                    latestUser.Id,
                    new ConversationMessage[] { lockedReplacement });
            });
        }

        if (!object.ReferenceEquals(session.Messages, stableMessages) ||
            session.UpdatedAt != stableUpdatedAt)
        {
            throw new InvalidDataException(
                "A failed suffix replacement did not roll back memory state.");
        }

        AssertMessageSequence(
            expectedMessages,
            session,
            "A failed suffix replacement changed message state.");
        ConversationStore rolledBack = new ConversationStore(path);
        ConversationSession rolledBackSession = rolledBack.GetSession(session.Id);
        if (rolledBackSession == null ||
            rolledBackSession.UpdatedAt != stableUpdatedAt)
        {
            throw new InvalidDataException(
                "A failed suffix replacement changed persisted timestamps.");
        }

        AssertMessageSequence(
            expectedMessages,
            rolledBackSession,
            "A failed suffix replacement changed the on-disk messages.");

        if (!store.SelectSession(previousCurrentSessionId) ||
            !store.DeleteSession(session.Id) ||
            store.Sessions.Count != previousSessionCount ||
            store.CurrentSessionId != previousCurrentSessionId)
        {
            throw new InvalidDataException(
                "Suffix replacement test cleanup did not restore the store.");
        }
    }

    private static void AssertMessageSequence(
        ConversationMessage[] expected,
        ConversationSession actual,
        string failureMessage)
    {
        if (actual == null || actual.Messages == null ||
            actual.Messages.Count != expected.Length)
        {
            throw new InvalidDataException(failureMessage);
        }

        for (int index = 0; index < expected.Length; index++)
        {
            ConversationMessage expectedMessage = expected[index];
            ConversationMessage actualMessage = actual.Messages[index];
            if (actualMessage == null ||
                expectedMessage.Id != actualMessage.Id ||
                expectedMessage.ParentMessageId !=
                    actualMessage.ParentMessageId ||
                expectedMessage.VariantIndex != actualMessage.VariantIndex ||
                expectedMessage.Role != actualMessage.Role ||
                expectedMessage.Content != actualMessage.Content ||
                expectedMessage.CreatedAt != actualMessage.CreatedAt)
            {
                throw new InvalidDataException(failureMessage);
            }
        }
    }

    private static void TestBranchBoundary(
        string path,
        ConversationStore store,
        ConversationSession source,
        int messageIndex,
        string requestedTitle,
        string expectedTitle)
    {
        int previousSessionCount = store.Sessions.Count;
        int sourceMessageCount = source.Messages.Count;
        ConversationMessage anchor = source.Messages[messageIndex];
        ConversationSession branch = store.CloneSessionFromMessage(
            source.Id,
            anchor.Id,
            requestedTitle);
        if (branch == null ||
            branch.Id == source.Id ||
            branch.Title != expectedTitle ||
            branch.SourceSessionId != source.Id ||
            branch.SourceMessageId != anchor.Id ||
            branch.Messages.Count != messageIndex + 1 ||
            branch.IsPinned ||
            branch.IsArchived ||
            store.Sessions.Count != previousSessionCount + 1 ||
            store.CurrentSessionId != branch.Id ||
            source.Messages.Count != sourceMessageCount)
        {
            throw new InvalidDataException(
                "Session branching did not preserve the expected boundary.");
        }

        AssertMessagePrefix(source, branch, messageIndex);

        ConversationStore reloaded = new ConversationStore(path);
        ConversationSession restoredBranch = reloaded.GetSession(branch.Id);
        if (restoredBranch == null ||
            reloaded.CurrentSessionId != branch.Id ||
            restoredBranch.Title != expectedTitle ||
            restoredBranch.SourceSessionId != source.Id ||
            restoredBranch.SourceMessageId != anchor.Id ||
            restoredBranch.Messages.Count != messageIndex + 1)
        {
            throw new InvalidDataException(
                "Session branch metadata did not survive reload.");
        }

        AssertMessagePrefix(source, restoredBranch, messageIndex);
        if (!store.DeleteSession(branch.Id) ||
            store.Sessions.Count != previousSessionCount ||
            store.CurrentSessionId != source.Id ||
            store.GetSession(branch.Id) != null)
        {
            throw new InvalidDataException(
                "Branch cleanup did not restore the source session.");
        }

        ConversationStore cleaned = new ConversationStore(path);
        if (cleaned.Sessions.Count != previousSessionCount ||
            cleaned.CurrentSessionId != source.Id ||
            cleaned.GetSession(branch.Id) != null)
        {
            throw new InvalidDataException(
                "Branch cleanup did not survive reload.");
        }
    }

    private static void AssertMessagePrefix(
        ConversationSession source,
        ConversationSession branch,
        int lastMessageIndex)
    {
        for (int index = 0; index <= lastMessageIndex; index++)
        {
            ConversationMessage expected = source.Messages[index];
            ConversationMessage actual = branch.Messages[index];
            if (object.ReferenceEquals(expected, actual) ||
                expected.Id != actual.Id ||
                expected.ParentMessageId != actual.ParentMessageId ||
                expected.VariantIndex != actual.VariantIndex ||
                expected.Role != actual.Role ||
                expected.Content != actual.Content ||
                expected.CreatedAt != actual.CreatedAt)
            {
                throw new InvalidDataException(
                    "A branched message was not copied exactly.");
            }
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
