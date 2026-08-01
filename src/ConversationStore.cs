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

namespace FilePromptWin7
{
    internal sealed class ConversationStore
    {
        private const string BackupRootName =
            "FilePromptConversationBackup";
        private const string BackupVersion = "1";
        private const long MaximumBackupCharacters = 128L * 1024L * 1024L;
        private const int MaximumBackupSessions = 10000;
        private const int MaximumMessagesPerSession = 100000;
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        private readonly object syncRoot;
        private readonly List<ConversationSession> sessions;
        private readonly string storagePath;
        private string currentSessionId;
        private string loadWarning;

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
                if (!File.Exists(storagePath))
                {
                    return;
                }

                try
                {
                    XDocument document = XDocument.Load(storagePath);
                    XElement root = document.Root;
                    if (root == null)
                    {
                        return;
                    }

                    currentSessionId = GetAttribute(root, "currentSessionId");
                    HashSet<string> ids = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (XElement sessionElement in root.Elements("Session"))
                    {
                        ConversationSession session =
                            ReadSession(sessionElement);
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
                catch (Exception exception)
                {
                    string preservedPath = PreserveDamagedStore(storagePath);
                    loadWarning = string.IsNullOrEmpty(preservedPath)
                        ? "会话历史文件无法读取：" + exception.Message
                        : "会话历史文件异常，损坏副本已保留：" +
                            preservedPath;
                    sessions.Clear();
                    currentSessionId = string.Empty;
                }
            }
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
                File.Copy(path, preservedPath, false);
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

            WriteDocumentAtomic(document, outputPath);
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

                foreach (ConversationSession session in imported)
                {
                    session.EnsureIdentity();
                    if (!ids.Add(session.Id))
                    {
                        session.Id = CreateUniqueId(ids);
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
                session = new ConversationSession(title);
                sessions.Add(session);
                currentSessionId = session.Id;
                SaveUnlocked();
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

                currentSessionId = session.Id;
                SaveUnlocked();
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
                sessions.Remove(session);
                if (wasCurrent)
                {
                    currentSessionId = sessions.Count == 0
                        ? string.Empty
                        : sessions[sessions.Count - 1].Id;
                }

                SaveUnlocked();
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

                if (!string.IsNullOrWhiteSpace(title))
                {
                    session.Title = title.Trim();
                }

                session.Touch();
                SaveUnlocked();
                return true;
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

                session.AddMessage(role, content);
                SaveUnlocked();
                return true;
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

                session.AddMessage(message);
                SaveUnlocked();
                return true;
            }
        }

        private void SaveUnlocked()
        {
            string directory = Path.GetDirectoryName(storagePath);
            if (!string.IsNullOrWhiteSpace(directory) &&
                !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            XElement root = new XElement(
                "ConversationStore",
                new XAttribute("version", "1"),
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
                root.Add(CreateSessionElement(session));
            }

            XDocument document = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                root);
            WriteDocumentAtomic(document, storagePath);
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
            XElement sessionElement = new XElement(
                "Session",
                new XAttribute("id", SanitizeXmlText(session.Id)),
                new XAttribute("title", SanitizeXmlText(session.Title)),
                new XAttribute("createdAt", FormatDate(session.CreatedAt)),
                new XAttribute("updatedAt", FormatDate(session.UpdatedAt)));
            XElement messagesElement = new XElement("Messages");
            if (session.Messages != null)
            {
                foreach (ConversationMessage message in session.Messages)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    messagesElement.Add(
                        new XElement(
                            "Message",
                            new XAttribute(
                                "role",
                                ConversationMessage.NormalizeRole(message.Role)),
                            new XAttribute(
                                "createdAt",
                                FormatDate(message.CreatedAt)),
                            SanitizeXmlText(message.Content)));
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

            if (file.Length == 0 || file.Length > MaximumBackupCharacters)
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
                settings.MaxCharactersInDocument = MaximumBackupCharacters;
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
                    "This is not a FilePrompt conversation backup.");
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
            foreach (XElement sessionElement in sessionElements)
            {
                if (sessionElement.Name != "Session")
                {
                    throw new InvalidDataException(
                        "The conversation backup contains an unknown element.");
                }

                result.Add(ReadBackupSession(sessionElement));
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
                "updatedAt");
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
            session.Messages = new List<ConversationMessage>();
            foreach (XElement messageElement in messageElements)
            {
                if (messageElement.Name != "Message" ||
                    messageElement.HasElements)
                {
                    throw new InvalidDataException(
                        "A backup session contains an invalid message.");
                }

                ValidateAttributes(messageElement, "role", "createdAt");
                string role = RequireAttribute(messageElement, "role");
                if (!IsValidRole(role))
                {
                    throw new InvalidDataException(
                        "A backup message contains an invalid role.");
                }

                DateTime messageCreatedAt = ParseRequiredDate(
                    RequireAttribute(messageElement, "createdAt"));
                session.Messages.Add(
                    new ConversationMessage(
                        role,
                        messageElement.Value,
                        messageCreatedAt));
            }

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
            string path)
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

        private static ConversationSession ReadSession(XElement element)
        {
            ConversationSession session = new ConversationSession();
            session.Id = GetAttribute(element, "id");
            session.Title = GetAttribute(element, "title");
            session.CreatedAt = ParseDate(
                GetAttribute(element, "createdAt"),
                DateTime.UtcNow);
            session.UpdatedAt = ParseDate(
                GetAttribute(element, "updatedAt"),
                session.CreatedAt);
            session.Messages = new List<ConversationMessage>();

            XElement messagesElement = element.Element("Messages");
            if (messagesElement != null)
            {
                foreach (XElement messageElement in
                    messagesElement.Elements("Message"))
                {
                    DateTime createdAt = ParseDate(
                        GetAttribute(messageElement, "createdAt"),
                        DateTime.UtcNow);
                    session.Messages.Add(
                        new ConversationMessage(
                            GetAttribute(messageElement, "role"),
                            messageElement.Value,
                            createdAt));
                }
            }

            return session;
        }

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

        private static DateTime ParseDate(string value, DateTime fallback)
        {
            DateTime result;
            if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out result))
            {
                return result;
            }

            return fallback;
        }
    }
}
