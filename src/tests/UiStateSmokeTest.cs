using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class UiStateSmokeTest
{
    private const int EmGetScrollPosition = 0x04DD;
    private const int EmSetScrollPosition = 0x04DE;
    private const int WmClose = 0x0010;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(
        string className,
        string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter);

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

            TestAnonymousConnectionSettings(formType, form);
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
            TestFailedFilePathRecovery(formType, form, dataRoot);
            ThrowIfUiThreadException();
            TestPathResolutionBoundaries(formType, form, dataRoot);
            ThrowIfUiThreadException();
            TestWholeConversationExport(formType, form);
            TestSearchCharacterBudget(application, formType, form);
            TestExtensionsDialog(application, formType, form);
            TestSettingsDialogLayout(application);
            TestModelProfilesDialog(application, formType, form);
            TestRegenerationAndRetryState(application, formType, form);
            TestGenerationRetryWorkflows(application, formType, form);
            TestRetryInvalidation(application, formType, form, dataRoot);
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

    private static void TestAnonymousConnectionSettings(
        Type formType,
        object form)
    {
        TextBox endpoint = (TextBox)GetField(
            formType,
            form,
            "endpointTextBox");
        TextBox apiKey = (TextBox)GetField(
            formType,
            form,
            "apiKeyTextBox");
        ComboBox model = (ComboBox)GetField(
            formType,
            form,
            "modelTextBox");
        string previousEndpoint = endpoint.Text;
        string previousApiKey = apiKey.Text;
        string previousModel = model.Text;
        try
        {
            endpoint.Text = "http://127.0.0.1:11434/v1/chat/completions";
            apiKey.Text = string.Empty;
            model.Text = "local-model";
            AssertTrue(
                (bool)InvokePrivate(
                    formType,
                    form,
                    "HasCompleteConnectionSettings"),
                "Anonymous URL and model are complete connection settings");
            AssertTrue(
                (bool)InvokePrivate(
                    formType,
                    form,
                    "HasModelListConnectionSettings"),
                "Anonymous URL is sufficient for model discovery");

            model.Text = string.Empty;
            AssertTrue(
                !(bool)InvokePrivate(
                    formType,
                    form,
                    "HasCompleteConnectionSettings") &&
                (bool)InvokePrivate(
                    formType,
                    form,
                    "HasModelListConnectionSettings"),
                "Model is required only for complete connection settings");

            endpoint.Text = string.Empty;
            model.Text = "local-model";
            AssertTrue(
                !(bool)InvokePrivate(
                    formType,
                    form,
                    "HasCompleteConnectionSettings") &&
                !(bool)InvokePrivate(
                    formType,
                    form,
                    "HasModelListConnectionSettings"),
                "URL remains required for connection and model discovery");
        }
        finally
        {
            endpoint.Text = previousEndpoint;
            apiKey.Text = previousApiKey;
            model.Text = previousModel;
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
        ListBox sessionList = GetField(
            formType,
            form,
            "sessionListBox") as ListBox;

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
                    "资料",
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
        Control sidebar = ((TableLayoutPanel)GetField(
            formType,
            form,
            "rootLayout")).GetControlFromPosition(0, 0);
        Button addFile = GetField(
            formType,
            form,
            "addFileButton") as Button;
        AssertTrue(
            sidebar != null && sessionList.Parent != null &&
                sidebar.Contains(sessionList) &&
                sidebar.Contains(settingsButton) &&
                !sidebar.Contains(addFile) &&
                !sidebar.Contains(dropTarget) &&
                !sidebar.Contains(pathInput),
            "Sidebar contains conversation navigation, not file inputs");
        AssertTrue(
            addFile != null && composer.Contains(addFile) &&
                composer.Contains(dropTarget) &&
                composer.Contains(inputList) &&
                !workspace.GetControlFromPosition(0, 0).Contains(addFile) &&
                !workspace.GetControlFromPosition(0, 0).Contains(dropTarget),
            "File actions stay in the bottom composer and out of the header");
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
        Size compactSummarySize = TextRenderer.MeasureText(
            contextSummary.Text,
            contextSummary.Font,
            Size.Empty,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        AssertTrue(
            contextSummary.Visible &&
                compactSummarySize.Width <= contextSummary.ClientSize.Width &&
                contextSummary.Height < contextSummary.Font.Height * 2,
            "Narrow workspace keeps the context summary on one line (text=" +
                compactSummarySize.Width + ", client=" +
                contextSummary.ClientSize.Width + ", height=" +
                contextSummary.Height + ")");
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
            ContextMenuStrip exportMenu = GetField(
                formType,
                form,
                "exportMenu") as ContextMenuStrip;
            AssertTrue(
                ContainsMenuText(exportMenu, "整个会话 · Markdown") &&
                    ContainsMenuText(exportMenu, "整个会话 · 文本"),
                "Export menu exposes whole-conversation Markdown and text");
            string markdown = (string)InvokePrivate(
                formType,
                form,
                "BuildConversationMarkdown");
            string plainText = (string)InvokePrivate(
                formType,
                form,
                "BuildConversationPlainText");
            AssertTrue(
                markdown.IndexOf(
                    "FULL ATTACHMENT BODY",
                    StringComparison.Ordinal) >= 0,
                "Whole-conversation Markdown keeps attachment text");
            AssertTrue(
                plainText.IndexOf(
                    "FULL ATTACHMENT BODY",
                    StringComparison.Ordinal) >= 0 &&
                    plainText.IndexOf("# ", StringComparison.Ordinal) < 0,
                "Whole-conversation text keeps content without Markdown headings");
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

    private static void TestFailedFilePathRecovery(
        Type formType,
        object form,
        string dataRoot)
    {
        TextBox pathInput = (TextBox)GetField(
            formType,
            form,
            "pathTextBox");
        ToolStripStatusLabel status = (ToolStripStatusLabel)GetField(
            formType,
            form,
            "statusLabel");
        string existing = Path.Combine(dataRoot, "existing-retry.txt");
        string missing = Path.Combine(dataRoot, "dropped-then-missing.txt");
        pathInput.Text = existing;

        Task addTask = (Task)InvokePrivate(
            formType,
            form,
            "AddFilesAsync",
            (object)new string[] { missing, missing });
        PumpTask(addTask, 10000, "Missing dropped file recovery completes");

        string[] retryPaths = (string[])formType.GetMethod(
            "ParsePastedPaths",
            BindingFlags.Static | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Invoke(null, new object[] { pathInput.Text });
        AssertTrue(
            retryPaths.Length == 2 &&
                string.Equals(
                    retryPaths[0],
                    existing,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    retryPaths[1],
                    missing,
                    StringComparison.OrdinalIgnoreCase),
            "Failed picker or drop paths merge into the retry editor");
        AssertTrue(
            status.Text.IndexOf("+ 添加", StringComparison.Ordinal) >= 0 &&
                status.Text.IndexOf("从路径添加", StringComparison.Ordinal) >= 0,
            "Failed file status gives a concrete retry route");
        pathInput.Clear();
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
            Button skillImport = FindControlByAccessibleName<Button>(
                dialog,
                "导入离线技能");
            Button mcpImport = FindControlByAccessibleName<Button>(
                dialog,
                "导入 MCP 配置");
            AssertTrue(
                skillImport != null &&
                    ContainsMenuText(
                        skillImport.ContextMenuStrip,
                        "从剪贴板安装") &&
                    ContainsMenuText(
                        skillImport.ContextMenuStrip,
                        "从文件安装..."),
                "Skill import menu exposes clipboard and local file");
            AssertTrue(
                mcpImport != null &&
                    ContainsMenuText(
                        mcpImport.ContextMenuStrip,
                        "粘贴 JSON") &&
                    ContainsMenuText(
                        mcpImport.ContextMenuStrip,
                        "导入 JSON 文件..."),
                "MCP import menu exposes clipboard and local JSON file");
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
                FindControl<Button>(dialog, "选择技能文件") == null &&
                    FindControl<Button>(dialog, "选择技能目录") == null,
                "Skill import does not expose directory scanning");

            Type skillType = application.GetType(
                "FilePromptAIWin7.SkillDefinition",
                true);
            Type serverType = application.GetType(
                "FilePromptAIWin7.McpServerDefinition",
                true);
            object working = GetField(dialogType, dialog, "working");
            IList workingSkills = (IList)GetProperty(working, "Skills");
            IList workingServers = (IList)GetProperty(
                working,
                "McpServers");
            while (workingSkills.Count < 50)
            {
                workingSkills.Add(Activator.CreateInstance(skillType, true));
            }

            object overflowSkill = Activator.CreateInstance(skillType, true);
            Exception skillOverflow = CaptureInvocationFailure(delegate
            {
                InvokePrivate(
                    dialogType,
                    dialog,
                    "AddImportedSkill",
                    overflowSkill);
            });
            AssertTrue(
                skillOverflow is InvalidOperationException &&
                    workingSkills.Count == 50,
                "Skill import validates capacity without partial mutation");

            while (workingServers.Count < 19)
            {
                object server = Activator.CreateInstance(serverType, true);
                SetProperty(server, "Command", "tool.exe");
                workingServers.Add(server);
            }

            Type serverListType = typeof(List<>).MakeGenericType(serverType);
            IList importedServers = (IList)Activator.CreateInstance(
                serverListType);
            for (int index = 0; index < 2; index++)
            {
                object server = Activator.CreateInstance(serverType, true);
                SetProperty(server, "Command", "tool.exe");
                importedServers.Add(server);
            }

            Exception serverOverflow = CaptureInvocationFailure(delegate
            {
                InvokePrivate(
                    dialogType,
                    dialog,
                    "AddImportedMcpServers",
                    importedServers);
            });
            AssertTrue(
                serverOverflow is InvalidOperationException &&
                    workingServers.Count == 19,
                "MCP import validates capacity without partial mutation");
        }
        finally
        {
            dialog.Dispose();
        }


        Form readOnlyDialog = Activator.CreateInstance(
            dialogType,
            new object[] { settings, "扩展配置文件被占用" }) as Form;
        AssertTrue(readOnlyDialog != null, "Read-only extensions dialog can be created");
        try
        {
            readOnlyDialog.CreateControl();
            readOnlyDialog.PerformLayout();
            Button save = FindControl<Button>(readOnlyDialog, "保存");
            Button cancel = FindControl<Button>(readOnlyDialog, "取消");
            Button skillImport = FindControlByAccessibleName<Button>(
                readOnlyDialog,
                "导入离线技能");
            Button mcpImport = FindControlByAccessibleName<Button>(
                readOnlyDialog,
                "导入 MCP 配置");
            TextBox skillName = FindControl<TextBox>(readOnlyDialog, null);
            Label warning = FindControl<Label>(
                readOnlyDialog,
                "只读保护：扩展配置无法安全保存。请关闭程序并处理文件占用或权限后重新打开。");
            AssertTrue(
                save != null && !save.Enabled &&
                cancel != null && cancel.Enabled &&
                skillImport != null && !skillImport.Enabled &&
                mcpImport != null && !mcpImport.Enabled &&
                skillName != null && !skillName.Enabled,
                "Read-only extensions UI disables every modification entry");
            AssertTrue(
                warning != null &&
                warning.AccessibleDescription == "扩展配置文件被占用",
                "Read-only extensions UI explains why saving is unavailable");
        }
        finally
        {
            readOnlyDialog.Dispose();
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
        profileType.GetProperty("SystemPrompt").SetValue(
            profile,
            "Answer concisely.",
            null);
        profileType.GetProperty("Temperature").SetValue(profile, 0.6d, null);
        profileType.GetProperty("TopP").SetValue(profile, 0.9d, null);
        profileType.GetProperty("MaxOutputTokens").SetValue(
            profile,
            2048,
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
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(120, 100);
            dialog.Show();
            Application.DoEvents();
            dialog.PerformLayout();
            Application.DoEvents();
            AssertTrue(
                dialog.ClientSize == new Size(820, 610),
                "Model profiles dialog keeps its 820x610 client area");
            AssertTrue(
                dialog.AutoScaleMode == AutoScaleMode.None &&
                    FitsCenteredFullHd(dialog),
                "Model profiles dialog fits 1920x1080 at 96 DPI without scaling");
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
            TextBox systemPrompt = GetField(
                dialogType,
                dialog,
                "systemPromptBox") as TextBox;
            NumericUpDown temperature = GetField(
                dialogType,
                dialog,
                "temperatureBox") as NumericUpDown;
            NumericUpDown topP = GetField(
                dialogType,
                dialog,
                "topPBox") as NumericUpDown;
            NumericUpDown maxOutputTokens = GetField(
                dialogType,
                dialog,
                "maxOutputTokensBox") as NumericUpDown;
            AssertTrue(
                systemPrompt != null && systemPrompt.Multiline &&
                    systemPrompt.Visible && systemPrompt.Height > 0 &&
                    temperature != null && temperature.Visible &&
                    topP != null && topP.Visible &&
                    maxOutputTokens != null && maxOutputTokens.Visible,
                "Model profile generation controls are visible");
            string boundsError = FindVisibleControlBoundsError(dialog);
            AssertTrue(
                string.IsNullOrEmpty(boundsError),
                "Model profile controls remain inside the client area" +
                    (string.IsNullOrEmpty(boundsError)
                        ? string.Empty
                        : ": " + boundsError));
            string overlapError = FindVisibleButtonOverlapError(dialog);
            AssertTrue(
                string.IsNullOrEmpty(overlapError),
                "Model profile buttons do not overlap" +
                    (string.IsNullOrEmpty(overlapError)
                        ? string.Empty
                        : ": " + overlapError));
        }
        finally
        {
            dialog.Hide();
            dialog.Dispose();
        }
    }

    private static void TestSettingsDialogLayout(Assembly application)
    {
        Type dialogType = application.GetType(
            "FilePromptAIWin7.SettingsDialog",
            true);
        Form dialog = Activator.CreateInstance(dialogType, true) as Form;
        AssertTrue(dialog != null, "Settings dialog can be created");
        try
        {
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(80, 80);
            dialog.Show();
            Application.DoEvents();
            dialog.PerformLayout();
            Application.DoEvents();

            AssertTrue(
                dialog.ClientSize == new Size(780, 600),
                "Settings dialog keeps its 780x600 client area");
            AssertTrue(
                dialog.AutoScaleMode == AutoScaleMode.None &&
                    FitsCenteredFullHd(dialog),
                "Settings dialog fits 1920x1080 at 96 DPI without scaling");

            Button[] navigationButtons = GetField(
                dialogType,
                dialog,
                "navigationButtons") as Button[];
            Panel[] pages = GetField(
                dialogType,
                dialog,
                "pages") as Panel[];
            string[] expectedTitles = new string[]
            {
                "模型连接",
                "生成参数",
                "技能与 MCP",
                "会话与输入",
                "维护"
            };
            AssertTrue(
                navigationButtons != null &&
                    navigationButtons.Length == expectedTitles.Length &&
                    pages != null && pages.Length == expectedTitles.Length,
                "Settings dialog exposes five navigation pages");

            for (int index = 0; index < expectedTitles.Length; index++)
            {
                AssertTrue(
                    string.Equals(
                        navigationButtons[index].Text,
                        expectedTitles[index],
                        StringComparison.Ordinal),
                    "Settings navigation order " + expectedTitles[index]);
                navigationButtons[index].PerformClick();
                dialog.PerformLayout();
                Application.DoEvents();

                int visiblePageCount = 0;
                for (int pageIndex = 0;
                    pageIndex < pages.Length;
                    pageIndex++)
                {
                    if (pages[pageIndex].Visible)
                    {
                        visiblePageCount++;
                    }
                }

                AssertTrue(
                    visiblePageCount == 1 && pages[index].Visible &&
                        pages[index].Width > 0 && pages[index].Height > 0,
                    "Settings page switch " + expectedTitles[index]);
                string boundsError = FindVisibleControlBoundsError(dialog);
                AssertTrue(
                    string.IsNullOrEmpty(boundsError),
                    "Settings controls remain inside the client area on " +
                        expectedTitles[index] +
                        (string.IsNullOrEmpty(boundsError)
                            ? string.Empty
                            : ": " + boundsError));
                string overlapError = FindVisibleButtonOverlapError(dialog);
                AssertTrue(
                    string.IsNullOrEmpty(overlapError),
                    "Settings buttons do not overlap on " +
                        expectedTitles[index] +
                        (string.IsNullOrEmpty(overlapError)
                            ? string.Empty
                            : ": " + overlapError));
            }

            Control[] focusTargets = new Control[]
            {
                GetProperty(dialog, "EndpointTextBox") as Control,
                GetProperty(dialog, "SystemPromptTextBox") as Control,
                GetProperty(dialog, "ExtensionsButton") as Control,
                GetProperty(dialog, "SendShortcutComboBox") as Control,
                GetProperty(dialog, "BackupSessionsButton") as Control
            };
            MethodInfo prepareForOpen = dialogType.GetMethod(
                "PrepareForOpen",
                new Type[] { typeof(Control), typeof(string) });
            AssertTrue(
                prepareForOpen != null,
                "Settings dialog exposes control-based page routing");
            for (int index = 0; index < focusTargets.Length; index++)
            {
                AssertTrue(
                    focusTargets[index] != null,
                    "Settings focus target exists for " +
                        expectedTitles[index]);
                prepareForOpen.Invoke(
                    dialog,
                    new object[] { focusTargets[index], string.Empty });
                Application.DoEvents();
                AssertTrue(
                    pages[index].Visible &&
                        CountVisibleControls(pages) == 1,
                    "Settings focus routing selects " +
                        expectedTitles[index]);
            }


            dialogType.GetMethod(
                "SetSettingsWriteProtection",
                BindingFlags.Instance | BindingFlags.Public).Invoke(
                    dialog,
                    new object[] { true, "settings.xml 被另一进程占用" });
            Button saveButton = GetProperty(dialog, "SaveButton") as Button;
            TextBox endpoint = GetProperty(
                dialog,
                "EndpointTextBox") as TextBox;
            ComboBox shortcut = GetProperty(
                dialog,
                "SendShortcutComboBox") as ComboBox;
            Label validation = GetField(
                dialogType,
                dialog,
                "validationLabel") as Label;
            AssertTrue(
                saveButton != null && !saveButton.Enabled &&
                endpoint != null && !endpoint.Enabled &&
                shortcut != null && !shortcut.Enabled,
                "Settings read-only state disables save and editing controls");
            AssertTrue(
                validation != null &&
                validation.Text.IndexOf(
                    "只读保护",
                    StringComparison.Ordinal) >= 0 &&
                validation.Text.IndexOf(
                    "不能修改或保存",
                    StringComparison.Ordinal) >= 0 &&
                validation.AccessibleDescription ==
                    "settings.xml 被另一进程占用",
                "Settings read-only state clearly explains save protection");
        }
        finally
        {
            dialog.Hide();
            dialog.Dispose();
        }
    }

    private static void TestRegenerationAndRetryState(
        Assembly application,
        Type formType,
        object form)
    {
        Button quickModel = GetField(
            formType,
            form,
            "quickModelButton") as Button;
        Button regenerate = GetField(
            formType,
            form,
            "regenerateButton") as Button;
        Button retry = GetField(
            formType,
            form,
            "retryButton") as Button;
        AssertTrue(
            quickModel != null && quickModel.Width == 104 &&
                quickModel.Height == 27 && quickModel.AutoEllipsis &&
                !quickModel.AutoSize &&
                quickModel.AccessibleName == "快速切换已保存模型" &&
                quickModel.ContextMenuStrip != null,
            "Quick model control has a stable compact width");
        AssertTrue(
            regenerate != null && regenerate.Width == 86 &&
                regenerate.Height == 27 && !regenerate.AutoSize &&
                regenerate.AccessibleName == "原位重新生成最新回复",
            "Regenerate control has a stable compact width");
        AssertTrue(
            retry != null && retry.Width == 64 && retry.Height == 27 &&
                !retry.AutoSize &&
                retry.AccessibleName == "重试上一次失败的模型请求",
            "Retry control has a stable compact width");

        object store = GetField(formType, form, "conversationStore");
        Type storeType = store.GetType();
        string originalCurrentId = Convert.ToString(
            storeType.GetProperty("CurrentSessionId").GetValue(store, null));
        object originalProfiles = GetField(
            formType,
            form,
            "modelProfiles");
        Control endpoint = GetField(
            formType,
            form,
            "endpointTextBox") as Control;
        Control apiKey = GetField(
            formType,
            form,
            "apiKeyTextBox") as Control;
        Control model = GetField(
            formType,
            form,
            "modelTextBox") as Control;
        string originalEndpoint = endpoint.Text;
        string originalApiKey = apiKey.Text;
        string originalModel = model.Text;
        object originalGeneration = GetField(
            formType,
            form,
            "generationCancellation");
        object originalConnection = GetField(
            formType,
            form,
            "connectionTestCancellation");
        bool originalAddingFiles = (bool)GetField(
            formType,
            form,
            "isAddingFiles");
        bool originalWriteBlocked = (bool)storeType.GetField(
            "writeBlockedByRecovery",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
        bool originalRetryAvailable = (bool)GetField(
            formType,
            form,
            "retryAvailable");
        bool originalRetryRegeneration = (bool)GetField(
            formType,
            form,
            "retryRegeneration");
        string originalRetrySessionId = (string)GetField(
            formType,
            form,
            "retrySessionId");
        string originalRetryPrompt = (string)GetField(
            formType,
            form,
            "retryPromptText");
        Form window = form as Form;
        Size originalWindowSize = window.Size;
        object temporarySession = null;
        CancellationTokenSource busyCancellation = null;
        try
        {
            AssertTrue(
                originalGeneration == null && originalConnection == null &&
                    !originalAddingFiles && !originalWriteBlocked,
                "Regeneration state test starts while the workspace is idle");
            temporarySession = InvokePublic(
                store,
                "CreateSession",
                "regeneration state " + Guid.NewGuid().ToString("N"));
            IList messages = (IList)GetProperty(
                temporarySession,
                "Messages");
            Type messageType = application.GetType(
                "FilePromptAIWin7.ConversationMessage",
                true);

            object user = CreateConversationMessage(
                messageType,
                "user",
                "latest question");
            object assistant = CreateConversationMessage(
                messageType,
                "assistant",
                "latest answer");
            SetProperty(
                assistant,
                "ParentMessageId",
                (string)GetProperty(user, "Id"));

            AssertLatestRegenerationTurn(
                formType,
                form,
                false,
                null,
                null,
                -1,
                "Fewer than two messages cannot regenerate");
            messages.Add(user);
            AssertLatestRegenerationTurn(
                formType,
                form,
                false,
                null,
                null,
                -1,
                "A single user message cannot regenerate");
            messages.Add(CreateConversationMessage(
                messageType,
                "tool",
                "tool result"));
            AssertLatestRegenerationTurn(
                formType,
                form,
                false,
                null,
                null,
                -1,
                "A non-assistant final message cannot regenerate");
            messages.Clear();
            messages.Add(CreateConversationMessage(
                messageType,
                "assistant",
                "wrong predecessor"));
            messages.Add(assistant);
            AssertLatestRegenerationTurn(
                formType,
                form,
                false,
                null,
                null,
                -1,
                "A non-user predecessor cannot regenerate");
            messages.Clear();
            SetProperty(user, "Id", string.Empty);
            messages.Add(user);
            messages.Add(assistant);
            AssertLatestRegenerationTurn(
                formType,
                form,
                false,
                null,
                null,
                -1,
                "A user message without an identifier cannot regenerate");
            SetProperty(user, "Id", "latest-user-" +
                Guid.NewGuid().ToString("N"));
            SetProperty(
                assistant,
                "ParentMessageId",
                (string)GetProperty(user, "Id"));
            AssertLatestRegenerationTurn(
                formType,
                form,
                true,
                user,
                assistant,
                0,
                "The final user and assistant pair is identified");
            object trailingTool = CreateConversationMessage(
                messageType,
                "tool",
                "trailing tool");
            messages.Add(trailingTool);
            AssertLatestRegenerationTurn(
                formType,
                form,
                false,
                null,
                null,
                -1,
                "A trailing message invalidates the latest turn");
            messages.RemoveAt(messages.Count - 1);

            AssertTrue(
                (bool)InvokePrivate(
                    formType,
                    form,
                    "CanRegenerateLatestTurn"),
                "A reusable latest turn can regenerate while idle");

            InvokePrivate(
                formType,
                form,
                "RememberFailedGeneration",
                temporarySession,
                false,
                "retry normal prompt");
            AssertRetryState(
                formType,
                form,
                true,
                false,
                GetSessionId(temporarySession),
                "retry normal prompt",
                true,
                "重试",
                "Normal failed generation is remembered");
            InvokePrivate(
                formType,
                form,
                "RememberFailedGeneration",
                temporarySession,
                true,
                "retry regeneration prompt");
            AssertRetryState(
                formType,
                form,
                true,
                true,
                GetSessionId(temporarySession),
                "retry regeneration prompt",
                true,
                "重试生成",
                "Failed regeneration is remembered");

            busyCancellation = new CancellationTokenSource();
            SetField(
                formType,
                form,
                "generationCancellation",
                busyCancellation);
            InvokePrivate(formType, form, "UpdateRetryButton");
            InvokePrivate(formType, form, "UpdateOutputButtons", true);
            AssertTrue(
                !retry.Enabled && !regenerate.Enabled &&
                    !quickModel.Enabled &&
                    !(bool)InvokePrivate(
                        formType,
                        form,
                        "CanRegenerateLatestTurn"),
                "Generation disables retry and regeneration controls");
            SetField(formType, form, "generationCancellation", null);
            busyCancellation.Dispose();
            busyCancellation = null;

            busyCancellation = new CancellationTokenSource();
            SetField(
                formType,
                form,
                "connectionTestCancellation",
                busyCancellation);
            InvokePrivate(
                formType,
                form,
                "SetConnectionTestingState",
                true);
            AssertTrue(
                !retry.Enabled && !regenerate.Enabled &&
                    !quickModel.Enabled &&
                    !(bool)InvokePrivate(
                        formType,
                        form,
                        "CanRegenerateLatestTurn"),
                "Connection testing disables retry and regeneration controls");
            SetField(formType, form, "connectionTestCancellation", null);
            busyCancellation.Dispose();
            busyCancellation = null;
            InvokePrivate(
                formType,
                form,
                "SetConnectionTestingState",
                false);

            SetField(formType, form, "isAddingFiles", true);
            InvokePrivate(formType, form, "UpdateRetryButton");
            InvokePrivate(formType, form, "UpdateOutputButtons", false);
            AssertTrue(
                !retry.Enabled && !regenerate.Enabled &&
                    !quickModel.Enabled &&
                    !(bool)InvokePrivate(
                        formType,
                        form,
                        "CanRegenerateLatestTurn"),
                "File reading disables retry and regeneration controls");
            SetField(formType, form, "isAddingFiles", false);
            InvokePrivate(formType, form, "UpdateRetryButton");
            InvokePrivate(formType, form, "UpdateOutputButtons", false);

            string reusableContent = (string)GetProperty(user, "Content");
            SetProperty(
                user,
                "Content",
                reusableContent + "\r\n以下图片或内联文件仅随本轮发送");
            AssertTrue(
                !(bool)InvokePrivate(
                    formType,
                    form,
                    "CanRegenerateLatestTurn"),
                "A binary-only attachment marker prevents regeneration");
            SetProperty(user, "Content", reusableContent);

            storeType.GetField(
                "writeBlockedByRecovery",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(
                    store,
                    true);
            AssertTrue(
                !(bool)InvokePrivate(
                    formType,
                    form,
                    "CanRegenerateLatestTurn"),
                "Conversation write protection prevents regeneration");
            storeType.GetField(
                "writeBlockedByRecovery",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(
                    store,
                    false);

            InvokePrivate(formType, form, "ClearRetryState");
            AssertRetryState(
                formType,
                form,
                false,
                false,
                string.Empty,
                string.Empty,
                false,
                "重试",
                "Clearing retry resets all retry state");

            Type profileType = application.GetType(
                "FilePromptAIWin7.ModelProfile",
                true);
            Type profilesType = typeof(List<>).MakeGenericType(profileType);
            IList profiles = (IList)Activator.CreateInstance(profilesType);
            object profile = Activator.CreateInstance(profileType, true);
            SetProperty(profile, "Name", "Saved profile");
            SetProperty(
                profile,
                "EndpointUrl",
                "https://example.invalid/v1/chat/completions");
            SetProperty(profile, "ApiKey", "profile-key");
            SetProperty(profile, "ModelName", "CaseSensitiveModel");
            profiles.Add(profile);
            SetField(formType, form, "modelProfiles", profiles);
            endpoint.Text = "HTTPS://EXAMPLE.INVALID/v1/chat/completions";
            apiKey.Text = "profile-key";
            model.Text = "CaseSensitiveModel";
            InvokePrivate(formType, form, "UpdateQuickModelButton");
            AssertTrue(
                quickModel.Text == "Saved profile" &&
                    quickModel.AccessibleDescription ==
                        "当前模型：CaseSensitiveModel",
                "Quick model matches endpoint without case sensitivity");

            model.Text = "casesensitivemodel";
            InvokePrivate(formType, form, "UpdateQuickModelButton");
            AssertTrue(
                quickModel.Text == "casesensitivemodel" &&
                    quickModel.AccessibleDescription ==
                        "当前模型：casesensitivemodel",
                "Quick model matches model names with ordinal casing");

            model.Text = "  custom-model  ";
            InvokePrivate(formType, form, "UpdateQuickModelButton");
            AssertTrue(
                quickModel.Text == "custom-model" &&
                    quickModel.AccessibleDescription ==
                        "当前模型：custom-model",
                "Quick model shows a trimmed unmatched model name");
            model.Text = string.Empty;
            InvokePrivate(formType, form, "UpdateQuickModelButton");
            AssertTrue(
                quickModel.Text == "模型" &&
                    quickModel.AccessibleDescription == "尚未配置模型",
                "Quick model shows an unconfigured fallback");

            string longName = RepeatText("Long profile ", 24);
            SetProperty(profile, "Name", longName);
            model.Text = "CaseSensitiveModel";
            window.Size = window.MinimumSize;
            window.PerformLayout();
            Application.DoEvents();
            InvokePrivate(formType, form, "UpdateQuickModelButton");
            AssertTrue(
                quickModel.Text == longName && quickModel.Width == 104 &&
                    quickModel.AutoEllipsis && !quickModel.AutoSize &&
                    quickModel.Parent != null &&
                    quickModel.Right <= quickModel.Parent.ClientSize.Width,
                "Long profile names do not resize or overflow the quick control");
            model.Text = RepeatText("unmatched-model", 20);
            InvokePrivate(formType, form, "UpdateQuickModelButton");
            AssertTrue(
                quickModel.Width == 104 && quickModel.AutoEllipsis &&
                    !quickModel.AutoSize,
                "Long custom model names keep the quick control stable");
        }
        finally
        {
            if (busyCancellation != null)
            {
                busyCancellation.Dispose();
            }

            SetField(
                formType,
                form,
                "generationCancellation",
                originalGeneration);
            SetField(
                formType,
                form,
                "connectionTestCancellation",
                originalConnection);
            SetField(
                formType,
                form,
                "isAddingFiles",
                originalAddingFiles);
            storeType.GetField(
                "writeBlockedByRecovery",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(
                    store,
                    originalWriteBlocked);

            if (temporarySession != null)
            {
                ((IList)GetProperty(temporarySession, "Messages")).Clear();
                InvokePublic(
                    store,
                    "DeleteSession",
                    GetSessionId(temporarySession));
            }
            if (!string.IsNullOrEmpty(originalCurrentId))
            {
                InvokePublic(store, "SelectSession", originalCurrentId);
            }

            SetField(formType, form, "modelProfiles", originalProfiles);
            endpoint.Text = originalEndpoint;
            apiKey.Text = originalApiKey;
            model.Text = originalModel;
            window.Size = originalWindowSize;
            ((System.Windows.Forms.Timer)GetField(
                formType,
                form,
                "contextSummaryTimer")).Stop();
            SetField(
                formType,
                form,
                "retryAvailable",
                originalRetryAvailable);
            SetField(
                formType,
                form,
                "retryRegeneration",
                originalRetryRegeneration);
            SetField(
                formType,
                form,
                "retrySessionId",
                originalRetrySessionId);
            SetField(
                formType,
                form,
                "retryPromptText",
                originalRetryPrompt);
            InvokePrivate(formType, form, "UpdateRetryButton");
            InvokePrivate(formType, form, "UpdateQuickModelButton");
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
        }
    }

    private static void TestGenerationRetryWorkflows(
        Assembly application,
        Type formType,
        object form)
    {
        object store = GetField(formType, form, "conversationStore");
        Type storeType = store.GetType();
        IList originalSessions = (IList)storeType.GetProperty(
            "Sessions").GetValue(store, null);
        HashSet<string> originalIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (object session in originalSessions)
        {
            originalIds.Add(GetSessionId(session));
        }

        string originalCurrentId = Convert.ToString(
            storeType.GetProperty("CurrentSessionId").GetValue(store, null));
        object originalExtensions = GetField(
            formType,
            form,
            "extensionSettings");
        Control endpoint = (Control)GetField(
            formType,
            form,
            "endpointTextBox");
        Control apiKey = (Control)GetField(
            formType,
            form,
            "apiKeyTextBox");
        Control model = (Control)GetField(
            formType,
            form,
            "modelTextBox");
        RichTextBox prompt = (RichTextBox)GetField(
            formType,
            form,
            "promptTextBox");
        RichTextBox output = (RichTextBox)GetField(
            formType,
            form,
            "outputTextBox");
        string originalEndpoint = endpoint.Text;
        string originalApiKey = apiKey.Text;
        string originalModel = model.Text;
        string originalPrompt = prompt.Text;
        bool originalRetryAvailable = (bool)GetField(
            formType,
            form,
            "retryAvailable");
        bool originalRetryRegeneration = (bool)GetField(
            formType,
            form,
            "retryRegeneration");
        string originalRetrySessionId = (string)GetField(
            formType,
            form,
            "retrySessionId");
        string originalRetryPrompt = (string)GetField(
            formType,
            form,
            "retryPromptText");
        string storagePath = (string)storeType.GetField(
            "storagePath",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
        Type messageType = application.GetType(
            "FilePromptAIWin7.ConversationMessage",
            true);
        Type extensionType = application.GetType(
            "FilePromptAIWin7.ExtensionSettings",
            true);
        object regenerationSession = null;
        object normalSession = null;
        TcpTestServer server = null;
        System.Windows.Forms.Timer errorDialogTimer = null;
        bool errorDialogSeen = false;
        try
        {
            SetField(
                formType,
                form,
                "extensionSettings",
                Activator.CreateInstance(extensionType, true));
            endpoint.Text = "http://127.0.0.1:1/v1/chat/completions";
            apiKey.Text = "ui-generation-key";
            model.Text = "ui-generation-model";
            prompt.Clear();

            regenerationSession = InvokePublic(
                store,
                "CreateSession",
                "regeneration workflow " + Guid.NewGuid().ToString("N"));
            string regenerationSessionId = GetSessionId(regenerationSession);
            object prefixUser = CreateConversationMessage(
                messageType,
                "user",
                "prefix question");
            object prefixAssistant = CreateConversationMessage(
                messageType,
                "assistant",
                "prefix answer");
            SetProperty(
                prefixAssistant,
                "ParentMessageId",
                (string)GetProperty(prefixUser, "Id"));
            object latestUser = CreateConversationMessage(
                messageType,
                "user",
                "用户要求：\r\nlatest regeneration prompt\r\n");
            SetProperty(
                latestUser,
                "ParentMessageId",
                (string)GetProperty(prefixAssistant, "Id"));
            object oldAssistant = CreateConversationMessage(
                messageType,
                "assistant",
                "old assistant reply");
            SetProperty(
                oldAssistant,
                "ParentMessageId",
                (string)GetProperty(latestUser, "Id"));
            SetProperty(oldAssistant, "VariantIndex", 3);
            AddPersistedMessage(store, regenerationSessionId, prefixUser);
            AddPersistedMessage(store, regenerationSessionId, prefixAssistant);
            AddPersistedMessage(store, regenerationSessionId, latestUser);
            AddPersistedMessage(store, regenerationSessionId, oldAssistant);
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");

            IList messages = (IList)GetProperty(
                regenerationSession,
                "Messages");
            TestRegenerationPromptBudgets(
                application,
                formType,
                form,
                regenerationSession,
                messages,
                originalExtensions);
            string diskBefore = Convert.ToBase64String(
                File.ReadAllBytes(storagePath));
            DateTime updatedBefore = (DateTime)GetProperty(
                regenerationSession,
                "UpdatedAt");

            server = TcpTestServer.StartStalled();
            endpoint.Text = server.Url;
            object turn = GetLatestRegenerationTurn(formType, form);
            Task cancellationTask = InvokeGenerateAsync(
                formType,
                form,
                true,
                turn,
                null);
            PumpUntil(
                delegate
                {
                    return server.RequestReceived && GetField(
                        formType,
                        form,
                        "generationCancellation") != null;
                },
                10000,
                "Cancelled regeneration reaches the local endpoint");
            ((CancellationTokenSource)GetField(
                formType,
                form,
                "generationCancellation")).Cancel();
            PumpTask(
                cancellationTask,
                10000,
                "Cancelled regeneration completes");
            server.Dispose();
            server = null;
            AssertTrue(
                messages.Count == 4 &&
                    ReferenceEquals(messages[0], prefixUser) &&
                    ReferenceEquals(messages[1], prefixAssistant) &&
                    ReferenceEquals(messages[2], latestUser) &&
                    ReferenceEquals(messages[3], oldAssistant) &&
                    (DateTime)GetProperty(
                        regenerationSession,
                        "UpdatedAt") == updatedBefore &&
                    Convert.ToBase64String(File.ReadAllBytes(storagePath)) ==
                        diskBefore &&
                    !(bool)GetField(
                        formType,
                        form,
                        "retryAvailable") &&
                    output.Text.IndexOf(
                        "old assistant reply",
                        StringComparison.Ordinal) >= 0,
                "Cancelled regeneration preserves the original reply");

            string replacementText = "replacement assistant reply";
            server = TcpTestServer.StartErrorThenSuccess(
                400,
                "{\"error\":{\"message\":\"intentional failure\"}}",
                replacementText);
            endpoint.Text = server.Url;
            errorDialogTimer = CreateMessageBoxCloser(
                "模型调用失败",
                delegate { errorDialogSeen = true; });
            errorDialogTimer.Start();
            turn = GetLatestRegenerationTurn(formType, form);
            Task failedTask = InvokeGenerateAsync(
                formType,
                form,
                true,
                turn,
                null);
            PumpTask(failedTask, 10000, "Failed regeneration completes");
            errorDialogTimer.Stop();
            errorDialogTimer.Dispose();
            errorDialogTimer = null;
            AssertTrue(
                errorDialogSeen && messages.Count == 4 &&
                    ReferenceEquals(messages[3], oldAssistant) &&
                    Convert.ToBase64String(File.ReadAllBytes(storagePath)) ==
                        diskBefore &&
                    output.Text.IndexOf(
                        "old assistant reply",
                        StringComparison.Ordinal) >= 0,
                "Failed regeneration preserves the original reply");
            AssertRetryState(
                formType,
                form,
                true,
                true,
                regenerationSessionId,
                (string)GetProperty(latestUser, "Content"),
                true,
                "重试生成",
                "Failed regeneration enables an in-place retry");

            InvokePrivate(formType, form, "RetryLastFailedGeneration");
            PumpUntil(
                delegate
                {
                    return server.RequestCount == 2 && GetField(
                        formType,
                        form,
                        "generationCancellation") == null;
                },
                10000,
                "Regeneration retry completes");
            server.Dispose();
            server = null;
            messages = (IList)GetProperty(regenerationSession, "Messages");
            object replacement = messages[messages.Count - 1];
            AssertTrue(
                messages.Count == 4 &&
                    ReferenceEquals(messages[0], prefixUser) &&
                    ReferenceEquals(messages[1], prefixAssistant) &&
                    ReferenceEquals(messages[2], latestUser) &&
                    !ReferenceEquals(replacement, oldAssistant) &&
                    (string)GetProperty(replacement, "Role") == "assistant" &&
                    (string)GetProperty(replacement, "Content") ==
                        replacementText &&
                    (string)GetProperty(replacement, "ParentMessageId") ==
                        (string)GetProperty(latestUser, "Id") &&
                    (int)GetProperty(replacement, "VariantIndex") == 4 &&
                    !string.Equals(
                        (string)GetProperty(replacement, "Id"),
                        (string)GetProperty(oldAssistant, "Id"),
                        StringComparison.OrdinalIgnoreCase) &&
                    CountSessionMessagesWithContent(
                        messages,
                        replacementText) == 1 &&
                    CountSessionMessagesWithContent(
                        messages,
                        "old assistant reply") == 0,
                "Successful regeneration retry replaces the assistant in place");
            AssertRetryState(
                formType,
                form,
                false,
                false,
                string.Empty,
                string.Empty,
                false,
                "重试",
                "Successful regeneration clears retry state");
            AssertReloadedMessages(
                storeType,
                storagePath,
                regenerationSessionId,
                4,
                replacementText,
                (string)GetProperty(latestUser, "Id"),
                4,
                "Regenerated reply persists after reload");

            normalSession = InvokePublic(
                store,
                "CreateSession",
                "normal retry workflow " + Guid.NewGuid().ToString("N"));
            string normalSessionId = GetSessionId(normalSession);
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            string normalInstruction = "normal retry prompt";
            string normalPrompt = "用户要求：\r\n" +
                normalInstruction + "\r\n";
            server = TcpTestServer.StartSuccess("normal retry answer");
            endpoint.Text = server.Url;
            InvokePrivate(
                formType,
                form,
                "RememberFailedGeneration",
                normalSession,
                false,
                normalInstruction);
            InvokePrivate(formType, form, "RetryLastFailedGeneration");
            PumpUntil(
                delegate
                {
                    return server.RequestReceived && GetField(
                        formType,
                        form,
                        "generationCancellation") == null;
                },
                10000,
                "Normal retry completes");
            server.Dispose();
            server = null;
            IList normalMessages = (IList)GetProperty(
                normalSession,
                "Messages");
            AssertTrue(
                normalMessages.Count == 2 &&
                    (string)GetProperty(normalMessages[0], "Role") == "user" &&
                    (string)GetProperty(normalMessages[0], "Content") ==
                        normalPrompt &&
                    (string)GetProperty(normalMessages[1], "Role") ==
                        "assistant" &&
                    (string)GetProperty(normalMessages[1], "Content") ==
                        "normal retry answer" &&
                    CountSessionMessagesWithContent(
                        normalMessages,
                        normalPrompt) == 1 &&
                    CountSessionMessagesWithContent(
                        normalMessages,
                        "normal retry answer") == 1,
                "Normal retry saves exactly one user and assistant pair");
            AssertReloadedMessages(
                storeType,
                storagePath,
                normalSessionId,
                2,
                "normal retry answer",
                string.Empty,
                0,
                "Normal retry persists one complete turn after reload");
        }
        finally
        {
            if (errorDialogTimer != null)
            {
                errorDialogTimer.Stop();
                errorDialogTimer.Dispose();
            }
            if (server != null)
            {
                server.Dispose();
            }

            CancellationTokenSource activeCancellation = GetField(
                formType,
                form,
                "generationCancellation") as CancellationTokenSource;
            if (activeCancellation != null)
            {
                activeCancellation.Cancel();
                PumpUntil(
                    delegate
                    {
                        return GetField(
                            formType,
                            form,
                            "generationCancellation") == null;
                    },
                    5000,
                    "Generation cleanup completes");
            }

            IList sessions = (IList)storeType.GetProperty(
                "Sessions").GetValue(store, null);
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
                "extensionSettings",
                originalExtensions);
            endpoint.Text = originalEndpoint;
            apiKey.Text = originalApiKey;
            model.Text = originalModel;
            prompt.Text = originalPrompt;
            ((System.Windows.Forms.Timer)GetField(
                formType,
                form,
                "contextSummaryTimer")).Stop();
            SetField(
                formType,
                form,
                "retryAvailable",
                originalRetryAvailable);
            SetField(
                formType,
                form,
                "retryRegeneration",
                originalRetryRegeneration);
            SetField(
                formType,
                form,
                "retrySessionId",
                originalRetrySessionId);
            SetField(
                formType,
                form,
                "retryPromptText",
                originalRetryPrompt);
            InvokePrivate(formType, form, "UpdateRetryButton");
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            InvokePrivate(formType, form, "RestoreCurrentDraft");
        }
    }

    private static void TestRegenerationPromptBudgets(
        Assembly application,
        Type formType,
        object form,
        object session,
        IList messages,
        object originalExtensions)
    {
        Type extensionType = application.GetType(
            "FilePromptAIWin7.ExtensionSettings",
            true);
        Type skillType = application.GetType(
            "FilePromptAIWin7.SkillDefinition",
            true);
        RichTextBox output = (RichTextBox)GetField(
            formType,
            form,
            "outputTextBox");
        object latestUser = messages[messages.Count - 2];
        string originalUserContent = (string)GetProperty(
            latestUser,
            "Content");
        string originalOutput = output.Text;
        DateTime originalUpdatedAt = (DateTime)GetProperty(
            session,
            "UpdatedAt");
        int originalCount = messages.Count;
        System.Windows.Forms.Timer validationTimer = null;
        try
        {
            object settings = Activator.CreateInstance(extensionType, true);
            object skill = Activator.CreateInstance(skillType, true);
            SetProperty(skill, "Name", "budget skill");
            SetProperty(skill, "Enabled", true);
            SetProperty(skill, "Instructions", new string('s', 48000));
            ((IList)GetProperty(settings, "Skills")).Add(skill);
            SetField(formType, form, "extensionSettings", settings);

            bool dialogSeen = false;
            validationTimer = CreateMessageBoxCloser(
                "请检查输入",
                delegate { dialogSeen = true; });
            validationTimer.Start();
            Task systemBudgetTask = InvokeGenerateAsync(
                formType,
                form,
                true,
                GetLatestRegenerationTurn(formType, form),
                null);
            PumpTask(
                systemBudgetTask,
                5000,
                "Regeneration rejects an oversized system prompt");
            validationTimer.Stop();
            validationTimer.Dispose();
            validationTimer = null;
            AssertTrue(
                dialogSeen && messages.Count == originalCount &&
                    ReferenceEquals(
                        messages[messages.Count - 2],
                        latestUser) &&
                    (string)GetProperty(latestUser, "Content") ==
                        originalUserContent &&
                    (DateTime)GetProperty(session, "UpdatedAt") ==
                        originalUpdatedAt &&
                    output.Text == originalOutput &&
                    GetField(
                        formType,
                        form,
                        "generationCancellation") == null,
                "System prompt budget failure leaves the turn and UI unchanged");

            SetProperty(skill, "Instructions", new string('s', 24000));
            SetProperty(latestUser, "Content", new string('u', 24000));
            originalOutput = output.Text;
            dialogSeen = false;
            validationTimer = CreateMessageBoxCloser(
                "请检查输入",
                delegate { dialogSeen = true; });
            validationTimer.Start();
            Task combinedBudgetTask = InvokeGenerateAsync(
                formType,
                form,
                true,
                GetLatestRegenerationTurn(formType, form),
                null);
            PumpTask(
                combinedBudgetTask,
                5000,
                "Regeneration rejects a combined prompt overflow");
            validationTimer.Stop();
            validationTimer.Dispose();
            validationTimer = null;
            AssertTrue(
                dialogSeen && messages.Count == originalCount &&
                    ReferenceEquals(
                        messages[messages.Count - 2],
                        latestUser) &&
                    (DateTime)GetProperty(session, "UpdatedAt") ==
                        originalUpdatedAt &&
                    output.Text == originalOutput &&
                    GetField(
                        formType,
                        form,
                        "generationCancellation") == null,
                "Combined prompt budget failure leaves the turn and UI unchanged");
        }
        finally
        {
            if (validationTimer != null)
            {
                validationTimer.Stop();
                validationTimer.Dispose();
            }
            SetProperty(latestUser, "Content", originalUserContent);
            SetField(
                formType,
                form,
                "extensionSettings",
                Activator.CreateInstance(extensionType, true));
            InvokePrivate(formType, form, "RenderConversation", session);
        }
    }

    private static void TestRetryInvalidation(
        Assembly application,
        Type formType,
        object form,
        string dataRoot)
    {
        object store = GetField(formType, form, "conversationStore");
        Type storeType = store.GetType();
        IList originalSessions = (IList)storeType.GetProperty(
            "Sessions").GetValue(store, null);
        HashSet<string> originalIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (object session in originalSessions)
        {
            originalIds.Add(GetSessionId(session));
        }

        string originalCurrentId = Convert.ToString(
            storeType.GetProperty("CurrentSessionId").GetValue(store, null));
        bool originalArchiveView = (bool)GetField(
            formType,
            form,
            "showArchivedSessions");
        bool originalRetryAvailable = (bool)GetField(
            formType,
            form,
            "retryAvailable");
        bool originalRetryRegeneration = (bool)GetField(
            formType,
            form,
            "retryRegeneration");
        string originalRetrySessionId = (string)GetField(
            formType,
            form,
            "retrySessionId");
        string originalRetryPrompt = (string)GetField(
            formType,
            form,
            "retryPromptText");
        Type messageType = application.GetType(
            "FilePromptAIWin7.ConversationMessage",
            true);
        object source = null;
        object other = null;
        string backupPath = Path.Combine(
            dataRoot,
            "retry-restore-" + Guid.NewGuid().ToString("N") + ".fpc");
        string emptyBackupPath = Path.Combine(
            dataRoot,
            "retry-empty-" + Guid.NewGuid().ToString("N") + ".fpc");
        try
        {
            source = CreateSessionWithTurn(
                store,
                messageType,
                "retry invalidation source");
            other = InvokePublic(
                store,
                "CreateSession",
                "retry invalidation other");

            InvokePrivate(
                formType,
                form,
                "RememberFailedGeneration",
                source,
                false,
                "session switch retry");
            InvokePrivate(formType, form, "RefreshSessionList");
            ListBox sessionList = (ListBox)GetField(
                formType,
                form,
                "sessionListBox");
            sessionList.SelectedIndex = FindSessionIndex(
                sessionList.Items,
                GetSessionId(other));
            InvokePrivate(
                formType,
                form,
                "OnSessionSelected",
                sessionList,
                EventArgs.Empty);
            AssertRetryCleared(
                formType,
                form,
                "Switching sessions immediately expires retry state");

            InvokePublic(store, "SelectSession", GetSessionId(source));
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            InvokePrivate(
                formType,
                form,
                "RememberFailedGeneration",
                source,
                true,
                "archive retry");
            InvokePrivate(
                formType,
                form,
                "OnToggleSessionArchivedClick",
                form,
                EventArgs.Empty);
            AssertRetryCleared(
                formType,
                form,
                "Archiving a session immediately expires retry state");
            InvokePublic(
                store,
                "SetSessionArchived",
                GetSessionId(source),
                false);

            InvokePublic(store, "SelectSession", GetSessionId(source));
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            InvokePrivate(
                formType,
                form,
                "RememberFailedGeneration",
                source,
                true,
                "branch retry");
            InvokePrivate(
                formType,
                form,
                "OnBranchSessionClick",
                form,
                EventArgs.Empty);
            AssertRetryCleared(
                formType,
                form,
                "Branching a session immediately expires retry state");

            InvokePublic(store, "SelectSession", GetSessionId(source));
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            InvokePrivate(
                formType,
                form,
                "RememberFailedGeneration",
                source,
                false,
                "delete retry");
            AssertTrue(
                (bool)InvokePrivate(
                    formType,
                    form,
                    "DeleteSessionAndClearDraft",
                    source),
                "Retry invalidation fixture deletes its current session");
            AssertRetryCleared(
                formType,
                form,
                "Deleting a session immediately expires retry state");
            source = null;

            object backupSourceStore = CreateStore(storeType, Path.Combine(
                dataRoot,
                "retry-backup-source-" +
                    Guid.NewGuid().ToString("N") + ".xml"));
            object backupSession = InvokePublic(
                backupSourceStore,
                "CreateSession",
                "restored retry session");
            AddPersistedMessage(
                backupSourceStore,
                GetSessionId(backupSession),
                CreateConversationMessage(
                    messageType,
                    "user",
                    "restored question"));
            InvokePublic(backupSourceStore, "ExportBackup", backupPath);

            object emptyStore = CreateStore(storeType, Path.Combine(
                dataRoot,
                "retry-empty-source-" +
                    Guid.NewGuid().ToString("N") + ".xml"));
            InvokePublic(emptyStore, "ExportBackup", emptyBackupPath);

            object current = storeType.GetProperty(
                "CurrentSession").GetValue(store, null);
            InvokePrivate(
                formType,
                form,
                "RememberFailedGeneration",
                current,
                false,
                "empty restore retry");
            InvokePrivate(
                formType,
                form,
                "RestoreSessionsFromPath",
                emptyBackupPath);
            AssertRetryState(
                formType,
                form,
                true,
                false,
                GetSessionId(current),
                "empty restore retry",
                true,
                "重试",
                "Restoring an empty backup preserves retry state");

            InvokePrivate(
                formType,
                form,
                "RestoreSessionsFromPath",
                backupPath);
            AssertRetryCleared(
                formType,
                form,
                "Restoring sessions immediately expires retry state");
        }
        finally
        {
            IList sessions = (IList)storeType.GetProperty(
                "Sessions").GetValue(store, null);
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
            SetField(
                formType,
                form,
                "retryAvailable",
                originalRetryAvailable);
            SetField(
                formType,
                form,
                "retryRegeneration",
                originalRetryRegeneration);
            SetField(
                formType,
                form,
                "retrySessionId",
                originalRetrySessionId);
            SetField(
                formType,
                form,
                "retryPromptText",
                originalRetryPrompt);
            InvokePrivate(formType, form, "UpdateRetryButton");
            InvokePrivate(formType, form, "UpdateSessionViewButtons");
            InvokePrivate(formType, form, "RefreshSessionList");
            InvokePrivate(formType, form, "LoadCurrentSession");
            InvokePrivate(formType, form, "RestoreCurrentDraft");
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

    private static T FindControlByAccessibleName<T>(
        Control root,
        string accessibleName) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            T match = child as T;
            if (match != null && string.Equals(
                match.AccessibleName,
                accessibleName,
                StringComparison.Ordinal))
            {
                return match;
            }

            T nested = FindControlByAccessibleName<T>(
                child,
                accessibleName);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static int CountVisibleControls(Control[] controls)
    {
        int count = 0;
        foreach (Control control in controls)
        {
            if (control != null && control.Visible)
            {
                count++;
            }
        }

        return count;
    }

    private static bool FitsCenteredFullHd(Form dialog)
    {
        const int fullHdWidth = 1920;
        const int fullHdHeight = 1080;
        return dialog.Width <= fullHdWidth && dialog.Height <= fullHdHeight &&
            (fullHdWidth - dialog.Width) / 2 >= 0 &&
            (fullHdHeight - dialog.Height) / 2 >= 0;
    }

    private static string FindVisibleControlBoundsError(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            Rectangle client = root.ClientRectangle;
            Rectangle bounds = child.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0 ||
                bounds.Left < client.Left || bounds.Top < client.Top ||
                bounds.Right > client.Right || bounds.Bottom > client.Bottom)
            {
                return DescribeControl(child) + " bounds=" + bounds +
                    " parent=" + DescribeControl(root) +
                    " client=" + client;
            }

            string nestedError = FindVisibleControlBoundsError(child);
            if (!string.IsNullOrEmpty(nestedError))
            {
                return nestedError;
            }
        }

        return string.Empty;
    }

    private static string FindVisibleButtonOverlapError(Control root)
    {
        List<Button> buttons = new List<Button>();
        AddVisibleButtons(root, buttons);
        for (int firstIndex = 0;
            firstIndex < buttons.Count;
            firstIndex++)
        {
            Button first = buttons[firstIndex];
            Rectangle firstBounds = first.RectangleToScreen(
                first.ClientRectangle);
            for (int secondIndex = firstIndex + 1;
                secondIndex < buttons.Count;
                secondIndex++)
            {
                Button second = buttons[secondIndex];
                Rectangle secondBounds = second.RectangleToScreen(
                    second.ClientRectangle);
                Rectangle intersection = Rectangle.Intersect(
                    firstBounds,
                    secondBounds);
                if (!intersection.IsEmpty &&
                    intersection.Width > 0 && intersection.Height > 0)
                {
                    return DescribeControl(first) + " " + firstBounds +
                        " overlaps " + DescribeControl(second) + " " +
                        secondBounds;
                }
            }
        }

        return string.Empty;
    }

    private static void AddVisibleButtons(
        Control root,
        IList<Button> result)
    {
        foreach (Control child in root.Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            Button button = child as Button;
            if (button != null)
            {
                result.Add(button);
            }

            AddVisibleButtons(child, result);
        }
    }

    private static string DescribeControl(Control control)
    {
        string value = string.IsNullOrWhiteSpace(control.Text)
            ? control.AccessibleName
            : control.Text;
        return control.GetType().Name + "[" + (value ?? string.Empty) + "]";
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
        ((Control)GetField(formType, form, "modelTextBox")).Text =
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

    private static object CreateConversationMessage(
        Type messageType,
        string role,
        string content)
    {
        return Activator.CreateInstance(
            messageType,
            new object[] { role, content });
    }

    private static void AddPersistedMessage(
        object store,
        string sessionId,
        object message)
    {
        AssertTrue(
            (bool)InvokePublic(
                store,
                "AddMessage",
                sessionId,
                message),
            "Conversation fixture message is persisted");
    }

    private static object CreateSessionWithTurn(
        object store,
        Type messageType,
        string title)
    {
        object session = InvokePublic(store, "CreateSession", title);
        object user = CreateConversationMessage(
            messageType,
            "user",
            title + " question");
        object assistant = CreateConversationMessage(
            messageType,
            "assistant",
            title + " answer");
        SetProperty(
            assistant,
            "ParentMessageId",
            (string)GetProperty(user, "Id"));
        AddPersistedMessage(store, GetSessionId(session), user);
        AddPersistedMessage(store, GetSessionId(session), assistant);
        return session;
    }

    private static object CreateStore(Type storeType, string storagePath)
    {
        ConstructorInfo constructor = storeType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new Type[] { typeof(string) },
            null);
        if (constructor == null)
        {
            throw new MissingMethodException(
                storeType.FullName,
                ".ctor(string)");
        }

        return constructor.Invoke(new object[] { storagePath });
    }

    private static void AssertLatestRegenerationTurn(
        Type formType,
        object form,
        bool expected,
        object expectedUser,
        object expectedAssistant,
        int expectedIndex,
        string name)
    {
        object[] arguments = new object[] { null };
        bool found = (bool)InvokePrivate(
            formType,
            form,
            "TryGetLatestRegenerationTurn",
            arguments);
        object turn = arguments[0];
        AssertTrue(
            found == expected &&
                (!expected ||
                    (turn != null &&
                        ReferenceEquals(
                            GetProperty(turn, "UserMessage"),
                            expectedUser) &&
                        ReferenceEquals(
                            GetProperty(turn, "AssistantMessage"),
                            expectedAssistant) &&
                        (int)GetProperty(turn, "UserMessageIndex") ==
                            expectedIndex)),
            name);
    }

    private static object GetLatestRegenerationTurn(
        Type formType,
        object form)
    {
        object[] arguments = new object[] { null };
        bool found = (bool)InvokePrivate(
            formType,
            form,
            "TryGetLatestRegenerationTurn",
            arguments);
        if (!found || arguments[0] == null)
        {
            throw new InvalidOperationException(
                "The latest regeneration fixture turn was not found.");
        }

        return arguments[0];
    }

    private static void AssertRetryState(
        Type formType,
        object form,
        bool available,
        bool regeneration,
        string sessionId,
        string prompt,
        bool buttonEnabled,
        string buttonText,
        string name)
    {
        Button button = (Button)GetField(
            formType,
            form,
            "retryButton");
        AssertTrue(
            (bool)GetField(formType, form, "retryAvailable") == available &&
                (bool)GetField(
                    formType,
                    form,
                    "retryRegeneration") == regeneration &&
                string.Equals(
                    (string)GetField(
                        formType,
                        form,
                        "retrySessionId"),
                    sessionId ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    (string)GetField(
                        formType,
                        form,
                        "retryPromptText"),
                    prompt ?? string.Empty,
                    StringComparison.Ordinal) &&
                button.Enabled == buttonEnabled &&
                button.Text == buttonText,
            name);
    }

    private static void AssertRetryCleared(
        Type formType,
        object form,
        string name)
    {
        AssertRetryState(
            formType,
            form,
            false,
            false,
            string.Empty,
            string.Empty,
            false,
            "重试",
            name);
    }

    private static Task InvokeGenerateAsync(
        Type formType,
        object form,
        bool regenerate,
        object regenerationTurn,
        string retryPrompt)
    {
        return (Task)InvokePrivate(
            formType,
            form,
            "GenerateAsync",
            regenerate,
            regenerationTurn,
            retryPrompt);
    }

    private static void PumpTask(Task task, int timeoutMilliseconds, string name)
    {
        if (task == null)
        {
            throw new InvalidOperationException(name + " returned no task.");
        }

        PumpUntil(
            delegate { return task.IsCompleted; },
            timeoutMilliseconds,
            name);
        task.GetAwaiter().GetResult();
        ThrowIfUiThreadException();
    }

    private static void PumpUntil(
        Func<bool> condition,
        int timeoutMilliseconds,
        string name)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(
            timeoutMilliseconds);
        while (!condition())
        {
            Application.DoEvents();
            ThrowIfUiThreadException();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(name + " timed out.");
            }

            Thread.Sleep(10);
        }

        Application.DoEvents();
        ThrowIfUiThreadException();
    }

    private static System.Windows.Forms.Timer CreateMessageBoxCloser(
        string title,
        Action onClosed)
    {
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        timer.Interval = 50;
        timer.Tick += delegate
        {
            IntPtr handle = FindWindow(null, title);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (onClosed != null)
            {
                onClosed();
            }
            PostMessage(
                handle,
                WmClose,
                IntPtr.Zero,
                IntPtr.Zero);
        };
        return timer;
    }

    private static int CountSessionMessagesWithContent(
        IList messages,
        string content)
    {
        int count = 0;
        foreach (object message in messages)
        {
            if (message != null && string.Equals(
                Convert.ToString(GetProperty(message, "Content")),
                content,
                StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static void AssertReloadedMessages(
        Type storeType,
        string storagePath,
        string sessionId,
        int expectedCount,
        string expectedFinalContent,
        string expectedParentId,
        int expectedVariantIndex,
        string name)
    {
        object reloadedStore = CreateStore(storeType, storagePath);
        object session = InvokePublic(
            reloadedStore,
            "GetSession",
            sessionId);
        IList messages = session == null
            ? null
            : (IList)GetProperty(session, "Messages");
        object finalMessage = messages == null || messages.Count == 0
            ? null
            : messages[messages.Count - 1];
        AssertTrue(
            messages != null && messages.Count == expectedCount &&
                finalMessage != null &&
                (string)GetProperty(finalMessage, "Role") == "assistant" &&
                (string)GetProperty(finalMessage, "Content") ==
                    expectedFinalContent &&
                (expectedParentId.Length == 0 ||
                    (string)GetProperty(
                        finalMessage,
                        "ParentMessageId") == expectedParentId) &&
                (expectedVariantIndex == 0 ||
                    (int)GetProperty(
                        finalMessage,
                        "VariantIndex") == expectedVariantIndex),
            name);
    }

    private sealed class TcpTestServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread worker;
        private readonly ManualResetEvent releaseResponse;
        private readonly int statusCode;
        private readonly string responseBody;
        private readonly string secondResponseBody;
        private readonly bool stalled;
        private int requestCount;
        private volatile bool disposed;
        private Exception workerException;

        private TcpTestServer(
            int statusCode,
            string responseBody,
            bool stalled,
            string secondResponseBody)
        {
            this.statusCode = statusCode;
            this.responseBody = responseBody ?? string.Empty;
            this.stalled = stalled;
            this.secondResponseBody = secondResponseBody;
            releaseResponse = new ManualResetEvent(false);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = "http://127.0.0.1:" + port +
                "/v1/chat/completions";
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "UiStateSmokeTest HTTP server";
            worker.Start();
        }

        public string Url { get; private set; }

        public bool RequestReceived
        {
            get { return Thread.VolatileRead(ref requestCount) > 0; }
        }

        public int RequestCount
        {
            get { return Thread.VolatileRead(ref requestCount); }
        }

        public static TcpTestServer StartSuccess(string content)
        {
            string encoded = JsonEscape(content ?? string.Empty);
            return new TcpTestServer(
                200,
                "data: {\"choices\":[{\"delta\":{\"content\":\"" +
                    encoded + "\"}}]}\r\n\r\n" +
                    "data: [DONE]\r\n\r\n",
                false,
                null);
        }

        public static TcpTestServer StartError(
            int statusCode,
            string body)
        {
            return new TcpTestServer(statusCode, body, false, null);
        }

        public static TcpTestServer StartErrorThenSuccess(
            int statusCode,
            string errorBody,
            string successContent)
        {
            string encoded = JsonEscape(successContent ?? string.Empty);
            string successBody =
                "data: {\"choices\":[{\"delta\":{\"content\":\"" +
                    encoded + "\"}}]}\r\n\r\n" +
                    "data: [DONE]\r\n\r\n";
            return new TcpTestServer(
                statusCode,
                errorBody,
                false,
                successBody);
        }

        public static TcpTestServer StartStalled()
        {
            return new TcpTestServer(200, string.Empty, true, null);
        }

        private void Run()
        {
            try
            {
                int responseCount = secondResponseBody == null ? 1 : 2;
                for (int index = 0; index < responseCount; index++)
                {
                    using (TcpClient connection = listener.AcceptTcpClient())
                    using (NetworkStream stream = connection.GetStream())
                    {
                        ReadRequest(stream);
                        Interlocked.Increment(ref requestCount);
                        if (stalled)
                        {
                            releaseResponse.WaitOne();
                            return;
                        }

                        int currentStatus = index == 0 ? statusCode : 200;
                        string currentBody = index == 0
                            ? responseBody
                            : secondResponseBody;
                        WriteResponse(stream, currentStatus, currentBody);
                    }
                }
            }
            catch (Exception exception)
            {
                if (!disposed)
                {
                    workerException = exception;
                }
            }
        }

        private static void WriteResponse(
            NetworkStream stream,
            int statusCode,
            string responseBody)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(responseBody);
            string reason = statusCode == 200 ? "OK" : "Bad Request";
            string contentType = statusCode == 200
                ? "text/event-stream"
                : "application/json";
            string headers = "HTTP/1.1 " + statusCode + " " + reason +
                "\r\nContent-Type: " + contentType +
                "; charset=utf-8\r\nContent-Length: " + bodyBytes.Length +
                "\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static void ReadRequest(NetworkStream stream)
        {
            stream.ReadTimeout = 10000;
            MemoryStream request = new MemoryStream();
            byte[] buffer = new byte[4096];
            int headerEnd = -1;
            int contentLength = 0;
            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                request.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(
                    request.GetBuffer(),
                    (int)request.Length);
            }

            string headers = Encoding.ASCII.GetString(
                request.GetBuffer(),
                0,
                headerEnd);
            foreach (string line in headers.Split(
                new string[] { "\r\n" },
                StringSplitOptions.None))
            {
                if (line.StartsWith(
                    "Content-Length:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(
                        line.Substring("Content-Length:".Length).Trim(),
                        out contentLength);
                }
            }

            int bodyStart = headerEnd + 4;
            while (request.Length - bodyStart < contentLength)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                request.Write(buffer, 0, read);
            }
        }

        private static int FindHeaderEnd(byte[] value, int length)
        {
            for (int index = 0; index <= length - 4; index++)
            {
                if (value[index] == 13 && value[index + 1] == 10 &&
                    value[index + 2] == 13 && value[index + 3] == 10)
                {
                    return index;
                }
            }

            return -1;
        }

        private static string JsonEscape(string value)
        {
            return value.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            releaseResponse.Set();
            listener.Stop();
            if (!worker.Join(5000))
            {
                throw new TimeoutException(
                    "The UI test HTTP server did not stop.");
            }

            releaseResponse.Dispose();
            if (workerException != null)
            {
                throw new InvalidOperationException(
                    "The UI test HTTP server failed.",
                    workerException);
            }
        }
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

    private static Exception CaptureInvocationFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return Unwrap(exception);
        }
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
