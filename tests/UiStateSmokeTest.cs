using System;
using System.Collections;
using System.Collections.Generic;
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
            ThrowIfUiThreadException();
            TestCtrlNBusyGuard(formType, form);
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

    private static void TestWorkspaceLayoutAndIncrementalTranscript(
        Type formType,
        object form)
    {
        AssertTrue(
            !(bool)GetField(formType, form, "settingsExpanded"),
            "Connection settings are hidden by default");
        RowStyle settingsRow = GetField(
            formType,
            form,
            "settingsRowStyle") as RowStyle;
        AssertTrue(
            settingsRow != null && settingsRow.Height == 0F,
            "Hidden settings do not consume workspace height");
        RowStyle inputsRow = GetField(
            formType,
            form,
            "inputsAreaRowStyle") as RowStyle;
        RowStyle promptRow = GetField(
            formType,
            form,
            "promptAreaRowStyle") as RowStyle;
        AssertTrue(
            inputsRow != null && inputsRow.Height >= 84F &&
                inputsRow.Height <= 100F &&
                promptRow != null && promptRow.Height >= 90F,
            "Empty attachment tray stays compact while the prompt remains usable");
        RichTextBox output = GetField(
            formType,
            form,
            "outputTextBox") as RichTextBox;
        ListView inputList = GetField(
            formType,
            form,
            "inputListView") as ListView;
        Label contextSummary = GetField(
            formType,
            form,
            "contextSummaryLabel") as Label;
        ToolTip contextToolTip = GetField(
            formType,
            form,
            "contextSummaryToolTip") as ToolTip;
        AssertTrue(
            contextSummary != null &&
                contextSummary.AccessibleName == "当前会话上下文摘要" &&
                contextSummary.Text.IndexOf(
                    "条消息",
                    StringComparison.Ordinal) >= 0,
            "Context summary is visible in the workspace header");
        int extensionOffset = contextSummary.Text.IndexOf(
            "技能",
            StringComparison.Ordinal);
        int historyOffset = contextSummary.Text.IndexOf(
            "历史",
            StringComparison.Ordinal);
        AssertTrue(
            extensionOffset >= 0 && historyOffset > extensionOffset &&
                contextToolTip != null &&
                contextToolTip.GetToolTip(contextSummary) ==
                    contextSummary.Text &&
                contextSummary.AccessibleDescription == contextSummary.Text,
            "Compact summary prioritizes extensions and exposes full text");

        Form window = form as Form;
        AssertTrue(
            window != null && output != null && inputList != null,
            "Main workspace controls exist");
        Button extensionsButton = GetField(
            formType,
            form,
            "extensionsButton") as Button;
        Button toggleSettingsButton = GetField(
            formType,
            form,
            "toggleSettingsButton") as Button;
        Button moreButton = GetField(
            formType,
            form,
            "moreButton") as Button;
        AssertTrue(
            extensionsButton != null && toggleSettingsButton != null &&
                moreButton != null,
            "Header workspace actions exist");
        Size originalSize = window.Size;
        window.Show();
        Application.DoEvents();
        window.Size = window.MinimumSize;
        InvokePrivate(formType, form, "UpdateHeaderActionsLayout");
        Application.DoEvents();
        AssertTrue(
            !extensionsButton.Visible && !toggleSettingsButton.Visible &&
                moreButton.Visible && moreButton.Text == "菜单",
            "Narrow workspace collapses secondary header actions");
        window.Size = originalSize;
        InvokePrivate(formType, form, "UpdateHeaderActionsLayout");
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
            inputsRow.Height >= 120F && inputList.Visible,
            "Attachment tray expands when an item is present");
        InvokePrivate(formType, form, "OnClearClick", form, EventArgs.Empty);
        Application.DoEvents();
        AssertTrue(
            inputsRow.Height <= 100F && !inputList.Visible,
            "Attachment tray collapses after items are cleared");
        window.Size = window.MinimumSize;
        InvokePrivate(formType, form, "SetSettingsExpanded", true);
        Application.DoEvents();
        InvokePrivate(formType, form, "UpdateConversationAreaRows");
        Application.DoEvents();
        AssertTrue(
            output.Height >= 32,
            "Transcript remains visible with settings open at minimum size");
        InvokePrivate(formType, form, "SetSettingsExpanded", false);
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

    private static void TestSendShortcutConfig(
        Type formType,
        object form)
    {
        object generateButton = GetField(formType, form, "generateButton");
        ContextMenuStrip menu =
            ((Button)generateButton).ContextMenuStrip;
        AssertTrue(menu != null, "Send button shortcut menu exists");
        AssertTrue(menu.Items.Count >= 4, "Send button shortcut menu has options");

        int checkedCount = 0;
        foreach (ToolStripItem item in menu.Items)
        {
            ToolStripMenuItem menuItem = item as ToolStripMenuItem;
            if (menuItem != null && menuItem.Tag != null)
            {
                checkedCount += menuItem.Checked ? 1 : 0;
            }
        }
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

    private static void TestCtrlNBusyGuard(Type formType, object form)
    {
        object store = GetField(formType, form, "conversationStore");
        int before = ((IList)store.GetType().GetProperty("Sessions")
            .GetValue(store, null)).Count;
        SetField(formType, form, "isAddingFiles", true);
        try
        {
            KeyEventArgs keys = new KeyEventArgs(Keys.Control | Keys.N);
            InvokePrivate(formType, form, "OnMainKeyDown", form, keys);
            int after = ((IList)store.GetType().GetProperty("Sessions")
                .GetValue(store, null)).Count;
            AssertTrue(before == after, "Ctrl+N is blocked during extraction");
        }
        finally
        {
            SetField(formType, form, "isAddingFiles", false);
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
        AssertTrue(readButton != null && readButton.Text == "读取路径",
            "Path read action is visible");

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
        Button moreButton = GetField(
            formType,
            form,
            "moreButton") as Button;
        AssertTrue(
            moreButton != null && moreButton.ContextMenuStrip != null,
            "More menu exists for model profiles");
        AssertTrue(
            ContainsMenuText(moreButton.ContextMenuStrip, "测试模型连接") &&
                ContainsMenuText(moreButton.ContextMenuStrip, "技能 / MCP...") &&
                ContainsMenuText(moreButton.ContextMenuStrip, "连接设置..."),
            "More menu keeps connection and extension actions available");
        ToolStripItem profilesItem = null;
        foreach (ToolStripItem item in moreButton.ContextMenuStrip.Items)
        {
            if (string.Equals(
                item.Text,
                "模型配置...",
                StringComparison.Ordinal))
            {
                profilesItem = item;
                break;
            }
        }
        AssertTrue(profilesItem != null, "Model profiles menu entry exists");

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
        Button moreButton = GetField(
            formType,
            form,
            "moreButton") as Button;
        AssertTrue(moreButton != null, "More button exists");
        AssertTrue(
            moreButton.ContextMenuStrip != null,
            "More menu exists");
        ToolStripItem uninstallItem = null;
        foreach (ToolStripItem item in moreButton.ContextMenuStrip.Items)
        {
            if (string.Equals(
                item.Text,
                "卸载 FilePrompt AI...",
                StringComparison.Ordinal))
            {
                uninstallItem = item;
                break;
            }
        }
        AssertTrue(uninstallItem != null, "Uninstall entry exists");
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
