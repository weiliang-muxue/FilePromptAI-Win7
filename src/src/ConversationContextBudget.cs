using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FilePromptAIWin7
{
    internal sealed class ConversationContextSelection
    {
        private readonly IList<ConversationMessage> messages;

        internal ConversationContextSelection(
            IList<ConversationMessage> messages,
            bool wasTruncated,
            int originalMessageCount,
            int selectedMessageCount,
            long originalCharacterCount,
            long selectedCharacterCount,
            int originalTurnCount,
            int selectedTurnCount,
            long maximumCharacterBudget,
            long reservedCharacterCount,
            long availableHistoryCharacterBudget)
        {
            this.messages = messages;
            WasTruncated = wasTruncated;
            OriginalMessageCount = originalMessageCount;
            SelectedMessageCount = selectedMessageCount;
            OriginalCharacterCount = originalCharacterCount;
            SelectedCharacterCount = selectedCharacterCount;
            OriginalTurnCount = originalTurnCount;
            SelectedTurnCount = selectedTurnCount;
            MaximumCharacterBudget = maximumCharacterBudget;
            ReservedCharacterCount = reservedCharacterCount;
            AvailableHistoryCharacterBudget =
                availableHistoryCharacterBudget;
        }

        public IList<ConversationMessage> Messages
        {
            get { return messages; }
        }

        public bool WasTruncated { get; private set; }

        public int OriginalMessageCount { get; private set; }

        public int SelectedMessageCount { get; private set; }

        public long OriginalCharacterCount { get; private set; }

        public long SelectedCharacterCount { get; private set; }

        public int OriginalTurnCount { get; private set; }

        public int SelectedTurnCount { get; private set; }

        public long MaximumCharacterBudget { get; private set; }

        public long ReservedCharacterCount { get; private set; }

        public long AvailableHistoryCharacterBudget { get; private set; }
    }

    internal static class ConversationContextBudget
    {
        public static ConversationContextSelection SelectRecentCompleteTurns(
            IList<ConversationMessage> messages,
            long maximumCharacterBudget,
            long systemPromptCharacterCount,
            long currentPromptCharacterCount,
            long attachmentCharacterCount)
        {
            EnsureNotNegative(
                maximumCharacterBudget,
                "maximumCharacterBudget");
            EnsureNotNegative(
                systemPromptCharacterCount,
                "systemPromptCharacterCount");
            EnsureNotNegative(
                currentPromptCharacterCount,
                "currentPromptCharacterCount");
            EnsureNotNegative(
                attachmentCharacterCount,
                "attachmentCharacterCount");

            long reservedCharacterCount = AddSaturated(
                AddSaturated(
                    systemPromptCharacterCount,
                    currentPromptCharacterCount),
                attachmentCharacterCount);
            long availableHistoryCharacterBudget = reservedCharacterCount >=
                maximumCharacterBudget
                ? 0L
                : maximumCharacterBudget - reservedCharacterCount;

            int originalMessageCount = messages == null ? 0 : messages.Count;
            long originalCharacterCount = CountMessageCharacters(messages);
            List<CompleteTurn> turns = FindCompleteTurns(messages);
            List<CompleteTurn> selectedTurns = new List<CompleteTurn>();
            long selectedCharacterCount = 0L;

            for (int index = turns.Count - 1; index >= 0; index--)
            {
                CompleteTurn turn = turns[index];
                long remaining = availableHistoryCharacterBudget -
                    selectedCharacterCount;
                if (turn.CharacterCount > remaining)
                {
                    // Keep a contiguous suffix. Skipping an oversized recent
                    // turn would attach an older answer to the current prompt.
                    break;
                }

                selectedTurns.Add(turn);
                selectedCharacterCount += turn.CharacterCount;
            }

            selectedTurns.Reverse();
            List<ConversationMessage> selectedMessages =
                new List<ConversationMessage>(selectedTurns.Count * 2);
            foreach (CompleteTurn turn in selectedTurns)
            {
                selectedMessages.Add(turn.User.Clone());
                selectedMessages.Add(turn.Assistant.Clone());
            }

            IList<ConversationMessage> readOnlyMessages =
                new ReadOnlyCollection<ConversationMessage>(selectedMessages);
            bool wasTruncated = selectedMessages.Count != originalMessageCount;
            return new ConversationContextSelection(
                readOnlyMessages,
                wasTruncated,
                originalMessageCount,
                selectedMessages.Count,
                originalCharacterCount,
                selectedCharacterCount,
                turns.Count,
                selectedTurns.Count,
                maximumCharacterBudget,
                reservedCharacterCount,
                availableHistoryCharacterBudget);
        }

        // Counts Unicode scalar values. A valid surrogate pair always counts as
        // one character and is never split because selection is turn-granular.
        public static long CountCharacters(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0L;
            }

            long count = 0L;
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]) &&
                    index + 1 < value.Length &&
                    char.IsLowSurrogate(value[index + 1]))
                {
                    index++;
                }

                count++;
            }

            return count;
        }

        private static List<CompleteTurn> FindCompleteTurns(
            IList<ConversationMessage> messages)
        {
            List<CompleteTurn> turns = new List<CompleteTurn>();
            if (messages == null)
            {
                return turns;
            }

            for (int index = 0; index + 1 < messages.Count; index++)
            {
                ConversationMessage user = messages[index];
                ConversationMessage assistant = messages[index + 1];
                if (!HasRole(user, "user") ||
                    !HasRole(assistant, "assistant"))
                {
                    continue;
                }

                turns.Add(new CompleteTurn(user, assistant));
                index++;
            }

            return turns;
        }

        private static bool HasRole(
            ConversationMessage message,
            string expectedRole)
        {
            return message != null &&
                string.Equals(
                    message.Role == null ? null : message.Role.Trim(),
                    expectedRole,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static long CountMessageCharacters(
            IList<ConversationMessage> messages)
        {
            long count = 0L;
            if (messages == null)
            {
                return count;
            }

            foreach (ConversationMessage message in messages)
            {
                if (message != null)
                {
                    count = AddSaturated(
                        count,
                        CountCharacters(message.Content));
                }
            }

            return count;
        }

        private static long AddSaturated(long left, long right)
        {
            return left > long.MaxValue - right
                ? long.MaxValue
                : left + right;
        }

        private static void EnsureNotNegative(long value, string parameterName)
        {
            if (value < 0L)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Character counts cannot be negative.");
            }
        }

        private sealed class CompleteTurn
        {
            public CompleteTurn(
                ConversationMessage user,
                ConversationMessage assistant)
            {
                User = user;
                Assistant = assistant;
                CharacterCount = AddSaturated(
                    CountCharacters(user.Content),
                    CountCharacters(assistant.Content));
            }

            public ConversationMessage User { get; private set; }

            public ConversationMessage Assistant { get; private set; }

            public long CharacterCount { get; private set; }
        }
    }
}
