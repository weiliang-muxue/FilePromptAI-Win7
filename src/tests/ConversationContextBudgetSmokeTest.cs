using System;
using System.Collections.Generic;
using FilePromptAIWin7;

internal static class ConversationContextBudgetSmokeTest
{
    private static int Main()
    {
        try
        {
            TestKeepsNewestCompleteTurnsWithinReservedBudget();
            TestDoesNotSplitSurrogatePairOrTurn();
            TestDoesNotSkipOversizedRecentTurn();
            TestIgnoresIncompleteAndMalformedMessages();
            TestNullHistoryAndInvalidArguments();
            Console.WriteLine("PASS | conversation context budget");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | " + exception);
            return 1;
        }
    }

    private static void TestKeepsNewestCompleteTurnsWithinReservedBudget()
    {
        List<ConversationMessage> history = new List<ConversationMessage>
        {
            Message("user", "old-user"),
            Message("assistant", "old-answer"),
            Message("user", "new-user"),
            Message("assistant", "new-answer")
        };
        long newestTurnCharacters = Characters("new-user") +
            Characters("new-answer");
        long reserved = 4L + 5L + 6L;
        ConversationContextSelection result =
            ConversationContextBudget.SelectRecentCompleteTurns(
                history,
                reserved + newestTurnCharacters,
                4L,
                5L,
                6L);

        AssertEqual(4, result.OriginalMessageCount, "original messages");
        AssertEqual(2, result.SelectedMessageCount, "selected messages");
        AssertEqual(2, result.OriginalTurnCount, "original turns");
        AssertEqual(1, result.SelectedTurnCount, "selected turns");
        AssertEqual(
            Characters("old-user") + Characters("old-answer") +
                newestTurnCharacters,
            result.OriginalCharacterCount,
            "original chars");
        AssertEqual(reserved, result.ReservedCharacterCount, "reserved chars");
        AssertEqual(
            newestTurnCharacters,
            result.AvailableHistoryCharacterBudget,
            "available history chars");
        AssertEqual(
            newestTurnCharacters,
            result.SelectedCharacterCount,
            "selected chars");
        AssertTrue(result.WasTruncated, "old turn should be truncated");
        AssertEqual("new-user", result.Messages[0].Content, "newest user");
        AssertEqual(
            "new-answer",
            result.Messages[1].Content,
            "newest assistant");

        history[2].Content = "mutated";
        AssertEqual(
            "new-user",
            result.Messages[0].Content,
            "selection must clone source messages");

        bool readOnly = false;
        try
        {
            result.Messages.Add(Message("user", "not allowed"));
        }
        catch (NotSupportedException)
        {
            readOnly = true;
        }

        AssertTrue(readOnly, "selection collection must be read-only");
    }

    private static void TestDoesNotSplitSurrogatePairOrTurn()
    {
        string emoji = "\uD83D\uDE00";
        List<ConversationMessage> history = new List<ConversationMessage>
        {
            Message("user", "A" + emoji),
            Message("assistant", "B")
        };
        long turnCharacters = 3L;
        AssertEqual(1L, Characters(emoji), "emoji scalar count");

        ConversationContextSelection fits =
            ConversationContextBudget.SelectRecentCompleteTurns(
                history,
                turnCharacters,
                0L,
                0L,
                0L);
        AssertEqual(2, fits.SelectedMessageCount, "surrogate fit count");
        AssertEqual(
            "A" + emoji,
            fits.Messages[0].Content,
            "surrogate pair content");
        AssertTrue(!fits.WasTruncated, "exact scalar budget should fit");

        ConversationContextSelection tooSmall =
            ConversationContextBudget.SelectRecentCompleteTurns(
                history,
                turnCharacters - 1L,
                0L,
                0L,
                0L);
        AssertEqual(0, tooSmall.SelectedMessageCount, "no partial turn");
        AssertEqual(0L, tooSmall.SelectedCharacterCount, "no partial chars");
        AssertTrue(tooSmall.WasTruncated, "small budget should truncate");
    }

    private static void TestDoesNotSkipOversizedRecentTurn()
    {
        List<ConversationMessage> history = new List<ConversationMessage>
        {
            Message("user", "u"),
            Message("assistant", "a"),
            Message("user", "latest-user-is-large"),
            Message("assistant", "latest-answer-is-large")
        };
        ConversationContextSelection result =
            ConversationContextBudget.SelectRecentCompleteTurns(
                history,
                2L,
                0L,
                0L,
                0L);

        AssertEqual(0, result.SelectedMessageCount, "contiguous suffix");
        AssertEqual(0, result.SelectedTurnCount, "no older fallback turn");
        AssertTrue(result.WasTruncated, "oversized latest turn truncates");
    }

    private static void TestIgnoresIncompleteAndMalformedMessages()
    {
        List<ConversationMessage> history = new List<ConversationMessage>
        {
            null,
            Message("assistant", "orphan answer"),
            Message("USER", "valid user"),
            Message(" Assistant ", "valid answer"),
            Message("tool", "tool output"),
            Message("user", "unfinished user")
        };
        ConversationContextSelection result =
            ConversationContextBudget.SelectRecentCompleteTurns(
                history,
                1000L,
                0L,
                0L,
                0L);

        AssertEqual(6, result.OriginalMessageCount, "malformed original count");
        AssertEqual(2, result.SelectedMessageCount, "only valid pair");
        AssertEqual(1, result.OriginalTurnCount, "valid turn count");
        AssertTrue(result.WasTruncated, "unusable messages omitted");
        AssertEqual("valid user", result.Messages[0].Content, "valid user");
        AssertEqual(
            "valid answer",
            result.Messages[1].Content,
            "valid answer");
    }

    private static void TestNullHistoryAndInvalidArguments()
    {
        ConversationContextSelection empty =
            ConversationContextBudget.SelectRecentCompleteTurns(
                null,
                10L,
                2L,
                3L,
                5L);
        AssertEqual(0, empty.OriginalMessageCount, "null original count");
        AssertEqual(0, empty.SelectedMessageCount, "null selected count");
        AssertEqual(0L, empty.AvailableHistoryCharacterBudget, "zero budget");
        AssertTrue(!empty.WasTruncated, "null history is not truncated");

        bool rejected = false;
        try
        {
            ConversationContextBudget.SelectRecentCompleteTurns(
                null,
                -1L,
                0L,
                0L,
                0L);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }

        AssertTrue(rejected, "negative budget must be rejected");

        ConversationContextSelection saturated =
            ConversationContextBudget.SelectRecentCompleteTurns(
                null,
                long.MaxValue,
                long.MaxValue,
                long.MaxValue,
                long.MaxValue);
        AssertEqual(
            long.MaxValue,
            saturated.ReservedCharacterCount,
            "reservation overflow saturates");
        AssertEqual(
            0L,
            saturated.AvailableHistoryCharacterBudget,
            "saturated reservation leaves no history budget");
    }

    private static ConversationMessage Message(string role, string content)
    {
        return new ConversationMessage(role, content);
    }

    private static long Characters(string value)
    {
        return ConversationContextBudget.CountCharacters(value);
    }

    private static void AssertTrue(bool value, string name)
    {
        if (!value)
        {
            throw new InvalidOperationException("Assertion failed: " + name);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                "Assertion failed: " + name + ". Expected " + expected +
                ", got " + actual + ".");
        }
    }
}
