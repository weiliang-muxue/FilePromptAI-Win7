using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class UiStateSmokeTest
{
    private const int EmGetScrollPosition = 0x04DD;
    private const int EmSetScrollPosition = 0x04DE;
    private static Exception uiThreadException;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        ref NativePoint longParameter);

    [STAThread]
    private static int Main(string[] args)
    {
        string dataRoot = Path.Combine(
            Path.GetTempPath(),
            "FilePromptAIUiState-" + Guid.NewGuid().ToString("N"));
        string previousRoot = Environment.GetEnvironmentVariable(
            "FILEPROMPTAI_DATA_ROOT");
        object form = null;
        try
        {
            if (args.Length != 1)
            {
                throw new ArgumentException(
                    "Usage: UiStateSmokeTest <FilePromptAI.exe>");
            }

            string applicationPath = Path.GetFullPath(args[0]);
            ConfigureAssemblyResolution(applicationPath);
            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                dataRoot);
            Application.SetUnhandledExceptionMode(
                UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(
                object sender,
                ThreadExceptionEventArgs eventArgs)
            {
                uiThreadException = eventArgs.Exception;
                Console.Error.WriteLine("UI THREAD EXCEPTION");
                Console.Error.WriteLine(eventArgs.Exception);
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Assembly application = Assembly.LoadFrom(applicationPath);
            Type formType = application.GetType(
                "FilePromptAIWin7.MainForm",
                true);
            form = Activator.CreateInstance(formType, true);

            TestWorkspaceLayoutAndIncrementalTranscript(formType, form);
            TestCurrentTurnTextBudget(formType, form);
            TestBinaryRetentionBudget(formType, form);
            TestFileDropRegistration(formType, form);
            ThrowIfUiThreadException();
            TestMainKeyboardShortcuts(formType, form);
            TestSendShortcutConfig(formType, form);
            TestPromptActions(formType, form);
            TestDragBusyGuard(formType, form, dataRoot);
            TestPathInput(formType, form, dataRoot);
            ThrowIfUiThreadException();
            TestPathResolutionBoundaries(formType, form, dataRoot);
            ThrowIfUiThreadException();
            TestWholeConversationExport(formType, form);
            TestSearchCharacterBudget(application, formType, form);
            TestExtensionsDialog(application, formType, form);
            TestModelProfilesDialog(application, formType, form);
            TestUninstallerEntry(formType, form);
            TestMcpApprovalArguments(application, formType, form);
            TestStdioStartupApproval(application, formType, form);
            TestRejectedStdioStartupCancelsGeneration(
                application,
                formType,
                form);
            TestSessionManagement(formType, form);
            TestDeleteSessionDraftTransaction(formType, form);
            TestExitConfirmationState(formType, form);
            ThrowIfUiThreadException();
            Console.WriteLine("PASS | UI state hardening");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | UI state hardening");
            Console.Error.WriteLine(Unwrap(exception));
            return 1;
        }
        finally
        {
            IDisposable disposable = form as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }

            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                previousRoot);
            try
            {
                if (Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, true);
                }
            }
            catch
            {
                // Temporary state can be removed on the next run.
            }
        }
    }

    private static void TestBinaryRetentionBudget(Type formType, object form)
    {
        InvokePrivate(formType, form, "OnClearClick", form, EventArgs.Empty);
        Type inputItemType = formType.Assembly.GetType(
            "FilePromptAIWin7.InputItem",
            true);
        Type inputKindType = formType.Assembly.GetType(
            "FilePromptAIWin7.InputKind",
            true);
        object first = Activator.CreateInstance(inputItemType, true);
        inputItemType.GetProperty("Name").SetValue(first, "first.bin", null);
        inputItemType.GetProperty("Kind").SetValue(
            first,
            Enum.Parse(inputKindType, "File"),
            null);
        inputItemType.GetProperty("BinaryData").SetValue(
            first,
            new byte[12 * 1024 * 1024],
            null);
        AssertTrue(
            (bool)InvokePrivate(formType, form, "AddInputItem", first),
            "Binary material below retention limit is accepted");

        object second = Activator.CreateInstance(inputItemType, true);
        inputItemType.GetProperty("Name").SetValue(second, "second.bin", null);
        inputItemType.GetProperty("Kind").SetValue(
            second,
            Enum.Parse(inputKindType, "File"),
            null);
        inputItemType.GetProperty("BinaryData").SetValue(
            second,
            new byte[9 * 1024 * 1024],
            null);
        Exception failure = null;
        try
        {
            InvokePrivate(formType, form, "AddInputItem", second);
        }
        catch (TargetInvocationException exception)
        {
            failure = exception.InnerException;
        }
        finally
        {
            InvokePrivate(formType, form, "OnClearClick", form, EventArgs.Empty);
        }

        AssertTrue(
            failure is InvalidOperationException &&
                failure.Message.IndexOf(
                    "20 MB",
                    StringComparison.Ordinal) >= 0,
            "Binary material above retention limit is rejected");
    }

    private static void TestExitConfirmationState(Type formType, object form)
    {
        RichTextBox prompt = GetField(
            formType,
            form,
            "promptTextBox") as RichTextBox;
        IList items = (IList)GetField(formType, form, "inputItems");
        IDictionary drafts = (IDictionary)GetField(
            formType,
            form,
            "sessionDrafts");
        string previousPrompt = prompt.Text;
        ArrayList previousItems = new ArrayList(items);
        ArrayList previousDrafts = new ArrayList();
        foreach (DictionaryEntry entry in drafts)
        {
            previousDrafts.Add(entry);
        }
        object previousCancellation = GetField(
            formType,
            form,
            "generationCancellation");
        object previousConnectionCancellation = GetField(
            formType,
            form,
            "connectionTestCancellation");
        CancellationTokenSource testCancellation = null;
        CancellationTokenSource testConnectionCancellation = null;
        try
        {
            prompt.Clear();
            items.Clear();
            drafts.Clear();
            SetField(formType, form, "generationCancellation", null);
            AssertTrue(
                !(bool)InvokePrivate(
                    formType,
                    form,
                    "NeedsExitConfirmation"),
                "Empty workspace exits without confirmation");

            prompt.Text = "尚未发送的草稿";
            AssertTrue(
                (bool)InvokePrivate(
                    formType,
                    form,
                    "NeedsExitConfirmation"),
                "Unsent text requires exit confirmation");

            prompt.Clear();
            Type inputItemType = items.GetType().GetGenericArguments()[0];
            items.Add(Activator.CreateInstance(inputItemType, true));
            AssertTrue(
                (bool)InvokePrivate(
                    formType,
                    form,
                    "NeedsExitConfirmation"),
                "Added material requires exit confirmation");

            items.Clear();
            testCancellation = new CancellationTokenSource();
            SetField(
                formType,
                form,
                "generationCancellation",
                testCancellation);
            AssertTrue(
                (bool)InvokePrivate(
                    formType,
                    form,
                    "NeedsExitConfirmation"),
                "Active generation requires exit confirmation");

            SetField(formType, form, "generationCancellation", null);
            Type draftType = formType.GetNestedType(
                "SessionDraft",
                BindingFlags.NonPublic);
            object draft = Activator.CreateInstance(draftType, true);
            draftType.GetProperty("Prompt").SetValue(
                draft,
                "其他会话草稿",
                null);
            drafts.Add("other-session", draft);
            AssertTrue(
                (bool)InvokePrivate(
                    formType,
                    form,
                    "NeedsExitConfirmation"),
                "Background session draft requires exit confirmation");

            testConnectionCancellation = new CancellationTokenSource();
            SetField(
                formType,
                form,
                "connectionTestCancellation",
                testConnectionCancellation);
            FormClosingEventArgs userClose = new FormClosingEventArgs(
                CloseReason.UserClosing,
                false);
            InvokePrivate(
                formType,
                form,
                "OnSettingsDialogClosing",
                form,
                userClose);
            AssertTrue(userClose.Cancel,
                "User cannot close settings during connection test");

            FormClosingEventArgs shutdownClose = new FormClosingEventArgs(
                CloseReason.WindowsShutDown,
                false);
            InvokePrivate(
                formType,
                form,
                "OnSettingsDialogClosing",
                form,
                shutdownClose);
            AssertTrue(!shutdownClose.Cancel,
                "Settings do not block Windows shutdown");
        }
        finally
        {
            prompt.Text = previousPrompt;
            items.Clear();
            foreach (object item in previousItems)
            {
                items.Add(item);
            }

            drafts.Clear();
            foreach (DictionaryEntry entry in previousDrafts)
            {
                drafts.Add(entry.Key, entry.Value);
            }

            SetField(
                formType,
                form,
                "generationCancellation",
                previousCancellation);
            SetField(
                formType,
                form,
                "connectionTestCancellation",
                previousConnectionCancellation);
            if (testCancellation != null)
            {
                testCancellation.Dispose();
            }
            if (testConnectionCancellation != null)
            {
                testConnectionCancellation.Dispose();
            }
        }
    }

    private static void TestWorkspaceLayoutAndIncrementalTranscript(
        Type formType,
        object form)
    {
        TableLayoutPanel workspace = GetField(
            formType,
            form,
            "workspaceLayout") as TableLayoutPanel;
        TableLayoutPanel conversationArea = GetField(
            formType,
            form,
            "conversationArea") as TableLayoutPanel;
        RowStyle composerRow = GetField(
            formType,
            form,
            "composerAreaRowStyle") as RowStyle;
        RowStyle attachmentRow = GetField(
            formType,
            form,
            "attachmentTrayRowStyle") as RowStyle;
        RichTextBox output = GetField(
            formType,
            form,
            "outputTextBox") as RichTextBox;
        ListView inputList = GetField(
            formType,
            form,
            "inputListView") as ListView;
        RichTextBox prompt = GetField(
            formType,
            form,
            "promptTextBox") as RichTextBox;
        Control composer = GetField(
            formType,
            form,
            "composerPanel") as Control;
        Panel dropTarget = GetField(
            formType,
            form,
            "fileDropTargetPanel") as Panel;
        Button settingsButton = GetField(
            formType,
            form,
            "settingsButton") as Button;
        Button readPathButton = GetField(
            formType,
            form,
            "readPathButton") as Button;
        Button generateButton = GetField(
            formType,
            form,
            "generateButton") as Button;
        Label contextSummary = GetField(
            formType,
            form,
            "contextSummaryLabel") as Label;
        ToolTip contextToolTip = GetField(
            formType,
            form,
            "contextSummaryToolTip") as ToolTip;
        Form settingsDialog = GetField(
            formType,
            form,
            "settingsDialog") as Form;
        Form pathDialog = GetField(
            formType,
            form,
            "pathInputDialog") as Form;
        TextBox endpoint = GetField(
            formType,
            form,
            "endpointTextBox") as TextBox;
        TextBox pathInput = GetField(
            formType,
            form,
            "pathTextBox") as TextBox;

        AssertTrue(
            workspace != null && workspace.RowCount == 3 &&
                conversationArea != null && conversationArea.RowCount == 2 &&
                composerRow != null && composerRow.Height >= 140F &&
                composerRow.Height <= 170F &&
                attachmentRow != null && attachmentRow.Height == 0F,
            "Workspace contains only header, transcript, composer and status");
        AssertTrue(
            settingsDialog != null && pathDialog != null &&
                endpoint != null && pathInput != null &&
                settingsDialog.Contains(endpoint) &&
                pathDialog.Contains(pathInput) &&
                !workspace.Contains(endpoint) &&
                !workspace.Contains(pathInput),
            "Connection and path inputs live in separate dialogs");
        AssertTrue(
            ContainsControlText(settingsDialog, "模型连接") &&
                ContainsControlText(settingsDialog, "技能与 MCP") &&
                ContainsControlText(settingsDialog, "会话与输入") &&
                ContainsControlText(settingsDialog, "维护"),
            "Settings dialog exposes the four configuration sections");
        AssertTrue(
            contextSummary != null &&
                contextSummary.AccessibleName == "当前会话上下文摘要" &&
                contextSummary.Text.IndexOf(
                    "条消息",
                    StringComparison.Ordinal) >= 0,
            "Context summary is visible in the workspace header");
        string fullContextSummary = contextToolTip == null
            ? string.Empty
            : contextToolTip.GetToolTip(contextSummary);
        int extensionOffset = fullContextSummary.IndexOf(
            "技能",
            StringComparison.Ordinal);
        int historyOffset = fullContextSummary.IndexOf(
            "历史",
            StringComparison.Ordinal);
        AssertTrue(
            extensionOffset >= 0 && historyOffset > extensionOffset &&
                contextToolTip != null &&
                contextSummary.AccessibleDescription == fullContextSummary,
            "Compact summary prioritizes extensions and exposes full text");

        Form window = form as Form;
        AssertTrue(
            window != null && output != null && inputList != null &&
                prompt != null && composer != null && dropTarget != null &&
                settingsButton != null && readPathButton != null &&
                generateButton != null,
            "Main workspace controls exist");
        window.Show();
        Application.DoEvents();
        AssertTrue(
            !settingsDialog.Visible && !pathDialog.Visible &&
                settingsButton.Parent != null,
            "Secondary controls stay out of the main workspace");
        Size originalSize = window.Size;
        window.Size = window.MinimumSize;
        InvokePrivate(formType, form, "UpdateConversationAreaRows");
        Application.DoEvents();
        AssertTrue(
            output.Visible && output.Height >= 128,
            "Narrow workspace keeps the transcript visible (height=" +
                output.Height + ")");
        AssertTrue(
            prompt.Visible && prompt.Enabled && prompt.Height > 0,
            "Narrow workspace keeps the prompt editor visible (height=" +
                prompt.Height + ")");
        AssertTrue(
            dropTarget.Visible && dropTarget.Width >= 120 &&
                !readPathButton.Visible,
            "Narrow workspace keeps drag-and-drop visible and paths off-canvas");
        AssertTrue(
            generateButton.Visible && generateButton.Width >= 64,
            "Narrow workspace keeps sending usable (visible=" +
                generateButton.Visible + ", width=" +
                generateButton.Width + ")");
        window.Size = originalSize;
        InvokePrivate(formType, form, "UpdateConversationAreaRows");
        Application.DoEvents();
        IntPtr inputListHandle = inputList.Handle;
        Type inputItemType = formType.Assembly.GetType(
            "FilePromptAIWin7.InputItem",
            true);
        Type inputKindType = formType.Assembly.GetType(
            "FilePromptAIWin7.InputKind",
            true);
        object inputItem = Activator.CreateInstance(inputItemType, true);
        inputItemType.GetProperty("Name").SetValue(
            inputItem,
            "layout-check.txt",
            null);
        inputItemType.GetProperty("Kind").SetValue(
            inputItem,
            Enum.Parse(inputKindType, "Text"),
            null);
        inputItemType.GetProperty("TextContent").SetValue(
            inputItem,
            "layout verification",
            null);
        AssertTrue(
            (bool)InvokePrivate(formType, form, "AddInputItem", inputItem),
            "Attachment tray accepts a pasted item");
        InvokePrivate(formType, form, "UpdateInputStatus");
        Application.DoEvents();
        AssertTrue(
            composerRow.Height >= 220F && attachmentRow.Height >= 58F &&
                inputList.Visible,
            "Composer expands its attachment tray when an item is present");
        inputList.Select();
        inputList.Items[0].Selected = true;
        Application.DoEvents();
        KeyEventArgs deleteKeys = new KeyEventArgs(Keys.Delete);
        RaiseKeyDown(inputList, deleteKeys);
        Application.DoEvents();
        AssertTrue(
            deleteKeys.Handled && inputList.Items.Count == 0 &&
                ((IList)GetField(formType, form, "inputItems")).Count == 0,
            "Delete removes the selected attachment from UI and draft state");
        InvokePrivate(formType, form, "OnClearClick", form, EventArgs.Empty);
        Application.DoEvents();
        AssertTrue(
            composerRow.Height <= 170F && attachmentRow.Height == 0F &&
                !inputList.Visible,
            "Composer collapses its attachment tray after items are cleared");
        window.Size = window.MinimumSize;
        InvokePrivate(formType, form, "UpdateConversationAreaRows");
        Application.DoEvents();
        AssertTrue(
            output.Height >= 128 && prompt.Height > 0,
            "Transcript and composer remain visible at minimum size");
        window.Size = originalSize;
        Application.DoEvents();
        AssertTrue(
            inputList.IsHandleCreated && inputList.Handle == inputListHandle,
            "Responsive column sizing keeps the drag-drop handle stable");

        object store = GetField(formType, form, "conversationStore");
        object session = store.GetType().GetProperty("CurrentSession")
            .GetValue(store, null);
        IList messages = (IList)session.GetType().GetProperty("Messages")
            .GetValue(session, null);
        Type messageType = formType.Assembly.GetType(
            "FilePromptAIWin7.ConversationMessage",
            true);
        const string previousAnswer = "unique previous answer";
        StringBuilder longAnswer = new StringBuilder(previousAnswer);
        for (int line = 0; line < 180; line++)
        {
            longAnswer.Append("\r\nscroll verification line ");
            longAnswer.Append(line);
        }
        messages.Clear();
        messages.Add(Activator.CreateInstance(
            messageType,
            new object[] { "user", "previous question" }));
        messages.Add(Activator.CreateInstance(
            messageType,
            new object[] { "assistant", longAnswer.ToString() }));
        InvokePrivate(formType, form, "LoadCurrentSession");

        AssertTrue(
            output != null && CountOccurrences(output.Text, previousAnswer) == 1,
            "Existing transcript is rendered once");
        int previousAnswerStart = output.Text.IndexOf(
            previousAnswer,
            StringComparison.Ordinal);
        output.Select(previousAnswerStart, previousAnswer.Length);
        output.SelectionBackColor = Color.LemonChiffon;

        InvokePrivate(
            formType,
            form,
            "PrepareStreamingTurn",
            session,
            "next question");
        AssertTrue(
            CountOccurrences(output.Text, previousAnswer) == 1,
            "Starting a turn does not redraw duplicate history");
        output.Select(previousAnswerStart, previousAnswer.Length);
        AssertTrue(
            output.SelectionBackColor.ToArgb() ==
                Color.LemonChiffon.ToArgb(),
            "Starting a turn preserves existing transcript formatting");

        CancellationTokenSource cancellation = new CancellationTokenSource();
        try
        {
            SetField(formType, form, "generationCancellation", cancellation);
            SetField(formType, form, "activeGenerationSequence", 41);
            SetField(formType, form, "followStreamTail", false);
            SetField(
                formType,
                form,
                "streamedResponse",
                new StringBuilder());
            output.Select(previousAnswerStart, previousAnswer.Length);
            SetScrollPosition(output, 220);
            NativePoint scrollBeforeFlush = GetScrollPosition(output);
            AssertTrue(
                scrollBeforeFlush.Y > 0,
                "Transcript has a measurable historical scroll position");
            InvokePrivate(
                formType,
                form,
                "AppendOutputForGeneration",
                "current chunk",
                41);
            InvokePrivate(
                formType,
                form,
                "AppendOutputForGeneration",
                "stale chunk",
                40);
            InvokePrivate(
                formType,
                form,
                "FlushPendingOutput");
            NativePoint scrollAfterFlush = GetScrollPosition(output);
            AssertTrue(
                output.Text.IndexOf(
                    "current chunk",
                    StringComparison.Ordinal) >= 0 &&
                output.Text.IndexOf(
                    "stale chunk",
                    StringComparison.Ordinal) < 0,
                "Stale stream chunks are ignored");
            AssertScrollPreserved(
                scrollBeforeFlush,
                scrollAfterFlush,
                "Streaming flush preserves the historical viewport");

            NativePoint scrollBeforeFinalize = GetScrollPosition(output);
            InvokePrivate(
                formType,
                form,
                "FinalizeStreamingTurn",
                "用户要求：\r\nnext question",
                "## final answer");
            NativePoint scrollAfterFinalize = GetScrollPosition(output);
            AssertTrue(
                CountOccurrences(output.Text, previousAnswer) == 1 &&
                output.Text.IndexOf(
                    "final answer",
                    StringComparison.Ordinal) >= 0,
                "Only the current assistant block is finalized");
            AssertScrollPreserved(
                scrollBeforeFinalize,
                scrollAfterFinalize,
                "Finalizing a turn preserves the historical viewport");

            NativePoint scrollBeforeFinalRemoval = GetScrollPosition(output);
            InvokePrivate(
                formType,
                form,
                "RemoveStreamingTurnPreview",
                session);
            NativePoint scrollAfterFinalRemoval = GetScrollPosition(output);
            AssertScrollPreserved(
                scrollBeforeFinalRemoval,
                scrollAfterFinalRemoval,
                "Removing a preview preserves the historical viewport");
            InvokePrivate(
                formType,
                form,
                "PrepareStreamingTurn",
                session,
                "cancelled question");
            output.Select(previousAnswerStart, previousAnswer.Length);
            SetField(formType, form, "followStreamTail", false);
            SetScrollPosition(output, 260);
            NativePoint scrollBeforeCancel = GetScrollPosition(output);
            InvokePrivate(
                formType,
                form,
                "RemoveStreamingTurnPreview",
                session);
            NativePoint scrollAfterCancel = GetScrollPosition(output);
            AssertTrue(
                output.SelectionStart == previousAnswerStart &&
                    CountOccurrences(output.Text, previousAnswer) == 1,
                "Cancelling a turn preserves the historical selection");
            AssertScrollPreserved(
                scrollBeforeCancel,
                scrollAfterCancel,
                "Cancelling a turn preserves the historical viewport");
        }
        finally
        {
            SetField(formType, form, "activeGenerationSequence", 0);
            SetField(formType, form, "generationCancellation", null);
            SetField(formType, form, "streamedResponse", null);
            cancellation.Dispose();
            messages.Clear();
            InvokePrivate(formType, form, "LoadCurrentSession");
        }
    }

    private static void TestSessionManagement(Type formType, object form)
    {
        Button currentButton = GetField(
            formType,
            form,
            "currentSessionsButton") as Button;
        Button archivedButton = GetField(
            formType,
            form,
            "archivedSessionsButton") as Button;
        Button newButton = GetField(
            formType,
            form,
            "newSessionButton") as Button;
        TextBox search = GetField(
            formType,
            form,
            "sessionSearchTextBox") as TextBox;
        ListBox list = GetField(
            formType,
            form,
            "sessionListBox") as ListBox;
        ContextMenuStrip menu = list == null
            ? null
            : list.ContextMenuStrip;

        AssertTrue(
            currentButton != null && archivedButton != null &&
                currentButton.Text == "当前" &&
                archivedButton.Text == "已归档" &&
                currentButton.AccessibleName == "查看当前会话" &&
                archivedButton.AccessibleName == "查看已归档会话",
            "Current and archived session controls exist");
        AssertTrue(
            list != null && search != null && newButton != null &&
                menu != null && menu.Items.Count == 6 &&
                menu.Items[0] is ToolStripMenuItem &&
                menu.Items[1] is ToolStripMenuItem &&
                menu.Items[2] is ToolStripMenuItem &&
                menu.Items[3] is ToolStripSeparator &&
                menu.Items[4] is ToolStripMenuItem &&
                menu.Items[4].Text == "重命名会话..." &&
                menu.Items[5] is ToolStripMenuItem &&
                menu.Items[5].Text == "删除当前会话...",
            "Session list exposes management actions");

        object store = GetField(formType, form, "conversationStore");
        PropertyInfo sessionsProperty = store.GetType().GetProperty(
            "Sessions");
        PropertyInfo currentSessionProperty = store.GetType().GetProperty(
            "CurrentSession");
        PropertyInfo currentSessionIdProperty = store.GetType().GetProperty(
            "CurrentSessionId");
        IList originalSessions = (IList)sessionsProperty.GetValue(store, null);
        HashSet<string> originalIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (object session in originalSessions)
        {
            originalIds.Add(GetSessionId(session));
        }

        string originalCurrentId = (string)currentSessionIdProperty.GetValue(
            store,
            null);
        bool originalArchiveView = (bool)GetField(
            formType,
            form,
            "showArchivedSessions");
        string originalSearch = search.Text;
        string marker = Guid.NewGuid().ToString("N");
        string activeTitle = "UI active " + marker;
        string newerTitle = "UI newer " + marker;
        string pinnedTitle = "UI pinned " + marker;
        string archivedTitle = "UI archived " + marker;
        string sourceTitle = "UI source " + marker;
        object active = null;
        object newer = null;
        object pinned = null;
        object archived = null;
        object source = null;
        CancellationTokenSource busyCancellation = null;
        try
        {
            active = InvokePublic(store, "CreateSession", activeTitle);
            newer = InvokePublic(store, "CreateSession", newerTitle);
            pinned = InvokePublic(store, "CreateSession", pinnedTitle);
            archived = InvokePublic(store, "CreateSession", archivedTitle);
            source = InvokePublic(store, "CreateSession", sourceTitle);
            InvokePublic(
                store,
                "SetSessionPinned",
                GetSessionId(pinned),
                true);
            InvokePublic(
                store,
                "SetSessionArchived",
                GetSessionId(archived),
                true);

            DateTime baseline = DateTime.UtcNow.AddHours(-2);
            SetProperty(active, "UpdatedAt", baseline.AddMinutes(1));
            SetProperty(newer, "UpdatedAt", baseline.AddMinutes(4));
            SetProperty(pinned, "UpdatedAt", baseline);
            SetProperty(archived, "UpdatedAt", baseline.AddMinutes(3));
            SetProperty(source, "UpdatedAt", baseline.AddMinutes(5));

            InvokePrivate(formType, form, "ClearSessionSearch");
            ((System.Windows.Forms.Timer)GetField(
                formType,
                form,
                "sessionSearchTimer")).Stop();
            SetField(formType, form, "showArchivedSessions", false);
            InvokePrivate(formType, form, "UpdateSessionViewButtons");
            InvokePrivate(formType, form, "RefreshSessionList");
            AssertTrue(
                FindSessionIndex(list.Items, GetSessionId(active)) >= 0 &&
                    FindSessionIndex(list.Items, GetSessionId(newer)) >= 0 &&
                    FindSessionIndex(list.Items, GetSessionId(source)) >= 0 &&
                    FindSessionIndex(list.Items, GetSessionId(archived)) < 0,
                "Current view filters out archived sessions");
            AssertTrue(
                FindSessionIndex(list.Items, GetSessionId(pinned)) == 0,
                "Pinned sessions sort before newer current sessions");

            InvokePrivate(
                formType,
                form,
                "SetSessionArchiveView",
                true);
            AssertTrue(
                (bool)GetField(formType, form, "showArchivedSessions") &&
                    FindSessionIndex(
                        list.Items,
                        GetSessionId(archived)) >= 0 &&
                    FindSessionIndex(list.Items, GetSessionId(active)) < 0 &&
                    GetSessionId(list.SelectedItem) == GetSessionId(archived),
                "Archived view filters and selects an archived session");
            InvokePrivate(
                formType,
                form,
                "SetSessionArchiveView",
                false);

            InvokePublic(
                store,
                "SelectSession",
                GetSessionId(active));
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");

            int rightClickIndex = FindSessionIndex(
                list.Items,
                GetSessionId(newer));
            Rectangle rightClickBounds = list.GetItemRectangle(
                rightClickIndex);
            RaiseMouseDown(
                list,
                new MouseEventArgs(
                    MouseButtons.Right,
                    1,
                    rightClickBounds.Left + 4,
                    rightClickBounds.Top + 4,
                    0));
            AssertTrue(
                GetSessionId(list.SelectedItem) == GetSessionId(newer) &&
                    GetSessionId(
                        currentSessionProperty.GetValue(store, null)) ==
                        GetSessionId(newer),
                "Right-click selects its session before opening the menu");
            InvokePrivate(
                formType,
                form,
                "OnToggleSessionPinnedClick",
                menu,
                EventArgs.Empty);
            AssertTrue(
                (bool)GetProperty(newer, "IsPinned") &&
                    !(bool)GetProperty(active, "IsPinned"),
                "Session menu action targets the right-clicked row");
            InvokePrivate(
                formType,
                form,
                "OnToggleSessionPinnedClick",
                menu,
                EventArgs.Empty);
            InvokePublic(
                store,
                "SelectSession",
                GetSessionId(active));
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");

            RaiseMenuOpening(menu);
            ToolStripMenuItem pinItem = menu.Items[0] as ToolStripMenuItem;
            ToolStripMenuItem archiveItem =
                menu.Items[1] as ToolStripMenuItem;
            ToolStripMenuItem branchItem =
                menu.Items[2] as ToolStripMenuItem;
            AssertTrue(
                pinItem.Text == "置顶会话" &&
                    archiveItem.Text == "归档会话" &&
                    branchItem.Text == "从最新回复创建分支" &&
                    !branchItem.Enabled,
                "Session menu labels an unpinned active session and disables branching without a reply");

            InvokePrivate(
                formType,
                form,
                "OnToggleSessionPinnedClick",
                menu,
                EventArgs.Empty);
            RaiseMenuOpening(menu);
            AssertTrue(
                (bool)GetProperty(active, "IsPinned") &&
                    pinItem.Text == "取消置顶" &&
                    FindSessionIndex(list.Items, GetSessionId(active)) == 0,
                "Pin action updates its label and moves the session first");
            InvokePrivate(
                formType,
                form,
                "OnToggleSessionPinnedClick",
                menu,
                EventArgs.Empty);
            AssertTrue(
                !(bool)GetProperty(active, "IsPinned") &&
                    FindSessionIndex(list.Items, GetSessionId(pinned)) == 0,
                "Unpin action restores pinned-first ordering");

            InvokePrivate(
                formType,
                form,
                "OnToggleSessionArchivedClick",
                menu,
                EventArgs.Empty);
            object selectedAfterArchive = currentSessionProperty.GetValue(
                store,
                null);
            AssertTrue(
                (bool)GetProperty(active, "IsArchived") &&
                    GetSessionId(selectedAfterArchive) !=
                        GetSessionId(active) &&
                    FindSessionIndex(list.Items, GetSessionId(active)) < 0 &&
                    GetSessionId(list.SelectedItem) ==
                        GetSessionId(selectedAfterArchive),
                "Archiving the selected session chooses another current session");

            InvokePrivate(
                formType,
                form,
                "SetSessionArchiveView",
                true);
            AssertTrue(
                GetSessionId(currentSessionProperty.GetValue(store, null)) ==
                    GetSessionId(active) &&
                    GetSessionId(list.SelectedItem) == GetSessionId(active),
                "Archived view selects the newly archived session");
            RaiseMenuOpening(menu);
            AssertTrue(
                archiveItem.Text == "移回当前会话",
                "Archived session menu offers to move the session back");
            InvokePrivate(
                formType,
                form,
                "OnToggleSessionArchivedClick",
                menu,
                EventArgs.Empty);
            AssertTrue(
                !(bool)GetProperty(active, "IsArchived") &&
                    !(bool)GetField(
                        formType,
                        form,
                        "showArchivedSessions") &&
                    GetSessionId(currentSessionProperty.GetValue(store, null)) ==
                        GetSessionId(active) &&
                    GetSessionId(list.SelectedItem) == GetSessionId(active),
                "Unarchiving keeps the session selected in the current view");

            IList sourceMessages = (IList)GetProperty(source, "Messages");
            Type messageType = formType.Assembly.GetType(
                "FilePromptAIWin7.ConversationMessage",
                true);
            object userMessage = Activator.CreateInstance(
                messageType,
                new object[] { "user", "branch source question" });
            object assistantMessage = Activator.CreateInstance(
                messageType,
                new object[] { "assistant", "branch source answer" });
            SetProperty(
                assistantMessage,
                "ParentMessageId",
                (string)GetProperty(userMessage, "Id"));
            object trailingMessage = Activator.CreateInstance(
                messageType,
                new object[] { "user", "trailing question" });
            SetProperty(
                trailingMessage,
                "ParentMessageId",
                (string)GetProperty(assistantMessage, "Id"));
            sourceMessages.Add(userMessage);
            sourceMessages.Add(assistantMessage);
            sourceMessages.Add(trailingMessage);
            InvokePublic(
                store,
                "SelectSession",
                GetSessionId(source));
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            RaiseMenuOpening(menu);
            AssertTrue(
                branchItem.Enabled,
                "Branch action is enabled when an assistant reply exists");

            int sessionCountBeforeBranch =
                ((IList)sessionsProperty.GetValue(store, null)).Count;
            string sourceId = GetSessionId(source);
            string assistantId = (string)GetProperty(
                assistantMessage,
                "Id");
            InvokePrivate(
                formType,
                form,
                "OnBranchSessionClick",
                menu,
                EventArgs.Empty);
            object branch = currentSessionProperty.GetValue(store, null);
            IList branchMessages = (IList)GetProperty(branch, "Messages");
            AssertTrue(
                ((IList)sessionsProperty.GetValue(store, null)).Count ==
                    sessionCountBeforeBranch + 1 &&
                    GetSessionId(branch) != sourceId &&
                    (string)GetProperty(branch, "SourceSessionId") == sourceId &&
                    (string)GetProperty(branch, "SourceMessageId") ==
                        assistantId &&
                    branchMessages.Count == 2 &&
                    sourceMessages.Count == 3 &&
                    GetSessionId(list.SelectedItem) == GetSessionId(branch),
                "Branching from the latest assistant keeps the source and selects the branch");

            busyCancellation = new CancellationTokenSource();
            SetField(
                formType,
                form,
                "generationCancellation",
                busyCancellation);
            InvokePrivate(
                formType,
                form,
                "SetSessionNavigationEnabled",
                false);
            RaiseMenuOpening(menu);
            AssertTrue(
                !list.Enabled && !search.Enabled &&
                    !currentButton.Enabled && !archivedButton.Enabled &&
                    !newButton.Enabled && !pinItem.Enabled &&
                    !archiveItem.Enabled && !branchItem.Enabled,
                "Busy state disables session navigation and mutations");
            SetField(formType, form, "generationCancellation", null);
            InvokePrivate(
                formType,
                form,
                "SetSessionNavigationEnabled",
                true);
            busyCancellation.Dispose();
            busyCancellation = null;
            AssertTrue(
                list.Enabled && search.Enabled && currentButton.Enabled &&
                    archivedButton.Enabled && newButton.Enabled,
                "Session navigation is restored after generation finishes");
        }
        finally
        {
            if (busyCancellation != null)
            {
                SetField(formType, form, "generationCancellation", null);
                InvokePrivate(
                    formType,
                    form,
                    "SetSessionNavigationEnabled",
                    true);
                busyCancellation.Dispose();
            }

            IList sessions = (IList)sessionsProperty.GetValue(store, null);
            List<string> temporaryIds = new List<string>();
            foreach (object session in sessions)
            {
                string id = GetSessionId(session);
                if (!originalIds.Contains(id))
                {
                    temporaryIds.Add(id);
                }
            }

            foreach (string id in temporaryIds)
            {
                InvokePublic(store, "DeleteSession", id);
            }

            if (!string.IsNullOrEmpty(originalCurrentId))
            {
                InvokePublic(store, "SelectSession", originalCurrentId);
            }

            SetField(
                formType,
                form,
                "showArchivedSessions",
                originalArchiveView);
            System.Windows.Forms.Timer searchTimer =
                (System.Windows.Forms.Timer)GetField(
                formType,
                form,
                "sessionSearchTimer");
            searchTimer.Stop();
            search.Text = originalSearch;
            searchTimer.Stop();
            InvokePrivate(formType, form, "UpdateSessionViewButtons");
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            InvokePrivate(formType, form, "RestoreCurrentDraft");
        }
    }

    private static void TestDeleteSessionDraftTransaction(
        Type formType,
        object form)
    {
        object store = GetField(formType, form, "conversationStore");
        PropertyInfo currentSessionIdProperty = store.GetType().GetProperty(
            "CurrentSessionId");
        MethodInfo getSessionMethod = store.GetType().GetMethod(
            "GetSession");
        RichTextBox prompt = GetField(
            formType,
            form,
            "promptTextBox") as RichTextBox;
        IDictionary drafts = (IDictionary)GetField(
            formType,
            form,
            "sessionDrafts");
        string storagePath = (string)store.GetType().GetField(
            "storagePath",
            BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(store);
        string originalCurrentId = (string)currentSessionIdProperty.GetValue(
            store,
            null);
        string originalPrompt = prompt.Text;
        object session = null;
        string sessionId = string.Empty;
        const string draftText = "draft survives failed delete";
        try
        {
            session = InvokePublic(
                store,
                "CreateSession",
                "UI delete draft " + Guid.NewGuid().ToString("N"));
            sessionId = GetSessionId(session);
            InvokePublic(store, "SelectSession", sessionId);
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            prompt.Text = draftText;
            AssertTrue(
                !drafts.Contains(sessionId),
                "Delete transaction starts with an uncached visible draft");

            Exception failure = null;
            using (FileStream locked = new FileStream(
                storagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                try
                {
                    InvokePrivate(
                        formType,
                        form,
                        "DeleteSessionAndClearDraft",
                        session);
                }
                catch (TargetInvocationException exception)
                {
                    failure = exception.InnerException;
                }
            }

            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            InvokePrivate(formType, form, "RestoreCurrentDraft");
            AssertTrue(
                failure is IOException &&
                    getSessionMethod.Invoke(
                        store,
                        new object[] { sessionId }) != null &&
                    drafts.Contains(sessionId) &&
                    prompt.Text == draftText,
                "Failed session delete preserves the session and visible draft");

            bool deleted = (bool)InvokePrivate(
                formType,
                form,
                "DeleteSessionAndClearDraft",
                session);
            AssertTrue(
                deleted &&
                    getSessionMethod.Invoke(
                        store,
                        new object[] { sessionId }) == null &&
                    !drafts.Contains(sessionId),
                "Successful session delete removes the session and its draft");
            session = null;
        }
        finally
        {
            prompt.Text = originalPrompt;
            if (session != null &&
                getSessionMethod.Invoke(
                    store,
                    new object[] { sessionId }) != null)
            {
                drafts.Remove(sessionId);
                InvokePublic(store, "DeleteSession", sessionId);
            }

            if (!string.IsNullOrEmpty(originalCurrentId) &&
                getSessionMethod.Invoke(
                    store,
                    new object[] { originalCurrentId }) != null)
            {
                InvokePublic(store, "SelectSession", originalCurrentId);
            }

            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            InvokePrivate(formType, form, "RestoreCurrentDraft");
        }
    }

    private static void TestSendShortcutConfig(
        Type formType,
        object form)
    {
        object generateButton = GetField(formType, form, "generateButton");
        ContextMenuStrip menu =
            ((Button)generateButton).ContextMenuStrip;
        AssertTrue(menu != null, "Send button shortcut menu exists");

        int checkedCount = 0;
        int optionCount = 0;
        foreach (ToolStripItem item in menu.Items)
        {
            ToolStripMenuItem menuItem = item as ToolStripMenuItem;
            if (menuItem != null && menuItem.Tag != null)
            {
                optionCount++;
                checkedCount += menuItem.Checked ? 1 : 0;
            }
        }
        AssertTrue(
            optionCount == 3,
            "Send button shortcut menu has three modes");
        AssertTrue(
            checkedCount == 1,
            "Send button shortcut menu has one checked option");

        InvokePrivate(formType, form, "SetSendShortcutMode", "CtrlEnter");
        AssertTrue(
            !(bool)InvokePrivate(
                formType, form, "IsEnterSendShortcut", false, false),
            "CtrlEnter mode: plain Enter does not send");
        AssertTrue(
            (bool)InvokePrivate(
                formType, form, "IsEnterSendShortcut", true, false),
            "CtrlEnter mode: Ctrl+Enter sends");

        InvokePrivate(formType, form, "SetSendShortcutMode", "Enter");
        AssertTrue(
            (bool)InvokePrivate(
                formType, form, "IsEnterSendShortcut", false, false),
            "Enter mode: plain Enter sends");
        AssertTrue(
            !(bool)InvokePrivate(
                formType, form, "IsEnterSendShortcut", true, false),
            "Enter mode: Ctrl+Enter does not send");

        InvokePrivate(formType, form, "SetSendShortcutMode", "Both");
        AssertTrue(
            (bool)InvokePrivate(
                formType, form, "IsEnterSendShortcut", false, false),
            "Both mode: plain Enter sends");
        AssertTrue(
            (bool)InvokePrivate(
                formType, form, "IsEnterSendShortcut", true, false),
            "Both mode: Ctrl+Enter sends");
        AssertTrue(
            !(bool)InvokePrivate(
                formType, form, "IsEnterSendShortcut", false, true),
            "Shift+Enter inserts newline");
    }

    private static void TestMainKeyboardShortcuts(Type formType, object form)
    {
        TextBox search = GetField(
            formType,
            form,
            "sessionSearchTextBox") as TextBox;
        RichTextBox prompt = GetField(
            formType,
            form,
            "promptTextBox") as RichTextBox;
        AssertTrue(search != null && prompt != null,
            "Keyboard shortcut targets exist");
        search.Text = "find this session";
        prompt.Focus();
        KeyEventArgs findKeys = new KeyEventArgs(Keys.Control | Keys.F);
        InvokePrivate(formType, form, "OnMainKeyDown", form, findKeys);
        Application.DoEvents();
        AssertTrue(
            search.Focused && search.SelectionLength == search.TextLength &&
                findKeys.SuppressKeyPress,
            "Ctrl+F focuses and selects the session search");

        object store = GetField(formType, form, "conversationStore");
        int before = ((IList)store.GetType().GetProperty("Sessions")
            .GetValue(store, null)).Count;
        KeyEventArgs newKeys = new KeyEventArgs(Keys.Control | Keys.N);
        InvokePrivate(formType, form, "OnMainKeyDown", form, newKeys);
        int afterNew = ((IList)store.GetType().GetProperty("Sessions")
            .GetValue(store, null)).Count;
        AssertTrue(
            afterNew == before + 1 && newKeys.SuppressKeyPress,
            "Ctrl+N creates a session while idle");

        SetField(formType, form, "isAddingFiles", true);
        try
        {
            KeyEventArgs keys = new KeyEventArgs(Keys.Control | Keys.N);
            InvokePrivate(formType, form, "OnMainKeyDown", form, keys);
            int after = ((IList)store.GetType().GetProperty("Sessions")
                .GetValue(store, null)).Count;
            AssertTrue(
                afterNew == after,
                "Ctrl+N is blocked during extraction");
        }
        finally
        {
            SetField(formType, form, "isAddingFiles", false);
        }

        CancellationTokenSource cancellation = new CancellationTokenSource();
        try
        {
            SetField(formType, form, "generationCancellation", cancellation);
            KeyEventArgs escapeKeys = new KeyEventArgs(Keys.Escape);
            InvokePrivate(
                formType,
                form,
                "OnMainKeyDown",
                form,
                escapeKeys);
            AssertTrue(
                cancellation.IsCancellationRequested &&
                    escapeKeys.SuppressKeyPress,
                "Escape cancels an active generation");
        }
        finally
        {
            SetField(formType, form, "generationCancellation", null);
            cancellation.Dispose();
        }
    }

    private static void TestPromptActions(Type formType, object form)
    {
        Button actionsButton = GetField(
            formType,
            form,
            "promptActionsButton") as Button;
        AssertTrue(actionsButton != null, "Prompt actions button exists");
        ContextMenuStrip menu = actionsButton.ContextMenuStrip;
        AssertTrue(menu != null && menu.Items.Count >= 7,
            "Prompt actions menu has common tasks");
        ToolStripMenuItem first = menu.Items[0] as ToolStripMenuItem;
        AssertTrue(first != null && first.Enabled,
            "Prompt action item is enabled");
        first.PerformClick();
        RichTextBox prompt = GetField(
            formType,
            form,
            "promptTextBox") as RichTextBox;
        AssertTrue(prompt != null && prompt.Text.Length > 0,
            "Prompt action fills the prompt box");
        prompt.Clear();

        object store = GetField(formType, form, "conversationStore");
        object session = store.GetType().GetProperty("CurrentSession")
            .GetValue(store, null);
        IList messages = (IList)session.GetType().GetProperty("Messages")
            .GetValue(session, null);
        Type messageType = formType.Assembly.GetType(
            "FilePromptAIWin7.ConversationMessage",
            true);
        messages.Add(Activator.CreateInstance(
            messageType,
            new object[]
            {
                "user",
                "\u7528\u6237\u8981\u6c42\uff1aoriginal instruction\r\n" +
                "\u4ee5\u4e0b\u8d44\u6599\u7531\u7528\u6237\u4e3b\u52a8\u6dfb\u52a0"
            }));
        try
        {
            InvokePrivate(formType, form, "LoadLastPromptForEditing");
            AssertTrue(prompt.Text == "original instruction",
                "Last user prompt loads for editing");

            messages.Clear();
            prompt.Clear();
            messages.Add(Activator.CreateInstance(
                messageType,
                new object[]
                {
                    "user",
                    "\u7528\u6237\u8981\u6c42\uff1abinary instruction\r\n\r\n" +
                    "\u4ee5\u4e0b\u56fe\u7247\u6216\u5185\u8054\u6587\u4ef6\u4ec5\u968f\u672c\u8f6e\u53d1\u9001\uff1b" +
                    "\u540e\u7eed\u8f6e\u6b21\u5982\u9700\u91cd\u65b0\u5206\u6790\uff0c\u8bf7\u518d\u6b21\u4e3b\u52a8\u6dfb\u52a0\uff1a\r\n" +
                    "- pasted.png\uff08\u56fe\u7247\uff0c3 \u5b57\u8282\uff09"
                }));
            InvokePrivate(formType, form, "LoadLastPromptForEditing");
            AssertTrue(prompt.Text == "binary instruction",
                "Binary attachment notes stay out of loaded prompt");

            messages.Clear();
            prompt.Clear();
            messages.Add(Activator.CreateInstance(
                messageType,
                new object[]
                {
                    "user",
                    "\u7528\u6237\u8981\u6c42\uff1amixed instruction\r\n" +
                    "\u4ee5\u4e0b\u8d44\u6599\u7531\u7528\u6237\u4e3b\u52a8\u6dfb\u52a0\r\n" +
                    "\u4ee5\u4e0b\u56fe\u7247\u6216\u5185\u8054\u6587\u4ef6\u4ec5\u968f\u672c\u8f6e\u53d1\u9001"
                }));
            InvokePrivate(formType, form, "LoadLastPromptForEditing");
            AssertTrue(prompt.Text == "mixed instruction",
                "Mixed attachment notes stay out of loaded prompt");
        }
        finally
        {
            messages.Clear();
            prompt.Clear();
        }
    }
    private static void TestDragBusyGuard(
        Type formType,
        object form,
        string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        string file = Path.Combine(dataRoot, "drag.txt");
        File.WriteAllText(file, "drag content");
        CancellationTokenSource cancellation = new CancellationTokenSource();
        SetField(
            formType,
            form,
            "generationCancellation",
            cancellation);
        try
        {
            DataObject data = new DataObject();
            data.SetData(DataFormats.FileDrop, new string[] { file });
            DragEventArgs drag = new DragEventArgs(
                data,
                0,
                0,
                0,
                DragDropEffects.Copy,
                DragDropEffects.None);
            InvokePrivate(formType, form, "OnDragEnter", form, drag);
            AssertTrue(
                drag.Effect == DragDropEffects.None,
                "File drop is blocked during generation");
        }
        finally
        {
            SetField(formType, form, "generationCancellation", null);
            cancellation.Dispose();
        }

        IList inputItems = (IList)GetField(
            formType,
            form,
            "inputItems");
        int before = inputItems.Count;
        DataObject acceptedData = new DataObject();
        acceptedData.SetData(
            DataFormats.FileDrop,
            new string[] { file });
        DragEventArgs acceptedDrag = new DragEventArgs(
            acceptedData,
            0,
            0,
            0,
            DragDropEffects.Copy,
            DragDropEffects.Copy);
        InvokePrivate(
            formType,
            form,
            "OnDragDrop",
            GetField(formType, form, "fileDropTargetPanel"),
            acceptedDrag);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
            ((bool)GetField(formType, form, "isAddingFiles") ||
                inputItems.Count == before))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        AssertTrue(
            inputItems.Count == before + 1,
            "File drop extracts and adds the selected file");
        InvokePrivate(
            formType,
            form,
            "OnClearClick",
            form,
            EventArgs.Empty);
    }

    private static void TestFileDropRegistration(
        Type formType,
        object form)
    {
        Form window = form as Form;
        Control composerPanel = GetField(
            formType,
            form,
            "composerPanel") as Control;
        ListView inputList = GetField(
            formType,
            form,
            "inputListView") as ListView;
        Panel dropTarget = GetField(
            formType,
            form,
            "fileDropTargetPanel") as Panel;
        AssertTrue(
            window != null && composerPanel != null && inputList != null &&
                dropTarget != null,
            "File drop controls exist");
        AssertTrue(
            !window.AllowDrop && !composerPanel.AllowDrop &&
                !inputList.AllowDrop,
            "Child controls do not register duplicate Win7 drop targets");

        IntPtr handle = window.Handle;
        InvokePrivate(
            formType,
            form,
            "InitializeFileDropTarget");
        AssertTrue(
            dropTarget.AllowDrop,
            "Dedicated file strip registers the drop target on an STA thread");

        InvokePrivate(
            formType,
            form,
            "DisableFileDropTarget");
        AssertTrue(
            !dropTarget.AllowDrop,
            "Dedicated file strip revokes the drop target before disposal");
    }

    private static void TestCurrentTurnTextBudget(
        Type formType,
        object form)
    {
        IList inputItems = (IList)GetField(formType, form, "inputItems");
        Type inputType = formType.Assembly.GetType(
            "FilePromptAIWin7.InputItem",
            true);
        Type kindType = formType.Assembly.GetType(
            "FilePromptAIWin7.InputKind",
            true);
        try
        {
            inputItems.Clear();
            object textItem = Activator.CreateInstance(inputType, true);
            inputType.GetProperty("Name").SetValue(
                textItem,
                "large-unicode.txt",
                null);
            inputType.GetProperty("Kind").SetValue(
                textItem,
                Enum.Parse(kindType, "Text"),
                null);
            inputType.GetProperty("TextContent").SetValue(
                textItem,
                RepeatText("\uD83D\uDE00", 60000),
                null);
            inputItems.Add(textItem);

            string systemWithinBudget = new string('s', 5000);
            object[] buildArguments = new object[]
            {
                "总结文件",
                systemWithinBudget,
                null,
                null,
                false
            };
            bool built = (bool)InvokePrivate(
                formType,
                form,
                "TryBuildCombinedPrompt",
                buildArguments);
            string combined = buildArguments[2] as string;
            AssertTrue(built, "Large extracted text still builds a prompt");
            AssertTrue(
                (bool)buildArguments[4],
                "Large extracted text is marked as truncated");
            AssertTrue(
                CountUnicodeCharacters(systemWithinBudget) +
                    CountUnicodeCharacters(combined) <= 48000L,
                "Combined system and current-turn text stays within budget");
            AssertTrue(
                combined != null && combined.IndexOf(
                    "文件正文超过本轮 48,000 字符预算",
                    StringComparison.Ordinal) >= 0,
                "Truncated file text includes an explicit marker");
            AssertTrue(
                !HasUnpairedSurrogate(combined),
                "File text truncation preserves Unicode scalar boundaries");

            object[] systemArguments = new object[]
            {
                "简短要求",
                new string('S', 48000),
                null,
                null,
                false
            };
            bool systemAccepted = (bool)InvokePrivate(
                formType,
                form,
                "TryBuildCombinedPrompt",
                systemArguments);
            AssertTrue(
                !systemAccepted && Convert.ToString(systemArguments[3])
                    .IndexOf("系统提示", StringComparison.Ordinal) >= 0,
                "Oversized skill system prompt is rejected before sending");

            object[] instructionArguments = new object[]
            {
                new string('U', 48000),
                string.Empty,
                null,
                null,
                false
            };
            bool instructionAccepted = (bool)InvokePrivate(
                formType,
                form,
                "TryBuildCombinedPrompt",
                instructionArguments);
            AssertTrue(
                !instructionAccepted && Convert.ToString(instructionArguments[3])
                    .IndexOf("文字描述", StringComparison.Ordinal) >= 0,
                "Oversized user instruction is rejected before sending");

            inputItems.Clear();
            object binaryItem = Activator.CreateInstance(inputType, true);
            inputType.GetProperty("Name").SetValue(
                binaryItem,
                "large-image.png",
                null);
            inputType.GetProperty("Kind").SetValue(
                binaryItem,
                Enum.Parse(kindType, "Image"),
                null);
            inputType.GetProperty("BinaryData").SetValue(
                binaryItem,
                new byte[64 * 1024],
                null);
            inputType.GetProperty("MimeType").SetValue(
                binaryItem,
                "image/png",
                null);
            inputItems.Add(binaryItem);

            object selection = InvokePrivate(
                formType,
                form,
                "SelectConversationTextContext",
                CreateHistory(formType.Assembly, 20000),
                string.Empty,
                "用户要求：\r\n继续分析\r\n");
            int selectedMessages = Convert.ToInt32(selection.GetType()
                .GetProperty("SelectedMessageCount")
                .GetValue(selection, null));
            AssertTrue(
                selectedMessages == 2,
                "Binary Base64 size does not consume the text-history budget");
        }
        finally
        {
            inputItems.Clear();
        }
    }

    private static void TestPathInput(
        Type formType,
        object form,
        string dataRoot)
    {
        TextBox pathInput = GetField(
            formType,
            form,
            "pathTextBox") as TextBox;
        Button readButton = GetField(
            formType,
            form,
            "readPathButton") as Button;
        AssertTrue(pathInput != null && pathInput.Multiline,
            "Path input accepts multiple lines");
        AssertTrue(readButton != null && readButton.Text == "添加",
            "Path add action exists in the dedicated dialog");

        MethodInfo parser = formType.GetMethod(
            "ParsePastedPaths",
            BindingFlags.Static | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
        string[] parsed = (string[])parser.Invoke(
            null,
            new object[]
            {
                "  \"C:\\Work Files\\a.txt\"  \r\n" +
                "C:\\Work Files\\A.txt\nD:\\b.csv\r"
            });
        AssertTrue(parsed.Length == 2,
            "Path parser trims quotes and removes duplicates");
        AssertTrue(
            parsed[0] == "C:\\Work Files\\a.txt" &&
            parsed[1] == "D:\\b.csv",
            "Path parser preserves Windows paths");

        Directory.CreateDirectory(dataRoot);
        string file = Path.Combine(dataRoot, "path-input.txt");
        File.WriteAllText(file, "path input content", Encoding.UTF8);
        IList inputItems = (IList)GetField(formType, form, "inputItems");
        int before = inputItems.Count;
        pathInput.Text = "\"" + file + "\"";
        AssertTrue(inputItems.Count == before,
            "Pasting a path does not read it automatically");

        InvokePrivate(
            formType,
            form,
            "OnReadPathClick",
            readButton,
            EventArgs.Empty);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
            (inputItems.Count == before ||
                (bool)GetField(formType, form, "isAddingFiles") ||
                pathInput.Text.Length > 0))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        AssertTrue(inputItems.Count == before + 1,
            "Path read action extracts the selected file");
        AssertTrue(pathInput.Text.Length == 0,
            "Successful path read clears the path input");

        InvokePrivate(formType, form, "SetInputButtonsEnabled", false);
        AssertTrue(!pathInput.Enabled && !readButton.Enabled,
            "Path controls are disabled while busy");
        InvokePrivate(formType, form, "SetInputButtonsEnabled", true);
        AssertTrue(pathInput.Enabled && readButton.Enabled,
            "Path controls recover after file extraction");
    }

    private static void TestWholeConversationExport(
        Type formType,
        object form)
    {
        object store = GetField(formType, form, "conversationStore");
        object session = store.GetType().GetProperty("CurrentSession")
            .GetValue(store, null);
        IList messages = (IList)session.GetType().GetProperty("Messages")
            .GetValue(session, null);
        Type messageType = formType.Assembly.GetType(
            "FilePromptAIWin7.ConversationMessage",
            true);
        const string sourceBody =
            "以下资料由用户主动拖入或粘贴后提取，" +
            "只包含文件名和实际内容，不包含本地路径：\r\n" +
            "===== 内容开始：report.txt =====\r\n" +
            "FULL ATTACHMENT BODY\r\n" +
            "===== 内容结束：report.txt =====";
        messages.Add(Activator.CreateInstance(
            messageType,
            new object[] { "user", sourceBody }));
        messages.Add(Activator.CreateInstance(
            messageType,
            new object[] { "assistant", "summary" }));
        try
        {
            string markdown = (string)InvokePrivate(
                formType,
                form,
                "BuildConversationMarkdown");
            AssertTrue(
                markdown.IndexOf(
                    "FULL ATTACHMENT BODY",
                    StringComparison.Ordinal) >= 0,
                "Whole-conversation export keeps attachment text");
        }
        finally
        {
            messages.Clear();
        }
    }

    private static void TestPathResolutionBoundaries(
        Type formType,
        object form,
        string dataRoot)
    {
        TextBox pathInput = (TextBox)GetField(
            formType,
            form,
            "pathTextBox");
        IList inputItems = (IList)GetField(
            formType,
            form,
            "inputItems");
        string valid = Path.Combine(dataRoot, "mixed-valid.txt");
        string missing = Path.Combine(dataRoot, "missing-file.txt");
        File.WriteAllText(valid, "mixed path content", Encoding.UTF8);

        int before = inputItems.Count;
        pathInput.Text = valid + Environment.NewLine + missing;
        InvokePrivate(
            formType,
            form,
            "OnReadPathClick",
            GetField(formType, form, "readPathButton"),
            EventArgs.Empty);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
            ((bool)GetField(formType, form, "isAddingFiles") ||
                inputItems.Count == before))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        AssertTrue(
            inputItems.Count == before + 1,
            "Mixed paths still add the valid file");
        AssertTrue(
            pathInput.Text.IndexOf(missing, StringComparison.Ordinal) >= 0,
            "Mixed paths keep the failed path for retry");

        InvokePrivate(
            formType,
            form,
            "OnClearClick",
            form,
            EventArgs.Empty);

        Type inputType = formType.Assembly.GetType(
            "FilePromptAIWin7.InputItem",
            true);
        Type kindType = formType.Assembly.GetType(
            "FilePromptAIWin7.InputKind",
            true);
        object first = Activator.CreateInstance(inputType, true);
        inputType.GetProperty("Name").SetValue(first, "duplicate-one", null);
        inputType.GetProperty("Kind").SetValue(
            first,
            Enum.Parse(kindType, "Text"),
            null);
        inputType.GetProperty("TextContent").SetValue(
            first,
            "duplicate content",
            null);
        inputType.GetProperty("SourcePath").SetValue(first, valid, null);
        bool firstAdded = (bool)InvokePrivate(
            formType,
            form,
            "AddInputItem",
            first);

        object second = Activator.CreateInstance(inputType, true);
        inputType.GetProperty("Name").SetValue(second, "duplicate-two", null);
        inputType.GetProperty("Kind").SetValue(
            second,
            Enum.Parse(kindType, "Text"),
            null);
        inputType.GetProperty("TextContent").SetValue(
            second,
            "duplicate content",
            null);
        inputType.GetProperty("SourcePath").SetValue(second, valid, null);
        bool secondAdded = (bool)InvokePrivate(
            formType,
            form,
            "AddInputItem",
            second);
        AssertTrue(firstAdded && !secondAdded,
            "Repeated source paths are skipped across batches");

        MethodInfo resolver = null;
        foreach (MethodInfo candidate in formType.GetMethods(
            BindingFlags.Static | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly))
        {
            ParameterInfo[] parameters = candidate.GetParameters();
            if (candidate.Name == "ResolveExistingFiles" &&
                parameters.Length == 2)
            {
                resolver = candidate;
                break;
            }
        }

        List<string> mostlyMissing = new List<string>();
        mostlyMissing.Add(valid);
        for (int index = 0; index < 129; index++)
        {
            mostlyMissing.Add(Path.Combine(
                dataRoot,
                "not-there-" + index.ToString() + ".txt"));
        }

        object resolution = resolver.Invoke(
            null,
            new object[] { mostlyMissing.ToArray(), CancellationToken.None });
        IList resolvedFiles = (IList)resolution.GetType().GetProperty("Files")
            .GetValue(resolution, null);
        bool tooMany = (bool)resolution.GetType()
            .GetProperty("TooManyFiles")
            .GetValue(resolution, null);
        AssertTrue(resolvedFiles.Count == 1 && !tooMany,
            "Missing paths do not consume the 128-file limit");

        InvokePrivate(
            formType,
            form,
            "OnClearClick",
            form,
            EventArgs.Empty);
    }

    private static void TestSearchCharacterBudget(
        Assembly application,
        Type formType,
        object form)
    {
        Type sessionType = application.GetType(
            "FilePromptAIWin7.ConversationSession",
            true);
        Type messageType = application.GetType(
            "FilePromptAIWin7.ConversationMessage",
            true);
        object session = Activator.CreateInstance(sessionType, true);
        object message = Activator.CreateInstance(messageType, true);
        string content = new string('a', 25000) + "needle";
        messageType.GetProperty("Role").SetValue(message, "assistant", null);
        messageType.GetProperty("Content").SetValue(message, content, null);
        IList messages = (IList)sessionType.GetProperty("Messages")
            .GetValue(session, null);
        messages.Add(message);

        bool hiddenTail = (bool)InvokePrivate(
            formType,
            form,
            "SessionMatchesFilter",
            session,
            "needle");
        bool visibleHead = (bool)InvokePrivate(
            formType,
            form,
            "SessionMatchesFilter",
            session,
            "aaaaa");
        AssertTrue(!hiddenTail, "Search does not scan beyond its budget");
        AssertTrue(visibleHead, "Search still scans recent content");
    }

    private static void TestExtensionsDialog(
        Assembly application,
        Type formType,
        object form)
    {
        Button extensionsButton = GetField(
            formType,
            form,
            "extensionsButton") as Button;
        Application.DoEvents();
        AssertTrue(extensionsButton != null, "Extensions button exists");
        AssertTrue(extensionsButton.Enabled, "Extensions button enabled when idle");

        Type settingsType = application.GetType(
            "FilePromptAIWin7.ExtensionSettings",
            true);
        Type dialogType = application.GetType(
            "FilePromptAIWin7.ExtensionsDialog",
            true);
        object settings = Activator.CreateInstance(settingsType, true);
        Form dialog = Activator.CreateInstance(
            dialogType,
            new object[] { settings }) as Form;
        AssertTrue(dialog != null, "Extensions dialog can be created");
        try
        {
            dialog.CreateControl();
            dialog.PerformLayout();
            AssertTrue(
                dialog.MinimumSize.Width <= 800 &&
                dialog.MinimumSize.Height <= 600,
                "Extensions dialog minimum size");
            TabControl tabs = FindControl<TabControl>(dialog, null);
            AssertTrue(
                tabs != null && tabs.TabPages.Count == 2,
                "Skills and MCP tabs exist");
            SplitContainer split = FindControl<SplitContainer>(dialog, null);
            AssertTrue(
                split != null && split.SplitterDistance >= 250,
                "Extensions navigation width is stable");
            AssertTrue(
                FindControl<Button>(dialog, "从剪贴板安装") != null,
                "Skill clipboard install exists");
            AssertTrue(
                FindControl<Button>(dialog, "粘贴 JSON") != null,
                "MCP JSON import exists");
            AssertTrue(
                FindControl<Button>(dialog, "测试所选") != null,
                "MCP connection test exists");
            CheckBox confirmation = FindControl<CheckBox>(
                dialog,
                "每次调用前确认");
            AssertTrue(
                confirmation != null && confirmation.Checked,
                "MCP confirmation defaults on in UI");
            AssertTrue(
                FindControl<Button>(dialog, "选择技能文件") == null,
                "Skill install does not scan local files");
        }
        finally
        {
            dialog.Dispose();
        }
    }

    private static void TestModelProfilesDialog(
        Assembly application,
        Type formType,
        object form)
    {
        Form settingsDialog = GetField(
            formType,
            form,
            "settingsDialog") as Form;
        AssertTrue(
            settingsDialog != null,
            "Settings dialog exists for model profiles");
        CheckBox settingsShowKey = FindControl<CheckBox>(
            settingsDialog,
            "\u663e\u793a Key");
        TextBox apiKey = settingsDialog.GetType()
            .GetProperty("ApiKeyTextBox")
            .GetValue(settingsDialog, null) as TextBox;
        AssertTrue(settingsShowKey != null && apiKey != null,
            "Settings API key visibility controls exist");
        settingsShowKey.Checked = true;
        AssertTrue(!apiKey.UseSystemPasswordChar,
            "API key can be revealed for the current settings view");
        settingsDialog.GetType().GetMethod(
            "PrepareForOpen",
            new[] { typeof(string), typeof(string) })
            .Invoke(settingsDialog, new object[] { string.Empty, string.Empty });
        AssertTrue(!settingsShowKey.Checked && apiKey.UseSystemPasswordChar,
            "Reopening settings hides the API key again");
        AssertTrue(
            FindControl<Button>(settingsDialog, "测试连接") != null &&
                FindControl<Button>(settingsDialog, "管理技能与 MCP...") != null &&
                FindControl<Button>(settingsDialog, "模型配置...") != null,
            "Settings pages keep model and extension actions available");

        Type profileType = application.GetType(
            "FilePromptAIWin7.ModelProfile",
            true);
        Type listType = typeof(List<>).MakeGenericType(profileType);
        IList profiles = (IList)Activator.CreateInstance(listType);
        object profile = Activator.CreateInstance(profileType, true);
        profileType.GetProperty("Name").SetValue(profile, "内网模型", null);
        profileType.GetProperty("EndpointUrl").SetValue(
            profile,
            "https://127.0.0.1/v1/chat/completions",
            null);
        profileType.GetProperty("ApiKey").SetValue(profile, "test-key", null);
        profileType.GetProperty("ModelName").SetValue(
            profile,
            "test-model",
            null);
        profiles.Add(profile);

        Type dialogType = application.GetType(
            "FilePromptAIWin7.ModelProfilesDialog",
            true);
        Form dialog = Activator.CreateInstance(
            dialogType,
            new object[] { profiles, profile }) as Form;
        AssertTrue(dialog != null, "Model profiles dialog can be created");
        try
        {
            dialog.CreateControl();
            dialog.PerformLayout();
            AssertTrue(
                dialog.ClientSize.Width >= 640 &&
                dialog.ClientSize.Height >= 390,
                "Model profiles dialog size is stable");
            AssertTrue(
                FindControl<ListBox>(dialog, null) != null,
                "Model profiles list exists");
            AssertTrue(
                FindControl<Button>(dialog, "新建") != null &&
                FindControl<Button>(dialog, "删除") != null &&
                FindControl<Button>(dialog, "保存配置") != null &&
                FindControl<Button>(dialog, "保存并应用") != null,
                "Model profile actions exist");
            CheckBox showKey = FindControl<CheckBox>(dialog, "显示 Key");
            AssertTrue(
                showKey != null && !showKey.Checked,
                "Model profile API key is hidden by default");
        }
        finally
        {
            dialog.Dispose();
        }
    }
    private static void TestUninstallerEntry(Type formType, object form)
    {
        Form settingsDialog = GetField(
            formType,
            form,
            "settingsDialog") as Form;
        AssertTrue(
            settingsDialog != null &&
                FindControl<Button>(settingsDialog, "卸载程序...") != null,
            "Settings maintenance page exposes uninstall");
    }

    private static T FindControl<T>(
        Control root,
        string text) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            T match = child as T;
            if (match != null &&
                (text == null || string.Equals(
                    match.Text,
                    text,
                    StringComparison.Ordinal)))
            {
                return match;
            }

            T nested = FindControl<T>(child, text);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool ContainsMenuText(
        ContextMenuStrip menu,
        string text)
    {
        if (menu == null)
        {
            return false;
        }

        foreach (ToolStripItem item in menu.Items)
        {
            if (string.Equals(item.Text, text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void TestMcpApprovalArguments(
        Assembly application,
        Type formType,
        object form)
    {
        Type toolType = application.GetType(
            "FilePromptAIWin7.McpToolDefinition",
            true);
        object tool = Activator.CreateInstance(toolType, true);
        toolType.GetProperty("ServerName").SetValue(
            tool,
            "approval-test",
            null);
        toolType.GetProperty("ToolName").SetValue(
            tool,
            "inspect",
            null);
        string arguments = "{\"value\":\"" +
            new string('x', 6000) + "\"}";
        int displayedCharacters = -1;
        bool noDefaultApproval = false;
        System.Windows.Forms.Timer closeTimer =
            new System.Windows.Forms.Timer();
        closeTimer.Interval = 100;
        closeTimer.Tick += delegate
        {
            foreach (Form open in Application.OpenForms)
            {
                if (!string.Equals(
                    open.Text,
                    "确认 MCP 工具调用",
                    StringComparison.Ordinal))
                {
                    continue;
                }

                TextBox argumentsBox = FindControl<TextBox>(open, null);
                displayedCharacters = argumentsBox == null
                    ? -1
                    : argumentsBox.Text.Length;
                noDefaultApproval = open.AcceptButton == null;
                open.DialogResult = DialogResult.No;
                open.Close();
                closeTimer.Stop();
                break;
            }
        };

        try
        {
            closeTimer.Start();
            bool approved = (bool)InvokePrivate(
                formType,
                form,
                "ShowMcpApproval",
                tool,
                arguments);
            AssertTrue(!approved, "MCP approval can be rejected");
            AssertTrue(
                displayedCharacters == arguments.Length,
                "MCP approval shows complete arguments");
            AssertTrue(
                noDefaultApproval,
                "MCP approval has no Enter-to-approve default");
        }
        finally
        {
            closeTimer.Stop();
            closeTimer.Dispose();
        }
    }

    private static void TestStdioStartupApproval(
        Assembly application,
        Type formType,
        object form)
    {
        Type serverType = application.GetType(
            "FilePromptAIWin7.McpServerDefinition",
            true);
        Type serverListType = typeof(List<>).MakeGenericType(serverType);
        IList servers = (IList)Activator.CreateInstance(serverListType);
        string longArgument = "--payload=" + new string('z', 6000);
        const string multilineArgument =
            "line-one\r\n服务名称：spoofed-service";
        const string environmentName = "PRIVATE_TOKEN_NAME";
        const string environmentValue = "secret-must-not-be-displayed";

        object stdio = Activator.CreateInstance(serverType, true);
        serverType.GetProperty("Name").SetValue(
            stdio,
            "local-review-server",
            null);
        serverType.GetProperty("Transport").SetValue(
            stdio,
            "stdio",
            null);
        serverType.GetProperty("Command").SetValue(
            stdio,
            "C:\\Tools\\review-mcp.exe",
            null);
        serverType.GetProperty("WorkingDirectory").SetValue(
            stdio,
            "C:\\Internal Work",
            null);
        serverType.GetProperty("Arguments").SetValue(
            stdio,
            new List<string>
            {
                "--stdio",
                longArgument,
                multilineArgument
            },
            null);
        serverType.GetProperty("Environment").SetValue(
            stdio,
            new Dictionary<string, string>
            {
                { environmentName, environmentValue }
            },
            null);
        serverType.GetProperty("Enabled").SetValue(stdio, true, null);
        servers.Add(stdio);

        object http = Activator.CreateInstance(serverType, true);
        serverType.GetProperty("Name").SetValue(
            http,
            "http-must-not-be-shown",
            null);
        serverType.GetProperty("Transport").SetValue(http, "http", null);
        serverType.GetProperty("Enabled").SetValue(http, true, null);
        servers.Add(http);

        string displayed = string.Empty;
        bool enterDefaultsToReject = false;
        bool rejectHasInitialFocus = false;
        System.Windows.Forms.Timer closeTimer =
            new System.Windows.Forms.Timer();
        closeTimer.Interval = 100;
        closeTimer.Tick += delegate
        {
            foreach (Form open in Application.OpenForms)
            {
                if (!string.Equals(
                    open.Text,
                    "确认启动本地 MCP 服务",
                    StringComparison.Ordinal))
                {
                    continue;
                }

                TextBox detailsBox = FindControl<TextBox>(open, null);
                displayed = detailsBox == null
                    ? string.Empty
                    : detailsBox.Text;
                Button reject = FindControl<Button>(open, "拒绝");
                enterDefaultsToReject = reject != null &&
                    ReferenceEquals(open.AcceptButton, reject);
                rejectHasInitialFocus = reject != null && reject.Focused;
                open.DialogResult = DialogResult.No;
                open.Close();
                closeTimer.Stop();
                break;
            }
        };

        try
        {
            closeTimer.Start();
            bool approved = (bool)InvokePrivate(
                formType,
                form,
                "ShowStdioMcpStartupApproval",
                servers);
            AssertTrue(!approved, "stdio startup can be rejected");
            AssertTrue(
                displayed.IndexOf(
                    "local-review-server",
                    StringComparison.Ordinal) >= 0 &&
                displayed.IndexOf(
                    "\"C:\\\\Tools\\\\review-mcp.exe\"",
                    StringComparison.Ordinal) >= 0 &&
                displayed.IndexOf(
                    "\"C:\\\\Internal Work\"",
                    StringComparison.Ordinal) >= 0,
                "stdio startup shows identity and paths");
            AssertTrue(
                displayed.IndexOf(longArgument, StringComparison.Ordinal) >= 0,
                "stdio startup shows complete arguments");
            AssertTrue(
                displayed.IndexOf(
                    "line-one\\r\\n服务名称：spoofed-service",
                    StringComparison.Ordinal) >= 0 &&
                displayed.IndexOf(
                    multilineArgument,
                    StringComparison.Ordinal) < 0,
                "stdio startup escapes argument line injection");
            AssertTrue(
                displayed.IndexOf(
                    environmentName,
                    StringComparison.Ordinal) >= 0 &&
                displayed.IndexOf(
                    environmentValue,
                    StringComparison.Ordinal) < 0,
                "stdio startup hides environment values");
            AssertTrue(
                displayed.IndexOf(
                    "http-must-not-be-shown",
                    StringComparison.Ordinal) < 0,
                "HTTP MCP skips startup approval");
            AssertTrue(
                enterDefaultsToReject,
                "stdio startup Enter defaults to reject");
            AssertTrue(
                rejectHasInitialFocus,
                "stdio startup focuses reject");
        }
        finally
        {
            closeTimer.Stop();
            closeTimer.Dispose();
        }
    }

    private static void TestRejectedStdioStartupCancelsGeneration(
        Assembly application,
        Type formType,
        object form)
    {
        Type settingsType = application.GetType(
            "FilePromptAIWin7.ExtensionSettings",
            true);
        Type serverType = application.GetType(
            "FilePromptAIWin7.McpServerDefinition",
            true);
        object settings = Activator.CreateInstance(settingsType, true);
        object server = Activator.CreateInstance(serverType, true);
        serverType.GetProperty("Name").SetValue(
            server,
            "must-not-start",
            null);
        serverType.GetProperty("Transport").SetValue(
            server,
            "stdio",
            null);
        serverType.GetProperty("Command").SetValue(
            server,
            "must-not-start.exe",
            null);
        serverType.GetProperty("Enabled").SetValue(server, true, null);
        IList configuredServers = (IList)settingsType
            .GetProperty("McpServers")
            .GetValue(settings, null);
        configuredServers.Add(server);
        SetField(formType, form, "extensionSettings", settings);

        ((TextBox)GetField(formType, form, "endpointTextBox")).Text =
            "http://127.0.0.1:1/v1/chat/completions";
        ((TextBox)GetField(formType, form, "apiKeyTextBox")).Text =
            "ui-test-key";
        ((TextBox)GetField(formType, form, "modelTextBox")).Text =
            "ui-test-model";
        ((RichTextBox)GetField(formType, form, "promptTextBox")).Text =
            "拒绝本地 MCP 启动测试";

        bool startupDialogSeen = false;
        System.Windows.Forms.Timer rejectTimer =
            new System.Windows.Forms.Timer();
        rejectTimer.Interval = 100;
        rejectTimer.Tick += delegate
        {
            foreach (Form open in Application.OpenForms)
            {
                if (!string.Equals(
                    open.Text,
                    "确认启动本地 MCP 服务",
                    StringComparison.Ordinal))
                {
                    continue;
                }

                startupDialogSeen = true;
                open.DialogResult = DialogResult.No;
                open.Close();
                rejectTimer.Stop();
                break;
            }
        };

        try
        {
            rejectTimer.Start();
            InvokePrivate(formType, form, "StartGeneration");
            ToolStripStatusLabel status = GetField(
                formType,
                form,
                "statusLabel") as ToolStripStatusLabel;
            AssertTrue(
                startupDialogSeen,
                "generation shows stdio startup approval");
            AssertTrue(
                GetField(formType, form, "generationCancellation") == null,
                "rejected stdio startup clears generation state");
            AssertTrue(
                status != null && string.Equals(
                    status.Text,
                    "已拒绝启动本地 MCP，本次生成已取消。",
                    StringComparison.Ordinal),
                "rejected stdio startup cancels generation");
        }
        finally
        {
            rejectTimer.Stop();
            rejectTimer.Dispose();
        }
    }

    private static bool ContainsControlText(Control root, string text)
    {
        if (root == null)
        {
            return false;
        }

        if (string.Equals(root.Text, text, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (Control child in root.Controls)
        {
            if (ContainsControlText(child, text))
            {
                return true;
            }
        }

        return false;
    }

    private static object GetField(
        Type type,
        object instance,
        string name)
    {
        return type.GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(instance);
    }

    private static object GetProperty(object instance, string name)
    {
        return instance.GetType().GetProperty(name).GetValue(instance, null);
    }

    private static void SetProperty(
        object instance,
        string name,
        object value)
    {
        instance.GetType().GetProperty(name).SetValue(instance, value, null);
    }

    private static object InvokePublic(
        object instance,
        string name,
        params object[] arguments)
    {
        return instance.GetType().InvokeMember(
            name,
            BindingFlags.InvokeMethod | BindingFlags.Instance |
                BindingFlags.Public,
            null,
            instance,
            arguments);
    }

    private static string GetSessionId(object session)
    {
        return session == null
            ? string.Empty
            : (string)GetProperty(session, "Id");
    }

    private static int FindSessionIndex(
        ListBox.ObjectCollection sessions,
        string id)
    {
        for (int index = 0; index < sessions.Count; index++)
        {
            if (string.Equals(
                GetSessionId(sessions[index]),
                id,
                StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static void RaiseMenuOpening(ContextMenuStrip menu)
    {
        typeof(ToolStripDropDown).GetMethod(
            "OnOpening",
            BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(
                menu,
                new object[] { new CancelEventArgs() });
    }

    private static IList CreateHistory(
        Assembly application,
        int charactersPerMessage)
    {
        Type messageType = application.GetType(
            "FilePromptAIWin7.ConversationMessage",
            true);
        Type listType = typeof(List<>).MakeGenericType(messageType);
        IList messages = (IList)Activator.CreateInstance(listType);
        object user = Activator.CreateInstance(messageType, true);
        messageType.GetProperty("Role").SetValue(user, "user", null);
        messageType.GetProperty("Content").SetValue(
            user,
            new string('u', charactersPerMessage),
            null);
        object assistant = Activator.CreateInstance(messageType, true);
        messageType.GetProperty("Role").SetValue(
            assistant,
            "assistant",
            null);
        messageType.GetProperty("Content").SetValue(
            assistant,
            new string('a', charactersPerMessage),
            null);
        messages.Add(user);
        messages.Add(assistant);
        return messages;
    }

    private static string RepeatText(string value, int count)
    {
        StringBuilder result = new StringBuilder(value.Length * count);
        for (int index = 0; index < count; index++)
        {
            result.Append(value);
        }

        return result.ToString();
    }

    private static long CountUnicodeCharacters(string value)
    {
        long count = 0L;
        if (string.IsNullOrEmpty(value))
        {
            return count;
        }

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

    private static bool HasUnpairedSurrogate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length ||
                    !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static void SetField(
        Type type,
        object instance,
        string name,
        object value)
    {
        type.GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(instance, value);
    }

    private static object InvokePrivate(
        Type type,
        object instance,
        string name,
        params object[] arguments)
    {
        return type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Invoke(instance, arguments);
    }

    private static void RaiseKeyDown(Control control, KeyEventArgs arguments)
    {
        typeof(Control).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(control, new object[] { arguments });
    }

    private static void RaiseMouseDown(
        Control control,
        MouseEventArgs arguments)
    {
        typeof(Control).GetMethod(
            "OnMouseDown",
            BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(control, new object[] { arguments });
    }

    private static void ConfigureAssemblyResolution(string applicationPath)
    {
        string directory = Path.GetDirectoryName(applicationPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(
            object sender,
            ResolveEventArgs args)
        {
            string candidate = Path.Combine(
                directory,
                new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(candidate)
                ? Assembly.LoadFrom(candidate)
                : null;
        };
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while (current is TargetInvocationException &&
            current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private static void ThrowIfUiThreadException()
    {
        if (uiThreadException == null)
        {
            return;
        }

        Exception captured = uiThreadException;
        uiThreadException = null;
        throw new InvalidOperationException(
            "Unhandled exception on the WinForms UI thread.",
            captured);
    }

    private static NativePoint GetScrollPosition(RichTextBox textBox)
    {
        NativePoint position = new NativePoint();
        SendMessage(
            textBox.Handle,
            EmGetScrollPosition,
            IntPtr.Zero,
            ref position);
        return position;
    }

    private static void SetScrollPosition(RichTextBox textBox, int y)
    {
        NativePoint position = new NativePoint();
        position.Y = y;
        SendMessage(
            textBox.Handle,
            EmSetScrollPosition,
            IntPtr.Zero,
            ref position);
        Application.DoEvents();
    }

    private static void AssertScrollPreserved(
        NativePoint before,
        NativePoint after,
        string name)
    {
        AssertTrue(
            Math.Abs(before.X - after.X) <= 2 &&
                Math.Abs(before.Y - after.Y) <= 2,
            name + " (before=" + before.X + "," + before.Y +
                "; after=" + after.X + "," + after.Y + ")");
    }

    private static int CountOccurrences(string value, string needle)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        int count = 0;
        int offset = 0;
        while (offset <= value.Length - needle.Length)
        {
            int index = value.IndexOf(needle, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            count++;
            offset = index + needle.Length;
        }

        return count;
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " failed.");
        }

        Console.WriteLine("PASS | " + name);
    }
}
