using System;
using System.Collections.Generic;

namespace FilePromptWin7
{
    internal sealed class ConversationMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public DateTime CreatedAt { get; set; }

        public ConversationMessage()
        {
            Role = "user";
            Content = string.Empty;
            CreatedAt = DateTime.UtcNow;
        }

        public ConversationMessage(string role, string content)
        {
            Role = NormalizeRole(role);
            Content = content ?? string.Empty;
            CreatedAt = DateTime.UtcNow;
        }

        public ConversationMessage(
            string role,
            string content,
            DateTime createdAt)
        {
            Role = NormalizeRole(role);
            Content = content ?? string.Empty;
            CreatedAt = createdAt == DateTime.MinValue
                ? DateTime.UtcNow
                : createdAt;
        }

        public ConversationMessage Clone()
        {
            return new ConversationMessage(Role, Content, CreatedAt);
        }

        internal static string NormalizeRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return "user";
            }

            string value = role.Trim().ToLowerInvariant();
            if (value != "system" && value != "user" &&
                value != "assistant" && value != "tool")
            {
                return "user";
            }

            return value;
        }
    }

    internal sealed class ConversationSession
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public IList<ConversationMessage> Messages { get; set; }

        public ConversationSession()
        {
            Id = Guid.NewGuid().ToString("N");
            Title = "新会话";
            CreatedAt = DateTime.UtcNow;
            UpdatedAt = CreatedAt;
            Messages = new List<ConversationMessage>();
        }

        public ConversationSession(string title)
            : this()
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                Title = title.Trim();
            }
        }

        public void AddMessage(string role, string content)
        {
            AddMessage(new ConversationMessage(role, content));
        }

        public void AddMessage(ConversationMessage message)
        {
            if (message == null)
            {
                return;
            }

            EnsureMessages();
            Messages.Add(message);
            UpdatedAt = DateTime.UtcNow;
        }

        public void Touch()
        {
            UpdatedAt = DateTime.UtcNow;
        }

        public ConversationSession Clone()
        {
            ConversationSession clone = new ConversationSession();
            clone.Id = Id;
            clone.Title = Title;
            clone.CreatedAt = CreatedAt;
            clone.UpdatedAt = UpdatedAt;
            clone.Messages = new List<ConversationMessage>();
            if (Messages != null)
            {
                foreach (ConversationMessage message in Messages)
                {
                    if (message != null)
                    {
                        clone.Messages.Add(message.Clone());
                    }
                }
            }

            return clone;
        }

        internal void EnsureIdentity()
        {
            if (string.IsNullOrWhiteSpace(Id))
            {
                Id = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(Title))
            {
                Title = "新会话";
            }

            if (CreatedAt == DateTime.MinValue)
            {
                CreatedAt = DateTime.UtcNow;
            }

            if (UpdatedAt == DateTime.MinValue)
            {
                UpdatedAt = CreatedAt;
            }

            EnsureMessages();
        }

        private void EnsureMessages()
        {
            if (Messages == null)
            {
                Messages = new List<ConversationMessage>();
            }
        }
    }
}
