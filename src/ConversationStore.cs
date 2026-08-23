using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FilePromptAIWin7
{
    internal sealed class ConversationStore
    {
        private const string StoreRootName = "ConversationStore";
        private const string StoreVersion = "1";
        private const string BackupRootName =
            "FilePromptAIConversationBackup";
        private const string BackupVersion = "1";
        private const long MaximumConversationBytes = 128L * 1024L * 1024L;
        private const int MaximumBackupSessions = 10000;
        private const int MaximumMessagesPerSession = 100000;
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        private readonly object syncRoot;
        private readonly List<ConversationSession> sessions;
        private readonly string storagePath;
        private string currentSessionId;
        private string loadWarning;
        private bool writeBlockedByRecovery;

        public static string StoragePath
        {
            get
            {
                return Path.Combine(AppDataPath.Root, "conversations.xml");
            }
        }

        public static string FilePath
        {
            get { return StoragePath; }
        }

        public ConversationStore()
            : this(StoragePath)
        {
        }

        internal ConversationStore(string path)
        {
            syncRoot = new object();
            sessions = new List<ConversationSession>();
            storagePath = string.IsNullOrWhiteSpace(path)
                ? StoragePath
                : path;
            Load();
        }

        public IList<ConversationSession> Sessions
        {
            get
            {
                lock (syncRoot)
                {
                    return new ReadOnlyCollection<ConversationSession>(
                        new List<ConversationSession>(sessions));
                }
            }
        }

        public string CurrentSessionId
        {
            get
            {
                lock (syncRoot)
                {
                    return currentSessionId;
                }
            }
        }

        public string LoadWarning
        {
            get
            {
                lock (syncRoot)
                {
                    return loadWarning ?? string.Empty;
                }
            }
        }

        public bool IsWriteBlocked
        {
            get
            {
                lock (syncRoot)
                {
                    return writeBlockedByRecovery;
                }
            }
        }

        public ConversationSession CurrentSession
        {
            get
            {
                lock (syncRoot)
                {
                    return FindSessionUnlocked(currentSessionId);
                }
            }
        }

        public void Load()
        {
            lock (syncRoot)
            {
                sessions.Clear();
                currentSessionId = string.Empty;
                loadWarning = string.Empty;
                writeBlockedByRecovery = false;
                if (!File.Exists(storagePath))
                {
                    return;
                }

                try
                {
                    XDocument document = ReadActiveDocument(storagePath);
                    XElement root = document.Root;
                    currentSessionId = GetAttribute(root, "currentSessionId");
                    HashSet<string> ids = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (XElement sessionElement in root.Elements("Session"))
                    {
                        ConversationSession session =
                            ReadBackupSession(sessionElement);
                        session.EnsureIdentity();
                        if (ids.Contains(session.Id))
                        {
                            session.Id = Guid.NewGuid().ToString("N");
                        }

                        ids.Add(session.Id);
                        sessions.Add(session);
                    }

                    if (FindSessionUnlocked(currentSessionId) == null &&
                        sessions.Count > 0)
                    {
                        currentSessionId = sessions[0].Id;
                    }
                }
                catch (Exception)
                {
                    string preservedPath = PreserveDamagedStore(storagePath);
                    writeBlockedByRecovery =
                        string.IsNullOrEmpty(preservedPath);
                    loadWarning = writeBlockedByRecovery
                        ? "会话历史文件无法读取且无法安全重命名，原文件保持不变；" +
                            "本次运行已禁止会话写入。请先手工备份文件并解除占用：" +
                            storagePath
                        : "会话历史文件异常，损坏副本已保留：" +
                            preservedPath;
                    sessions.Clear();
                    currentSessionId = string.Empty;
                }
            }
        }

        private static XDocument ReadActiveDocument(string path)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "The conversation store was not found.",
                    path);
            }

            if (file.Length == 0 || file.Length > MaximumConversationBytes)
            {
                throw new InvalidDataException(
                    "The conversation store has an invalid size.");
            }

            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.MaxCharactersInDocument = MaximumConversationBytes;
            settings.MaxCharactersFromEntities = 0;
            XDocument document;
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                document = XDocument.Load(reader, LoadOptions.None);
            }

            XElement root = document.Root;
            if (root == null || root.Name != StoreRootName)
            {
                throw new InvalidDataException(
                    "This is not a FilePrompt AI conversation store.");
            }

            ValidateAttributes(root, "version", "currentSessionId");
            if (!string.Equals(
                RequireAttribute(root, "version"),
                StoreVersion,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The conversation store version is not supported.");
            }

            ValidateContainerText(root);
            IList<XElement> children = root.Elements().ToList();
            if (children.Count > MaximumBackupSessions ||
                children.Any(element => element.Name != "Session"))
            {
                throw new InvalidDataException(
                    "The conversation store has an invalid structure.");
            }

            return document;
        }

        private static string PreserveDamagedStore(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return string.Empty;
                }

                string preservedPath = path + ".damaged-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak";
                File.Move(path, preservedPath);
                return preservedPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Save()
        {
            lock (syncRoot)
            {
                SaveUnlocked();
            }
        }

        public int ExportBackup(string path)
        {
            string outputPath = GetRequiredFullPath(path, "path");
            if (PathsEqual(outputPath, storagePath))
            {
                throw new InvalidOperationException(
                    "A backup cannot overwrite the active conversation store.");
            }

            XDocument document;
            int exportedCount;
            lock (syncRoot)
            {
                document = BuildBackupDocumentUnlocked(out exportedCount);
            }

            WriteDocumentAtomic(
                document,
                outputPath,
                MaximumConversationBytes);
            return exportedCount;
        }

        public int ImportBackup(string path)
        {
            string inputPath = GetRequiredFullPath(path, "path");
            List<ConversationSession> imported = ReadBackup(inputPath);
            if (imported.Count == 0)
            {
                return 0;
            }

            lock (syncRoot)
            {
                if (sessions.Count >
                    MaximumBackupSessions - imported.Count)
                {
                    throw new InvalidOperationException(
                        "The imported sessions would exceed the conversation store limit.");
                }

                HashSet<string> ids = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (ConversationSession existing in sessions)
                {
                    if (existing != null &&
                        !string.IsNullOrWhiteSpace(existing.Id))
                    {
                        ids.Add(existing.Id);
                    }
                }

                Dictionary<string, string> importedIds =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (ConversationSession session in imported)
                {
                    session.EnsureIdentity();
                    string originalId = session.Id;
                    string importedId = originalId;
                    if (!ids.Add(importedId))
                    {
                        importedId = CreateUniqueId(ids);
                    }

                    importedIds.Add(originalId, importedId);
                    session.Id = importedId;
                }

                foreach (ConversationSession session in imported)
                {
                    string remappedSourceId;
                    if (!string.IsNullOrWhiteSpace(session.SourceSessionId) &&
                        importedIds.TryGetValue(
                            session.SourceSessionId,
                            out remappedSourceId))
                    {
                        session.SourceSessionId = remappedSourceId;
                    }
                }

                string previousCurrentSessionId = currentSessionId;
                sessions.AddRange(imported);
                if (string.IsNullOrWhiteSpace(currentSessionId))
                {
                    currentSessionId = imported[0].Id;
                }

                try
                {
                    SaveUnlocked();
                }
                catch
                {
                    foreach (ConversationSession session in imported)
                    {
                        sessions.Remove(session);
                    }

                    currentSessionId = previousCurrentSessionId;
                    throw;
                }

                return imported.Count;
            }
        }

        public ConversationSession CreateSession(string title)
        {
            ConversationSession session;
            lock (syncRoot)
            {
                string previousCurrentSessionId = currentSessionId;
                session = new ConversationSession(title);
                sessions.Add(session);
                currentSessionId = session.Id;
                try
                {
                    SaveUnlocked();
                }
                catch
                {
                    sessions.Remove(session);
                    currentSessionId = previousCurrentSessionId;
                    throw;
                }
            }

            return session;
        }

        public ConversationSession Create(string title)
        {
            return CreateSession(title);
        }

        public bool SelectSession(string id)
        {
            lock (syncRoot)
            {
                ConversationSession session = FindSessionUnlocked(id);
                if (session == null)
                {
                    return false;
                }

                string previousCurrentSessionId = currentSessionId;
                currentSessionId = session.Id;
                try
                {
                    SaveUnlocked();
                }
                catch
                {
                    currentSessionId = previousCurrentSessionId;
                    throw;
                }

                return true;
            }
        }

        public bool Select(string id)
        {
            return SelectSession(id);
        }

        public ConversationSession GetSession(string id)
        {
            lock (syncRoot)
            {
                return FindSessionUnlocked(id);
            }
        }

        public ConversationSession FindById(string id)
        {
            return GetSession(id);
        }

        public bool DeleteSession(string id)
        {
            lock (syncRoot)
            {
                ConversationSession session = FindSessionUnlocked(id);
                if (session == null)
                {
                    return false;
                }

                bool wasCurrent = string.Equals(
                    currentSessionId,
                    session.Id,
                    StringComparison.OrdinalIgnoreCase);
                int sessionIndex = sessions.IndexOf(session);
                string previousCurrentSessionId = currentSessionId;
                sessions.Remove(session);
                if (wasCurrent)
                {
                    currentSessionId = sessions.Count == 0
                        ? string.Empty
                        : sessions[sessions.Count - 1].Id;
                }

                try
                {
                    SaveUnlocked();
                }
                catch
                {
                    sessions.Insert(sessionIndex, session);
                    currentSessionId = previousCurrentSessionId;
                    throw;
                }

                return true;
            }
        }

        public bool Delete(ConversationSession session)
        {
            return session != null && DeleteSession(session.Id);
        }

        public bool RenameSession(string id, string title)
        {
            lock (syncRoot)
            {
                ConversationSession session = FindSessionUnlocked(id);
                if (session == null)
                {
                    return false;
                }

                string previousTitle = session.Title;
                DateTime previousUpdatedAt = session.UpdatedAt;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    session.Title = title.Trim();
                }

                session.Touch();
                try
                {
                    SaveUnlocked();
                }
                catch
                {
                    session.Title = previousTitle;
                    session.UpdatedAt = previousUpdatedAt;
                    throw;
                }

                return true;
            }
        }

        public ConversationSession CloneSessionFromMessage(
            string sessionId,
            string messageId,
            string title)
        {
            lock (syncRoot)
            {
                ConversationSession source = FindSessionUnlocked(sessionId);
                int messageIndex = FindMessageIndex(source, messageId);
                if (source == null || messageIndex < 0)
                {
                    return null;
                }

                ConversationSession clone = new ConversationSession(
                    string.IsNullOrWhiteSpace(title) ? source.Title : title);
                clone.SourceSessionId = source.Id;
                clone.SourceMessageId = source.Messages[messageIndex].Id;
                clone.Messages = new List<ConversationMessage>();
                for (int index = 0; index <= messageIndex; index++)
                {
                    ConversationMessage message = source.Messages[index];
                    if (message != null)
                    {
                        clone.Messages.Add(message.Clone());
                    }
                }

                clone.Touch();
                ValidateMessageSequence(clone.Messages);
                string previousCurrentSessionId = currentSessionId;
                sessions.Add(clone);
                currentSessionId = clone.Id;
                try
                {
                    SaveUnlocked();
                    return clone;
                }
                catch
                {
                    sessions.Remove(clone);
                    currentSessionId = previousCurrentSessionId;
                    throw;
                }
            }
        }

        public bool SetSessionPinned(string id, bool isPinned)
        {
            return SetSessionState(id, isPinned, true);
        }

        public bool SetSessionArchived(string id, bool isArchived)
        {
            return SetSessionState(id, isArchived, false);
        }

        public bool SetSessionArchivedAndResolveCurrent(
            string id,
            bool isArchived,
            string replacementTitle)
        {
            lock (syncRoot)
            {
                ConversationSession session = FindSessionUnlocked(id);
                if (session == null)
                {
                    return false;
                }

                bool previousArchived = session.IsArchived;
                DateTime previousUpdatedAt = session.UpdatedAt;
                string previousCurrentSessionId = currentSessionId;
                ConversationSession createdReplacement = null;

                session.IsArchived = isArchived;
                session.Touch();
                if (!isArchived)
                {
                    currentSessionId = session.Id;
                }
                else if (string.Equals(
                    currentSessionId,
                    session.Id,
                    StringComparison.OrdinalIgnoreCase))
                {
                    ConversationSession replacement = sessions
                        .Where(candidate =>
                            candidate != null &&
                            !candidate.IsArchived &&
                            !string.Equals(
                                candidate.Id,
                                session.Id,
                                StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(candidate => candidate.IsPinned)
                        .ThenByDescending(candidate => candidate.UpdatedAt)
                        .FirstOrDefault();
                    if (replacement == null)
                    {
                        createdReplacement = new ConversationSession(
                            replacementTitle);
                        sessions.Add(createdReplacement);
                        replacement = createdReplacement;
                    }

                    currentSessionId = replacement.Id;
                }

                try
                {
                    SaveUnlocked();
                    return true;
                }
                catch
                {
                    if (createdReplacement != null)
                    {
                        sessions.Remove(createdReplacement);
                    }

                    session.IsArchived = previousArchived;
                    session.UpdatedAt = previousUpdatedAt;
                    currentSessionId = previousCurrentSessionId;
                    throw;
                }
            }
        }

        public bool ReplaceMessageSuffix(
            string sessionId,
            string afterMessageId,
            IList<ConversationMessage> replacementMessages)
        {
            if (replacementMessages == null)
            {
                return false;
            }

            lock (syncRoot)
            {
                ConversationSession session = FindSessionUnlocked(sessionId);
                int anchorIndex = FindMessageIndex(session, afterMessageId);
                if (session == null || anchorIndex < 0)
                {
                    return false;
                }

                List<ConversationMessage> candidate =
                    new List<ConversationMessage>();
                for (int index = 0; index <= anchorIndex; index++)
                {
                    ConversationMessage message = session.Messages[index];
                    if (message != null)
                    {
                        candidate.Add(message);
                    }
                }

                foreach (ConversationMessage message in replacementMessages)
                {
                    if (message == null)
                    {
                        return false;
                    }

                    candidate.Add(message);
                }

                if (!TryValidateMessageSequence(candidate))
                {
                    return false;
                }

                IList<ConversationMessage> previousMessages = session.Messages;
                DateTime previousUpdatedAt = session.UpdatedAt;
                session.Messages = candidate;
                session.Touch();
                try
                {
                    SaveUnlocked();
                    return true;
                }
                catch
                {
                    session.Messages = previousMessages;
                    session.UpdatedAt = previousUpdatedAt;
                    throw;
                }
            }
        }

        private bool SetSessionState(
            string id,
            bool value,
            bool pinnedState)
        {
            lock (syncRoot)
            {
                ConversationSession session = FindSessionUnlocked(id);
                if (session == null)
                {
                    return false;
                }

                bool previousValue = pinnedState
                    ? session.IsPinned
                    : session.IsArchived;
                DateTime previousUpdatedAt = session.UpdatedAt;
                if (pinnedState)
                {
                    session.IsPinned = value;
                }
                else
                {
                    session.IsArchived = value;
                }

                session.Touch();
                try
                {
                    SaveUnlocked();
                    return true;
                }
                catch
                {
                    if (pinnedState)
                    {
                        session.IsPinned = previousValue;
                    }
                    else
                    {
                        session.IsArchived = previousValue;
                    }

                    session.UpdatedAt = previousUpdatedAt;
                    throw;
                }
            }
        }

        public bool AddMessage(
            string sessionId,
            string role,
            string content)
        {
            lock (syncRoot)
            {
                ConversationSession session = FindSessionUnlocked(sessionId);
                if (session == null)
                {
                    return false;
                }

                return AddMessageUnlocked(
                    session,
                    new ConversationMessage(role, content));
            }
        }

        public bool AddMessage(
            string sessionId,
            ConversationMessage message)
        {
            if (message == null)
            {
                return false;
            }

            lock (syncRoot)
            {
                ConversationSession session = FindSessionUnlocked(sessionId);
                if (session == null)
                {
                    return false;
                }

                return AddMessageUnlocked(session, message);
            }
        }

        public bool AddTurn(
            string sessionId,
            ConversationMessage userMessage,
            ConversationMessage assistantMessage,
            string updatedTitle)
        {
            if (userMessage == null || assistantMessage == null)
            {
                return false;
            }

            lock (syncRoot)
            {
                ConversationSession session = FindSessionUnlocked(sessionId);
                if (session == null)
                {
                    return false;
                }

                IList<ConversationMessage> previousMessages = session.Messages;
                int previousCount = previousMessages == null
                    ? 0
                    : previousMessages.Count;
                string previousTitle = session.Title;
                DateTime previousUpdatedAt = session.UpdatedAt;
                session.AddMessage(userMessage);
                session.AddMessage(assistantMessage);
                if (!string.IsNullOrWhiteSpace(updatedTitle))
                {
                    session.Title = updatedTitle.Trim();
                }

                try
                {
                    SaveUnlocked();
                    return true;
                }
                catch
                {
                    RestoreMessages(
                        session,
                        previousMessages,
                        previousCount);
                    session.Title = previousTitle;
                    session.UpdatedAt = previousUpdatedAt;
                    throw;
                }
            }
        }

        private bool AddMessageUnlocked(
            ConversationSession session,
            ConversationMessage message)
        {
            List<ConversationMessage> candidate =
                CreateMessageSequenceSnapshot(session.Messages);
            candidate.Add(message);
            if (!TryValidateMessageSequence(candidate))
            {
                return false;
            }

            IList<ConversationMessage> previousMessages = session.Messages;
            int previousCount = previousMessages == null
                ? 0
                : previousMessages.Count;
            DateTime previousUpdatedAt = session.UpdatedAt;
            session.AddMessage(message);
            try
            {
                SaveUnlocked();
                return true;
            }
            catch
            {
                RestoreMessages(session, previousMessages, previousCount);
                session.UpdatedAt = previousUpdatedAt;
                throw;
            }
        }

        private static void RestoreMessages(
            ConversationSession session,
            IList<ConversationMessage> previousMessages,
            int previousCount)
        {
            if (ReferenceEquals(session.Messages, previousMessages) &&
                session.Messages != null)
            {
                while (session.Messages.Count > previousCount)
                {
                    session.Messages.RemoveAt(session.Messages.Count - 1);
                }
            }
            else
            {
                session.Messages = previousMessages;
            }
        }

        private void SaveUnlocked()
        {
            if (writeBlockedByRecovery)
            {
                throw new InvalidOperationException(
                    "会话历史处于只读保护状态。请先手工备份并处理原文件，" +
                    "然后重新启动程序。");
            }

            ValidateStoreCapacityUnlocked();

            string directory = Path.GetDirectoryName(storagePath);
            if (!string.IsNullOrWhiteSpace(directory) &&
                !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            XElement root = new XElement(
                StoreRootName,
                new XAttribute("version", StoreVersion),
                new XAttribute(
                    "currentSessionId",
                    currentSessionId ?? string.Empty));

            foreach (ConversationSession session in sessions)
            {
                if (session == null)
                {
                    continue;
                }

                session.EnsureIdentity();
                ValidateSessionMetadata(session);
                ValidateMessageSequence(session.Messages);
                root.Add(CreateSessionElement(session));
            }

            XDocument document = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                root);
            WriteDocumentAtomic(
                document,
                storagePath,
                MaximumConversationBytes);
        }

        private void ValidateStoreCapacityUnlocked()
        {
            if (sessions.Count > MaximumBackupSessions)
            {
                throw new InvalidOperationException(
                    "There are too many sessions to save in the conversation store.");
            }

            foreach (ConversationSession session in sessions)
            {
                if (session != null &&
                    session.Messages != null &&
                    session.Messages.Count > MaximumMessagesPerSession)
                {
                    throw new InvalidOperationException(
                        "A conversation contains too many messages to save.");
                }
            }
        }

        private XDocument BuildBackupDocumentUnlocked(out int exportedCount)
        {
            XElement sessionsElement = new XElement("Sessions");
            HashSet<string> ids = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            exportedCount = 0;
            foreach (ConversationSession source in sessions)
            {
                if (source == null)
                {
                    continue;
                }

                if (exportedCount >= MaximumBackupSessions)
                {
                    throw new InvalidOperationException(
                        "There are too many sessions to export in one backup.");
                }

                ConversationSession snapshot = source.Clone();
                snapshot.EnsureIdentity();
                snapshot.Id = SanitizeXmlText(snapshot.Id);
                if (string.IsNullOrWhiteSpace(snapshot.Id) ||
                    snapshot.Id.Length > 256 ||
                    !ids.Add(snapshot.Id))
                {
                    snapshot.Id = CreateUniqueId(ids);
                }

                snapshot.Title = SanitizeXmlText(snapshot.Title);
                if (string.IsNullOrWhiteSpace(snapshot.Title))
                {
                    snapshot.Title = "新会话";
                }

                if (snapshot.Messages != null &&
                    snapshot.Messages.Count > MaximumMessagesPerSession)
                {
                    throw new InvalidOperationException(
                        "A session contains too many messages to export.");
                }

                ValidateSessionMetadata(snapshot);
                ValidateMessageSequence(snapshot.Messages);
                sessionsElement.Add(CreateSessionElement(snapshot));
                exportedCount++;
            }

            XElement root = new XElement(
                BackupRootName,
                new XAttribute("version", BackupVersion),
                new XAttribute("exportedAt", FormatDate(DateTime.UtcNow)),
                sessionsElement);
            return new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                root);
        }

        private static XElement CreateSessionElement(
            ConversationSession session)
        {
            string safeTitle = SanitizeXmlText(session.Title);
            if (string.IsNullOrWhiteSpace(safeTitle))
            {
                safeTitle = "新会话";
            }

            XElement sessionElement = new XElement(
                "Session",
                new XAttribute("id", SanitizeXmlText(session.Id)),
                new XAttribute("title", safeTitle),
                new XAttribute("createdAt", FormatDate(session.CreatedAt)),
                new XAttribute("updatedAt", FormatDate(session.UpdatedAt)));
            AddOptionalBooleanAttribute(
                sessionElement,
                "isPinned",
                session.IsPinned);
            AddOptionalBooleanAttribute(
                sessionElement,
                "isArchived",
                session.IsArchived);
            AddOptionalAttribute(
                sessionElement,
                "sourceSessionId",
                session.SourceSessionId);
            AddOptionalAttribute(
                sessionElement,
                "sourceMessageId",
                session.SourceMessageId);
            XElement messagesElement = new XElement("Messages");
            if (session.Messages != null)
            {
                foreach (ConversationMessage message in session.Messages)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    XElement messageElement = new XElement(
                        "Message",
                        new XAttribute("id", message.Id),
                        new XAttribute(
                            "role",
                            ConversationMessage.NormalizeRole(message.Role)),
                        new XAttribute(
                            "createdAt",
                            FormatDate(message.CreatedAt)));
                    AddOptionalAttribute(
                        messageElement,
                        "parentMessageId",
                        message.ParentMessageId);
                    if (message.VariantIndex != 0)
                    {
                        messageElement.Add(
                            new XAttribute(
                                "variantIndex",
                                message.VariantIndex.ToString(
                                    CultureInfo.InvariantCulture)));
                    }

                    messageElement.Add(SanitizeXmlText(message.Content));
                    messagesElement.Add(messageElement);
                }
            }

            sessionElement.Add(messagesElement);
            return sessionElement;
        }

        private static List<ConversationSession> ReadBackup(string path)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "The conversation backup file was not found.",
                    path);
            }

            if (file.Length == 0 || file.Length > MaximumConversationBytes)
            {
                throw new InvalidDataException(
                    "The conversation backup file has an invalid size.");
            }

            XDocument document;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings();
                settings.DtdProcessing = DtdProcessing.Prohibit;
                settings.XmlResolver = null;
                settings.MaxCharactersInDocument = MaximumConversationBytes;
                settings.MaxCharactersFromEntities = 0;
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                using (XmlReader reader = XmlReader.Create(stream, settings))
                {
                    document = XDocument.Load(reader, LoadOptions.None);
                }
            }
            catch (XmlException exception)
            {
                throw new InvalidDataException(
                    "The conversation backup is not valid XML.",
                    exception);
            }

            XElement root = document.Root;
            if (root == null || root.Name != BackupRootName)
            {
                throw new InvalidDataException(
                    "This is not a FilePrompt AI conversation backup.");
            }

            ValidateAttributes(root, "version", "exportedAt");
            if (!string.Equals(
                RequireAttribute(root, "version"),
                BackupVersion,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The conversation backup version is not supported.");
            }

            ParseRequiredDate(RequireAttribute(root, "exportedAt"));
            ValidateContainerText(root);
            IList<XElement> rootChildren = root.Elements().ToList();
            if (rootChildren.Count != 1 ||
                rootChildren[0].Name != "Sessions")
            {
                throw new InvalidDataException(
                    "The conversation backup has an invalid root structure.");
            }

            XElement sessionsElement = rootChildren[0];
            ValidateAttributes(sessionsElement);
            ValidateContainerText(sessionsElement);
            IList<XElement> sessionElements =
                sessionsElement.Elements().ToList();
            if (sessionElements.Count > MaximumBackupSessions)
            {
                throw new InvalidDataException(
                    "The conversation backup contains too many sessions.");
            }

            List<ConversationSession> result =
                new List<ConversationSession>();
            HashSet<string> sessionIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (XElement sessionElement in sessionElements)
            {
                if (sessionElement.Name != "Session")
                {
                    throw new InvalidDataException(
                        "The conversation backup contains an unknown element.");
                }

                ConversationSession session = ReadBackupSession(
                    sessionElement);
                if (!sessionIds.Add(session.Id))
                {
                    throw new InvalidDataException(
                        "The conversation backup contains duplicate session identifiers.");
                }

                result.Add(session);
            }

            return result;
        }

        private static ConversationSession ReadBackupSession(
            XElement element)
        {
            ValidateAttributes(
                element,
                "id",
                "title",
                "createdAt",
                "updatedAt",
                "isPinned",
                "isArchived",
                "sourceSessionId",
                "sourceMessageId");
            string id = RequireAttribute(element, "id");
            string title = RequireAttribute(element, "title");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256)
            {
                throw new InvalidDataException(
                    "A backup session has an invalid identifier.");
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                throw new InvalidDataException(
                    "A backup session has an invalid title.");
            }

            DateTime createdAt = ParseRequiredDate(
                RequireAttribute(element, "createdAt"));
            DateTime updatedAt = ParseRequiredDate(
                RequireAttribute(element, "updatedAt"));
            ValidateContainerText(element);
            IList<XElement> children = element.Elements().ToList();
            if (children.Count != 1 || children[0].Name != "Messages")
            {
                throw new InvalidDataException(
                    "A backup session has an invalid message structure.");
            }

            XElement messagesElement = children[0];
            ValidateAttributes(messagesElement);
            ValidateContainerText(messagesElement);
            IList<XElement> messageElements =
                messagesElement.Elements().ToList();
            if (messageElements.Count > MaximumMessagesPerSession)
            {
                throw new InvalidDataException(
                    "A backup session contains too many messages.");
            }

            ConversationSession session = new ConversationSession();
            session.Id = id;
            session.Title = title;
            session.CreatedAt = createdAt;
            session.UpdatedAt = updatedAt;
            session.IsPinned = ParseOptionalBoolean(element, "isPinned");
            session.IsArchived = ParseOptionalBoolean(element, "isArchived");
            session.SourceSessionId = GetAttribute(element, "sourceSessionId");
            session.SourceMessageId = GetAttribute(element, "sourceMessageId");
            session.Messages = new List<ConversationMessage>();
            foreach (XElement messageElement in messageElements)
            {
                if (messageElement.Name != "Message" ||
                    messageElement.HasElements)
                {
                    throw new InvalidDataException(
                        "A backup session contains an invalid message.");
                }

                ValidateAttributes(
                    messageElement,
                    "id",
                    "parentMessageId",
                    "variantIndex",
                    "role",
                    "createdAt");
                string role = RequireAttribute(messageElement, "role");
                if (!IsValidRole(role))
                {
                    throw new InvalidDataException(
                        "A backup message contains an invalid role.");
                }

                DateTime messageCreatedAt = ParseRequiredDate(
                    RequireAttribute(messageElement, "createdAt"));
                string messageId = GetAttribute(messageElement, "id");
                string parentMessageId = GetAttribute(
                    messageElement,
                    "parentMessageId");
                int variantIndex = ParseOptionalNonNegativeInteger(
                    messageElement,
                    "variantIndex");
                session.Messages.Add(
                    new ConversationMessage(
                        role,
                        messageElement.Value,
                        messageCreatedAt,
                        messageId,
                        parentMessageId,
                        variantIndex));
            }

            ValidateSessionMetadata(session);
            ValidateMessageSequence(session.Messages);
            return session;
        }

        private static void ValidateAttributes(
            XElement element,
            params string[] allowedNames)
        {
            HashSet<string> allowed = new HashSet<string>(
                allowedNames ?? new string[0],
                StringComparer.Ordinal);
            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration ||
                    attribute.Name.Namespace != XNamespace.None ||
                    !allowed.Contains(attribute.Name.LocalName))
                {
                    throw new InvalidDataException(
                        "The conversation backup contains an unknown attribute.");
                }
            }
        }

        private static void AddOptionalAttribute(
            XElement element,
            string name,
            string value)
        {
            string safeValue = SanitizeXmlText(value);
            if (!string.IsNullOrWhiteSpace(safeValue))
            {
                element.Add(new XAttribute(name, safeValue));
            }
        }

        private static void AddOptionalBooleanAttribute(
            XElement element,
            string name,
            bool value)
        {
            if (value)
            {
                element.Add(new XAttribute(name, "true"));
            }
        }

        private static bool ParseOptionalBoolean(XElement element, string name)
        {
            XAttribute attribute = element.Attribute(name);
            if (attribute == null)
            {
                return false;
            }

            bool value;
            if (!bool.TryParse(attribute.Value, out value))
            {
                throw new InvalidDataException(
                    "The conversation store contains an invalid boolean.");
            }

            return value;
        }

        private static int ParseOptionalNonNegativeInteger(
            XElement element,
            string name)
        {
            XAttribute attribute = element.Attribute(name);
            if (attribute == null)
            {
                return 0;
            }

            int value;
            if (!int.TryParse(
                attribute.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value) || value < 0)
            {
                throw new InvalidDataException(
                    "The conversation message contains an invalid variant index.");
            }

            return value;
        }

        private static int FindMessageIndex(
            ConversationSession session,
            string messageId)
        {
            if (session == null || session.Messages == null ||
                string.IsNullOrWhiteSpace(messageId))
            {
                return -1;
            }

            for (int index = 0; index < session.Messages.Count; index++)
            {
                ConversationMessage message = session.Messages[index];
                if (message != null && string.Equals(
                    message.Id,
                    messageId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private static List<ConversationMessage> CreateMessageSequenceSnapshot(
            IList<ConversationMessage> messages)
        {
            List<ConversationMessage> result =
                new List<ConversationMessage>();
            if (messages != null)
            {
                foreach (ConversationMessage message in messages)
                {
                    if (message != null)
                    {
                        result.Add(message);
                    }
                }
            }

            return result;
        }

        private static bool TryValidateMessageSequence(
            IList<ConversationMessage> messages)
        {
            try
            {
                ValidateMessageSequence(messages);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static void ValidateMessageSequence(
            IList<ConversationMessage> messages)
        {
            HashSet<string> ids = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (messages == null)
            {
                return;
            }

            foreach (ConversationMessage message in messages)
            {
                if (message == null)
                {
                    continue;
                }

                message.EnsureIdentity();
                if (string.IsNullOrWhiteSpace(message.Id) ||
                    message.Id.Length > 256 ||
                    !string.Equals(
                        SanitizeXmlText(message.Id),
                        message.Id,
                        StringComparison.Ordinal) ||
                    !ids.Add(message.Id))
                {
                    throw new InvalidDataException(
                        "A conversation contains an invalid or duplicate message identifier.");
                }

                if (message.VariantIndex < 0)
                {
                    throw new InvalidDataException(
                        "A conversation message contains an invalid variant index.");
                }

            }

            foreach (ConversationMessage message in messages)
            {
                if (message != null &&
                    !string.IsNullOrWhiteSpace(message.ParentMessageId) &&
                    !ids.Contains(message.ParentMessageId))
                {
                    throw new InvalidDataException(
                        "A conversation message has an unknown parent message.");
                }
            }
        }

        private static void ValidateSessionMetadata(ConversationSession session)
        {
            if (session == null)
            {
                return;
            }

            ValidateOptionalIdentifier(session.SourceSessionId);
            ValidateOptionalIdentifier(session.SourceMessageId);
            if (string.IsNullOrWhiteSpace(session.SourceSessionId) !=
                string.IsNullOrWhiteSpace(session.SourceMessageId))
            {
                throw new InvalidDataException(
                    "A conversation source reference is incomplete.");
            }
        }

        private static void ValidateOptionalIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (value.Length > 256 ||
                !string.Equals(
                    SanitizeXmlText(value),
                    value,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A conversation source reference is invalid.");
            }
        }

        private static string RequireAttribute(
            XElement element,
            string name)
        {
            XAttribute attribute = element.Attribute(name);
            if (attribute == null)
            {
                throw new InvalidDataException(
                    "The conversation backup is missing a required attribute.");
            }

            return attribute.Value;
        }

        private static void ValidateContainerText(XElement element)
        {
            foreach (XText text in element.Nodes().OfType<XText>())
            {
                if (!string.IsNullOrWhiteSpace(text.Value))
                {
                    throw new InvalidDataException(
                        "The conversation backup contains unexpected text.");
                }
            }
        }

        private static bool IsValidRole(string role)
        {
            return string.Equals(role, "system", StringComparison.Ordinal) ||
                string.Equals(role, "user", StringComparison.Ordinal) ||
                string.Equals(role, "assistant", StringComparison.Ordinal) ||
                string.Equals(role, "tool", StringComparison.Ordinal);
        }

        private static DateTime ParseRequiredDate(string value)
        {
            DateTime result;
            if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out result) ||
                result == DateTime.MinValue)
            {
                throw new InvalidDataException(
                    "The conversation backup contains an invalid date.");
            }

            return result;
        }

        private static string CreateUniqueId(HashSet<string> ids)
        {
            string id;
            do
            {
                id = Guid.NewGuid().ToString("N");
            }
            while (!ids.Add(id));

            return id;
        }

        private static string GetRequiredFullPath(
            string path,
            string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "A file path is required.",
                    parameterName);
            }

            return Path.GetFullPath(path);
        }

        private static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) ||
                string.IsNullOrWhiteSpace(second))
            {
                return false;
            }

            string firstPath = Path.GetFullPath(first).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string secondPath = Path.GetFullPath(second).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return string.Equals(
                firstPath,
                secondPath,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteDocumentAtomic(
            XDocument document,
            string path,
            long maximumBytes = long.MaxValue)
        {
            if (document == null)
            {
                throw new ArgumentNullException("document");
            }

            string outputPath = GetRequiredFullPath(path, "path");
            string directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(outputPath) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                XmlWriterSettings settings = new XmlWriterSettings();
                settings.Encoding = new UTF8Encoding(true);
                settings.Indent = true;
                settings.IndentChars = "  ";
                settings.CloseOutput = false;
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    using (XmlWriter writer = XmlWriter.Create(stream, settings))
                    {
                        document.Save(writer);
                    }

                    stream.Flush(true);
                }

                long writtenBytes = new FileInfo(temporaryPath).Length;
                if (writtenBytes == 0 || writtenBytes > maximumBytes)
                {
                    throw new InvalidOperationException(
                        "The conversation file is too large to save or restore safely.");
                }

                if (!MoveFileEx(
                    temporaryPath,
                    outputPath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The conversation file could not be replaced atomically.",
                        new Win32Exception(error));
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch
                    {
                        // A failed temporary cleanup must not affect the original file.
                    }
                }
            }
        }

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            int flags);

        private ConversationSession FindSessionUnlocked(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return sessions.FirstOrDefault(
                delegate(ConversationSession session)
                {
                    return session != null &&
                        string.Equals(
                            session.Id,
                            id,
                            StringComparison.OrdinalIgnoreCase);
                });
        }

        private static string GetAttribute(XElement element, string name)
        {
            if (element == null)
            {
                return string.Empty;
            }

            XAttribute attribute = element.Attribute(name);
            return attribute == null ? string.Empty : attribute.Value;
        }

        private static string SanitizeXmlText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder result = null;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                bool valid = current == '\t' || current == '\n' ||
                    current == '\r' ||
                    (current >= 0x20 && current <= 0xD7FF) ||
                    (current >= 0xE000 && current <= 0xFFFD);
                if (char.IsHighSurrogate(current) &&
                    index + 1 < value.Length &&
                    char.IsLowSurrogate(value[index + 1]))
                {
                    if (result != null)
                    {
                        result.Append(current);
                        result.Append(value[index + 1]);
                    }

                    index++;
                    continue;
                }

                if (valid && !char.IsSurrogate(current))
                {
                    if (result != null)
                    {
                        result.Append(current);
                    }

                    continue;
                }

                if (result == null)
                {
                    result = new StringBuilder(value.Length);
                    result.Append(value, 0, index);
                }
            }

            return result == null ? value : result.ToString();
        }

        private static string FormatDate(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                value = DateTime.UtcNow;
            }

            return value.ToUniversalTime().ToString(
                "o",
                CultureInfo.InvariantCulture);
        }

    }
}
