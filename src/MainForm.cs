using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FilePromptAIWin7
{
    internal sealed class MainForm : Form
    {
        internal const string WindowTitle =
            "FilePrompt AI  ·  内网文件问答工作台";
        private const int MaxDisplayedUserCharacters = 8000;
        private const int MaximumFilesPerAdd = 128;
        private const int MaximumPathCandidates = 4096;
        private const int PathResolutionTimeoutMilliseconds = 15000;
        private const long MaximumRetainedBinaryBytes =
            20L * 1024L * 1024L;
        // A conservative text budget keeps common custom-model context windows
        // usable. Binary transport size is guarded independently by ModelClient.
        private const long MaximumConversationContextCharacters = 48000L;
        private const string TruncatedFileTextNotice =
            "\r\n[已提取的文件正文超过本轮 48,000 字符预算，后续部分已省略]\r\n";
        private const float MinimumOutputAreaHeight = 128F;
        private const float CompactComposerHeight = 154F;
        private const float ExpandedComposerHeight = 246F;
        private const int EmGetScrollPosition = 0x04DD;
        private const int EmSetScrollPosition = 0x04DE;

        private readonly FileContentExtractor extractor;
        private readonly ModelClient modelClient;
        private readonly List<InputItem> inputItems;
        private readonly ConversationStore conversationStore;
        private readonly Dictionary<string, SessionDraft> sessionDrafts;
        private readonly ExtensionStore extensionStore;
        private readonly ModelProfileStore modelProfileStore;
        private readonly object streamOutputSync;
        private IList<ModelProfile> modelProfiles;
        private ExtensionSettings extensionSettings;

        private TableLayoutPanel rootLayout;
        private TableLayoutPanel workspaceLayout;
        private TableLayoutPanel conversationArea;
        private RowStyle composerAreaRowStyle;
        private RowStyle attachmentTrayRowStyle;
        private Control composerPanel;
        private Panel fileDropTargetPanel;
        private TextBox endpointTextBox;
        private TextBox apiKeyTextBox;
        private TextBox modelTextBox;
        private TextBox sessionSearchTextBox;
        private ListBox sessionListBox;
        private Label sessionTitleLabel;
        private Label connectionStatusLabel;
        private Label contextSummaryLabel;
        private ToolTip contextSummaryToolTip;
        private ListView inputListView;
        private RichTextBox promptTextBox;
        private RichTextBox outputTextBox;
        private Button addFileButton;
        private TextBox pathTextBox;
        private Button readPathButton;
        private Button generateButton;
        private Button stopButton;
        private Button copyOutputButton;
        private Button saveOutputButton;
        private ContextMenuStrip exportMenu;
        private Button newSessionButton;
        private Button deleteSessionButton;
        private Button renameSessionButton;
        private Button backupSessionsButton;
        private Button restoreSessionsButton;
        private Button settingsButton;
        private Button testConnectionButton;
        private Button extensionsButton;
        private Button promptActionsButton;
        private ToolStripStatusLabel statusLabel;
        private ToolStripProgressBar progressBar;
        private System.Windows.Forms.Timer sessionSearchTimer;
        private System.Windows.Forms.Timer contextSummaryTimer;
        private System.Windows.Forms.Timer streamFlushTimer;

        private CancellationTokenSource generationCancellation;
        private CancellationTokenSource connectionTestCancellation;
        private CancellationTokenSource fileAddCancellation;
        private StringBuilder streamedResponse;
        private StringBuilder pendingStreamOutput;
        private string renderedSessionId;
        private int renderedMessageCount;
        private long renderedSessionCharacterEstimate;
        private long extensionPromptCharacterEstimate;
        private int streamedTurnStart;
        private int streamedContentStart;
        private int generationSequence;
        private volatile int activeGenerationSequence;
        private bool isAddingFiles;
        private bool isClosing;
        private bool exitConfirmationGranted;
        private bool isLoadingSession;
        private bool isUpdatingConversationRows;
        private bool followStreamTail;
        private string sendShortcutMode;
        private GroupBox promptGroup;
        private SettingsDialog settingsDialog;
        private PathInputDialog pathInputDialog;

        private sealed class SessionDraft
        {
            public string Prompt { get; set; }
            public IList<InputItem> Items { get; set; }

            public SessionDraft()
            {
                Prompt = string.Empty;
                Items = new List<InputItem>();
            }
        }

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

        private sealed class FileResolutionResult
        {
            public IList<string> Files { get; private set; }
            public IList<string> RejectedPaths { get; private set; }
            public bool TooManyFiles { get; set; }

            public FileResolutionResult()
            {
                Files = new List<string>();
                RejectedPaths = new List<string>();
            }
        }

        private sealed class FileAddResult
        {
            public int AddedCount { get; set; }
            public int SkippedCount { get; set; }
            public IList<string> FailedPaths { get; private set; }
            public bool TimedOut { get; set; }

            public FileAddResult()
            {
                FailedPaths = new List<string>();
            }
        }

        public MainForm()
        {
            extractor = new FileContentExtractor();
            modelClient = new ModelClient();
            inputItems = new List<InputItem>();
            conversationStore = new ConversationStore();
            extensionStore = new ExtensionStore();
            extensionSettings = extensionStore.Load();
            extensionPromptCharacterEstimate =
                CalculateExtensionPromptCharacterEstimate(extensionSettings);
            modelProfileStore = new ModelProfileStore();
            modelProfiles = modelProfileStore.Load();
            streamOutputSync = new object();
            pendingStreamOutput = new StringBuilder();
            sessionDrafts = new Dictionary<string, SessionDraft>(
                StringComparer.OrdinalIgnoreCase);
            renderedMessageCount = -1;
            streamedTurnStart = -1;
            streamedContentStart = -1;

            InitializeWindow();
            BuildInterface();
            LoadSavedSettings();
            EnsureInitialSession();
            RefreshSessionList();
            LoadCurrentSession();
            if (!string.IsNullOrEmpty(conversationStore.LoadWarning))
            {
                SetStatus(conversationStore.LoadWarning);
            }
            else if (!string.IsNullOrEmpty(extensionStore.LoadWarning))
            {
                SetStatus(extensionStore.LoadWarning);
            }
            else if (!string.IsNullOrEmpty(modelProfileStore.LoadWarning))
            {
                SetStatus(modelProfileStore.LoadWarning);
            }
        }

        private void InitializeWindow()
        {
            Text = WindowTitle;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 540);
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(
                Math.Max(
                    MinimumSize.Width,
                    Math.Min(1280, workingArea.Width - 32)),
                Math.Max(
                    MinimumSize.Height,
                    Math.Min(760, workingArea.Height - 32)));
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular);
            BackColor = UiTheme.WindowBackground;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    Application.ExecutablePath);
            }
            catch
            {
                // The window remains usable if the shell cannot read the icon.
            }
            KeyPreview = true;

            KeyDown += OnMainKeyDown;
            FormClosing += OnFormClosing;
        }

        private void BuildInterface()
        {
            rootLayout = new TableLayoutPanel();
            rootLayout.Dock = DockStyle.Fill;
            rootLayout.Padding = new Padding(0);
            rootLayout.BackColor = BackColor;
            rootLayout.ColumnCount = 2;
            rootLayout.RowCount = 1;
            rootLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Absolute, 236F));
            rootLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100F));
            Controls.Add(rootLayout);

            rootLayout.Controls.Add(CreateSessionSidebar(), 0, 0);
            rootLayout.Controls.Add(CreateWorkspace(), 1, 0);
            InitializeAuxiliaryDialogs();
            contextSummaryTimer = new System.Windows.Forms.Timer();
            contextSummaryTimer.Interval = 250;
            contextSummaryTimer.Tick += delegate
            {
                contextSummaryTimer.Stop();
                UpdateContextSummary();
            };
            streamFlushTimer = new System.Windows.Forms.Timer();
            streamFlushTimer.Interval = 40;
            streamFlushTimer.Tick += delegate { FlushPendingOutput(); };
            Shown += delegate
            {
                ResizeInputColumns();
                InitializeFileDropTarget();
            };
        }

        private void InitializeAuxiliaryDialogs()
        {
            settingsDialog = new SettingsDialog();
            endpointTextBox = settingsDialog.EndpointTextBox;
            apiKeyTextBox = settingsDialog.ApiKeyTextBox;
            modelTextBox = settingsDialog.ModelTextBox;
            testConnectionButton = settingsDialog.TestConnectionButton;
            extensionsButton = settingsDialog.ExtensionsButton;
            backupSessionsButton = settingsDialog.BackupSessionsButton;
            restoreSessionsButton = settingsDialog.RestoreSessionsButton;

            endpointTextBox.TextChanged += OnConnectionSettingChanged;
            apiKeyTextBox.TextChanged += OnConnectionSettingChanged;
            modelTextBox.TextChanged += OnConnectionSettingChanged;
            testConnectionButton.Click += OnTestConnectionClick;
            settingsDialog.ModelProfilesButton.Click += OnModelProfilesClick;
            extensionsButton.Click += OnExtensionsClick;
            backupSessionsButton.Click += OnBackupSessionsClick;
            restoreSessionsButton.Click += OnRestoreSessionsClick;
            settingsDialog.UninstallButton.Click += OnUninstallClick;
            settingsDialog.FormClosing += OnSettingsDialogClosing;

            pathInputDialog = new PathInputDialog();
            pathTextBox = pathInputDialog.PathsTextBox;
            readPathButton = pathInputDialog.ReadPathButton;
        }

        private void OnSettingsDialogClosing(
            object sender,
            FormClosingEventArgs args)
        {
            if (!isClosing && args != null &&
                args.CloseReason == CloseReason.UserClosing &&
                connectionTestCancellation != null)
            {
                args.Cancel = true;
                SetStatus("连接测试正在运行，请等待完成后再关闭设置。");
            }
        }

        private Control CreateSessionSidebar()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = UiTheme.SidebarBackground;
            panel.BorderStyle = BorderStyle.FixedSingle;
            panel.Padding = new Padding(12);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.RowCount = 5;
            layout.ColumnCount = 1;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

            Label brand = new Label();
            brand.Text = "FILEPROMPT AI";
            brand.ForeColor = UiTheme.TextPrimary;
            brand.Font = new Font(Font, FontStyle.Bold);
            brand.Dock = DockStyle.Fill;
            brand.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(brand, 0, 0);

            newSessionButton = CreateButton("新会话", 200);
            newSessionButton.Dock = DockStyle.Fill;
            newSessionButton.BackColor = UiTheme.Accent;
            newSessionButton.ForeColor = Color.White;
            newSessionButton.FlatStyle = FlatStyle.Flat;
            newSessionButton.FlatAppearance.BorderSize = 0;
            newSessionButton.FlatAppearance.MouseOverBackColor =
                UiTheme.AccentHover;
            newSessionButton.FlatAppearance.MouseDownBackColor =
                UiTheme.AccentPressed;
            newSessionButton.Click += OnNewSessionClick;
            layout.Controls.Add(newSessionButton, 0, 1);

            TableLayoutPanel searchLayout = new TableLayoutPanel();
            searchLayout.Dock = DockStyle.Fill;
            searchLayout.ColumnCount = 2;
            searchLayout.RowCount = 1;
            searchLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Absolute, 40F));
            searchLayout.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100F));

            Label searchLabel = new Label();
            searchLabel.Text = "搜索";
            searchLabel.Dock = DockStyle.Fill;
            searchLabel.TextAlign = ContentAlignment.MiddleLeft;
            searchLabel.ForeColor = UiTheme.TextSecondary;

            sessionSearchTextBox = CreateInputBox();
            sessionSearchTextBox.AccessibleName = "搜索会话";
            sessionSearchTextBox.Margin = new Padding(2, 7, 0, 6);
            sessionSearchTimer = new System.Windows.Forms.Timer();
            sessionSearchTimer.Interval = 180;
            sessionSearchTimer.Tick += delegate
            {
                sessionSearchTimer.Stop();
                RefreshSessionList();
            };
            sessionSearchTextBox.TextChanged += delegate
            {
                sessionSearchTimer.Stop();
                sessionSearchTimer.Start();
            };
            searchLayout.Controls.Add(searchLabel, 0, 0);
            searchLayout.Controls.Add(sessionSearchTextBox, 1, 0);
            layout.Controls.Add(searchLayout, 0, 2);

            sessionListBox = new ListBox();
            sessionListBox.Dock = DockStyle.Fill;
            sessionListBox.BorderStyle = BorderStyle.None;
            sessionListBox.BackColor = UiTheme.SidebarBackground;
            sessionListBox.ForeColor = UiTheme.TextPrimary;
            sessionListBox.IntegralHeight = false;
            sessionListBox.DrawMode = DrawMode.OwnerDrawFixed;
            sessionListBox.ItemHeight = 52;
            sessionListBox.DrawItem += OnSessionDrawItem;
            sessionListBox.SelectedIndexChanged += OnSessionSelected;

            renameSessionButton = CreateButton("重命名", 96);
            renameSessionButton.Click += OnRenameSessionClick;
            deleteSessionButton = CreateButton("删除", 96);
            deleteSessionButton.ForeColor = UiTheme.Danger;
            deleteSessionButton.Click += OnDeleteSessionClick;

            ContextMenuStrip sessionMenu = new ContextMenuStrip();
            ToolStripMenuItem renameItem =
                new ToolStripMenuItem("重命名会话...");
            renameItem.Click += OnRenameSessionClick;
            ToolStripMenuItem deleteItem =
                new ToolStripMenuItem("删除当前会话...");
            deleteItem.ForeColor = UiTheme.Danger;
            deleteItem.Click += OnDeleteSessionClick;
            sessionMenu.Items.Add(renameItem);
            sessionMenu.Items.Add(deleteItem);
            sessionMenu.Opening += delegate
            {
                bool hasSession = conversationStore.CurrentSession != null;
                renameItem.Enabled = hasSession && !IsBusy;
                deleteItem.Enabled = hasSession &&
                    conversationStore.Sessions.Count > 1 && !IsBusy;
            };
            sessionListBox.ContextMenuStrip = sessionMenu;
            layout.Controls.Add(sessionListBox, 0, 3);

            TableLayoutPanel actions = new TableLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.ColumnCount = 1;
            actions.RowCount = 1;
            actions.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100F));
            settingsButton = CreateButton("设置", 120);
            settingsButton.Dock = DockStyle.Fill;
            settingsButton.AccessibleName = "打开应用设置";
            settingsButton.Click += delegate { ShowSettingsDialog(); };
            actions.Controls.Add(settingsButton, 0, 0);
            layout.Controls.Add(actions, 0, 4);

            panel.Controls.Add(layout);
            return panel;
        }

        private Control CreateWorkspace()
        {
            workspaceLayout = new TableLayoutPanel();
            workspaceLayout.Dock = DockStyle.Fill;
            workspaceLayout.Padding = new Padding(16, 10, 16, 6);
            workspaceLayout.ColumnCount = 1;
            workspaceLayout.RowCount = 3;
            workspaceLayout.RowStyles.Add(
                new RowStyle(SizeType.Absolute, 50F));
            workspaceLayout.RowStyles.Add(
                new RowStyle(SizeType.Percent, 100F));
            workspaceLayout.RowStyles.Add(
                new RowStyle(SizeType.Absolute, 24F));

            workspaceLayout.Controls.Add(CreateHeader(), 0, 0);
            workspaceLayout.Controls.Add(CreateConversationArea(), 0, 1);
            workspaceLayout.Controls.Add(CreateStatusBar(), 0, 2);
            return workspaceLayout;
        }

        private Control CreateHeader()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = UiTheme.WindowBackground;

            sessionTitleLabel = new Label();
            sessionTitleLabel.Text = "新会话";
            sessionTitleLabel.ForeColor = UiTheme.TextPrimary;
            sessionTitleLabel.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
            sessionTitleLabel.AutoSize = false;
            sessionTitleLabel.Location = new Point(2, 2);
            sessionTitleLabel.Size = new Size(520, 24);
            sessionTitleLabel.AutoEllipsis = true;
            sessionTitleLabel.Anchor = AnchorStyles.Left |
                AnchorStyles.Top | AnchorStyles.Right;

            connectionStatusLabel = new Label();
            connectionStatusLabel.Text = "未连接";
            connectionStatusLabel.ForeColor = UiTheme.TextMuted;
            connectionStatusLabel.AutoSize = false;
            connectionStatusLabel.Location = new Point(3, 27);
            connectionStatusLabel.Size = new Size(520, 19);
            connectionStatusLabel.AutoEllipsis = true;
            connectionStatusLabel.Anchor = AnchorStyles.Left |
                AnchorStyles.Top | AnchorStyles.Right;

            panel.Resize += delegate
            {
                int width = Math.Max(40, panel.ClientSize.Width - 6);
                sessionTitleLabel.Width = width;
                connectionStatusLabel.Width = width;
            };

            panel.Controls.Add(sessionTitleLabel);
            panel.Controls.Add(connectionStatusLabel);

            return panel;
        }

        private Control CreateConversationArea()
        {
            conversationArea = new TableLayoutPanel();
            conversationArea.Dock = DockStyle.Fill;
            conversationArea.ColumnCount = 1;
            conversationArea.RowCount = 2;
            conversationArea.RowStyles.Add(
                new RowStyle(SizeType.Percent, 100F));
            composerAreaRowStyle = new RowStyle(
                SizeType.Absolute,
                CompactComposerHeight);
            conversationArea.RowStyles.Add(composerAreaRowStyle);

            conversationArea.Controls.Add(CreateOutputPanel(), 0, 0);
            conversationArea.Controls.Add(CreatePromptPanel(), 0, 1);
            conversationArea.Resize += delegate
            {
                UpdateConversationAreaRows();
            };
            return conversationArea;
        }

        private void UpdateConversationAreaRows()
        {
            if (conversationArea == null || composerAreaRowStyle == null ||
                attachmentTrayRowStyle == null || isUpdatingConversationRows)
            {
                return;
            }

            isUpdatingConversationRows = true;
            try
            {
                int height = conversationArea.ClientSize.Height;
                bool hasInputItems = inputItems != null &&
                    inputItems.Count > 0;
                float desiredComposerHeight = hasInputItems
                    ? ExpandedComposerHeight
                    : CompactComposerHeight;
                desiredComposerHeight = Math.Min(
                    desiredComposerHeight,
                    Math.Max(118F, height - MinimumOutputAreaHeight));
                if (Math.Abs(
                    composerAreaRowStyle.Height -
                        desiredComposerHeight) >= 0.5F)
                {
                    composerAreaRowStyle.Height = desiredComposerHeight;
                }

                attachmentTrayRowStyle.Height = hasInputItems
                    ? Math.Max(58F, desiredComposerHeight - 154F)
                    : 0F;
                if (inputListView != null)
                {
                    inputListView.Visible = hasInputItems;
                }
                if (composerPanel != null)
                {
                    composerPanel.AccessibleDescription = hasInputItems
                        ? "已添加 " + inputItems.Count + " 项资料"
                        : "未添加资料";
                }
            }
            finally
            {
                isUpdatingConversationRows = false;
            }
        }

        private Control CreateInputsPanel()
        {
            inputListView = new ListView();
            inputListView.Dock = DockStyle.Fill;
            inputListView.View = View.Details;
            inputListView.FullRowSelect = true;
            inputListView.HideSelection = false;
            inputListView.MultiSelect = true;
            inputListView.BackColor = UiTheme.InputBackground;
            inputListView.AccessibleName = "已添加内容";
            inputListView.Columns.Add("名称", 260);
            inputListView.Columns.Add("类型", 76);
            inputListView.Columns.Add("大小", 94);
            inputListView.Columns.Add("说明", 340);
            inputListView.Resize += delegate { ResizeInputColumns(); };
            inputListView.DoubleClick += OnPreviewInputItem;
            inputListView.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Delete)
                {
                    RemoveSelectedItems();
                    args.Handled = true;
                }
                else if (args.KeyCode == Keys.Enter)
                {
                    OnPreviewInputItem(sender, EventArgs.Empty);
                    args.Handled = true;
                }
            };

            ContextMenuStrip itemMenu = new ContextMenuStrip();
            ToolStripMenuItem previewItem =
                new ToolStripMenuItem("预览选中资料");
            previewItem.Click += OnPreviewInputItem;
            ToolStripMenuItem removeItem =
                new ToolStripMenuItem("移除选中资料");
            removeItem.Click += OnRemoveClick;
            ToolStripMenuItem clearItem =
                new ToolStripMenuItem("清空全部资料");
            clearItem.Click += OnClearClick;
            itemMenu.Items.Add(previewItem);
            itemMenu.Items.Add(removeItem);
            itemMenu.Items.Add(new ToolStripSeparator());
            itemMenu.Items.Add(clearItem);
            itemMenu.Opening += delegate
            {
                bool hasSelection = inputListView.SelectedItems.Count > 0;
                previewItem.Enabled = hasSelection;
                removeItem.Enabled = hasSelection && !IsBusy;
                clearItem.Enabled = inputListView.Items.Count > 0 && !IsBusy;
            };
            inputListView.ContextMenuStrip = itemMenu;
            return inputListView;
        }

        private void ResizeInputColumns()
        {
            if (inputListView == null || inputListView.Columns.Count < 4)
            {
                return;
            }

            int available = Math.Max(
                500,
                inputListView.ClientSize.Width - 32);
            int typeWidth = 76;
            int sizeWidth = 94;
            int nameWidth = Math.Max(170, (int)(available * 0.31F));
            int noteWidth = Math.Max(
                160,
                available - nameWidth - typeWidth - sizeWidth);
            inputListView.Columns[0].Width = nameWidth;
            inputListView.Columns[1].Width = typeWidth;
            inputListView.Columns[2].Width = sizeWidth;
            inputListView.Columns[3].Width = noteWidth;
            if (inputListView.IsHandleCreated)
            {
                inputListView.Invalidate();
            }
        }

        private Control CreatePromptPanel()
        {
            promptGroup = new GroupBox();
            promptGroup.Text = "输入消息";
            promptGroup.Dock = DockStyle.Fill;
            promptGroup.ForeColor = UiTheme.TextSecondary;
            promptGroup.Padding = new Padding(8, 4, 8, 7);
            promptGroup.Margin = new Padding(0, 8, 0, 0);
            composerPanel = promptGroup;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            attachmentTrayRowStyle = new RowStyle(SizeType.Absolute, 0F);
            layout.RowStyles.Add(attachmentTrayRowStyle);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37F));

            promptTextBox = new RichTextBox();
            promptTextBox.Dock = DockStyle.Fill;
            promptTextBox.BorderStyle = BorderStyle.FixedSingle;
            promptTextBox.AcceptsTab = false;
            promptTextBox.DetectUrls = false;
            promptTextBox.BackColor = Color.White;
            promptTextBox.AccessibleName = "文字描述或指令";
            promptTextBox.TextChanged += delegate
            {
                ScheduleContextSummaryUpdate();
            };
            promptTextBox.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter &&
                    IsEnterSendShortcut(args.Control, args.Shift))
                {
                    args.SuppressKeyPress = true;
                    StartGeneration();
                }
            };

            TableLayoutPanel actions = new TableLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.ColumnCount = 2;
            actions.RowCount = 1;
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            FlowLayoutPanel composerActions = new FlowLayoutPanel();
            composerActions.Dock = DockStyle.Fill;
            composerActions.WrapContents = false;
            composerActions.FlowDirection = FlowDirection.LeftToRight;
            composerActions.Margin = new Padding(0);

            ContextMenuStrip addMenu = new ContextMenuStrip();
            ToolStripMenuItem chooseFileItem =
                new ToolStripMenuItem("选择文件...");
            chooseFileItem.Click += OnAddFileClick;
            ToolStripMenuItem pasteItem =
                new ToolStripMenuItem("粘贴剪贴板内容");
            pasteItem.Click += OnPasteClick;
            ToolStripMenuItem pathItem =
                new ToolStripMenuItem("从路径添加...");
            pathItem.Click += delegate { ShowPathInputDialog(); };
            addMenu.Items.Add(chooseFileItem);
            addMenu.Items.Add(pasteItem);
            addMenu.Items.Add(pathItem);
            addMenu.Opening += delegate
            {
                bool enabled = !IsBusy;
                chooseFileItem.Enabled = enabled;
                pasteItem.Enabled = enabled;
                pathItem.Enabled = enabled;
            };

            addFileButton = CreateButton("+ 添加", 72);
            addFileButton.AccessibleName = "添加文件、路径或剪贴板内容";
            addFileButton.ContextMenuStrip = addMenu;
            addFileButton.Click += delegate
            {
                addMenu.Show(
                    addFileButton,
                    new Point(0, addFileButton.Height));
            };

            fileDropTargetPanel = new Panel();
            fileDropTargetPanel.Size = new Size(132, 27);
            fileDropTargetPanel.Margin = new Padding(3, 3, 3, 3);
            fileDropTargetPanel.BackColor = UiTheme.PanelAltBackground;
            fileDropTargetPanel.BorderStyle = BorderStyle.FixedSingle;
            fileDropTargetPanel.Cursor = Cursors.Hand;
            fileDropTargetPanel.AccessibleName =
                "拖入文件到这里，或单击选择文件";
            fileDropTargetPanel.DragEnter += OnDragEnter;
            fileDropTargetPanel.DragDrop += OnDragDrop;
            fileDropTargetPanel.Click += OnAddFileClick;
            fileDropTargetPanel.Paint += delegate(
                object sender,
                PaintEventArgs args)
            {
                TextRenderer.DrawText(
                    args.Graphics,
                    "拖入文件",
                    Font,
                    fileDropTargetPanel.ClientRectangle,
                    UiTheme.TextSecondary,
                    TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix);
            };
            if (contextSummaryToolTip != null)
            {
                contextSummaryToolTip.SetToolTip(
                    fileDropTargetPanel,
                    "把文件拖到这里，或单击打开文件选择器。");
                contextSummaryToolTip.SetToolTip(
                    addFileButton,
                    "选择文件、粘贴剪贴板内容，或输入明确的文件路径。");
            }

            promptActionsButton = CreateButton("快捷指令", 84);
            promptActionsButton.AccessibleName = "应用快捷指令模板";
            promptActionsButton.ContextMenuStrip = CreatePromptActionsMenu();
            promptActionsButton.Click += delegate
            {
                promptActionsButton.ContextMenuStrip.Show(
                    promptActionsButton,
                    new Point(0, promptActionsButton.Height));
            };
            promptActionsButton.Anchor = AnchorStyles.Left;

            FlowLayoutPanel sendActions = new FlowLayoutPanel();
            sendActions.Dock = DockStyle.Fill;
            sendActions.AutoSize = true;
            sendActions.WrapContents = false;
            sendActions.FlowDirection = FlowDirection.LeftToRight;
            sendActions.Margin = new Padding(0);

            stopButton = CreateButton("停止", 64);
            stopButton.Enabled = false;
            stopButton.Click += delegate
            {
                if (generationCancellation != null)
                {
                    generationCancellation.Cancel();
                }
            };

            generateButton = CreateButton("发送", 80);
            generateButton.BackColor = UiTheme.Accent;
            generateButton.ForeColor = Color.White;
            generateButton.FlatStyle = FlatStyle.Flat;
            generateButton.FlatAppearance.BorderSize = 0;
            generateButton.FlatAppearance.MouseOverBackColor =
                UiTheme.AccentHover;
            generateButton.FlatAppearance.MouseDownBackColor =
                UiTheme.AccentPressed;
            generateButton.Click += delegate { StartGeneration(); };
            generateButton.ContextMenuStrip = CreateSendShortcutMenu();
            generateButton.AccessibleName = "发送（右键可配置快捷键）";

            composerActions.Controls.Add(addFileButton);
            composerActions.Controls.Add(fileDropTargetPanel);
            composerActions.Controls.Add(promptActionsButton);
            sendActions.Controls.Add(stopButton);
            sendActions.Controls.Add(generateButton);
            actions.Controls.Add(composerActions, 0, 0);
            actions.Controls.Add(sendActions, 1, 0);
            layout.Controls.Add(CreateInputsPanel(), 0, 0);
            layout.Controls.Add(promptTextBox, 0, 1);
            layout.Controls.Add(actions, 0, 2);
            promptGroup.Controls.Add(layout);
            return promptGroup;
        }

        private Control CreateOutputPanel()
        {
            GroupBox group = new GroupBox();
            group.Text = "对话";
            group.Dock = DockStyle.Fill;
            group.ForeColor = UiTheme.TextSecondary;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.ColumnCount = 2;
            header.RowCount = 1;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            contextSummaryLabel = new Label();
            contextSummaryLabel.Text =
                "0 条消息 · 资料 0 项 · 历史 0 · 本轮 0 / 48,000";
            contextSummaryLabel.Dock = DockStyle.Fill;
            contextSummaryLabel.TextAlign = ContentAlignment.MiddleLeft;
            contextSummaryLabel.ForeColor = UiTheme.TextMuted;
            contextSummaryLabel.Font = new Font(Font.FontFamily, 8.5F);
            contextSummaryLabel.AutoSize = false;
            contextSummaryLabel.AutoEllipsis = true;
            contextSummaryLabel.UseCompatibleTextRendering = false;
            contextSummaryLabel.Padding = new Padding(4, 0, 4, 0);
            contextSummaryLabel.AccessibleName = "当前会话上下文摘要";
            contextSummaryToolTip = new ToolTip();
            contextSummaryToolTip.InitialDelay = 300;
            contextSummaryToolTip.ReshowDelay = 100;
            contextSummaryToolTip.AutoPopDelay = 15000;
            contextSummaryToolTip.ShowAlways = true;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.AutoSize = true;
            buttons.Padding = new Padding(2, 1, 0, 0);
            buttons.WrapContents = false;
            buttons.FlowDirection = FlowDirection.LeftToRight;

            copyOutputButton = CreateButton("复制回复", 76);
            copyOutputButton.Click += OnCopyOutputClick;
            saveOutputButton = CreateButton("导出文件", 86);
            saveOutputButton.AccessibleName = "导出回复或会话文件";
            exportMenu = CreateExportMenu();
            saveOutputButton.ContextMenuStrip = exportMenu;
            saveOutputButton.Click += delegate
            {
                exportMenu.Show(
                    saveOutputButton,
                    new Point(0, saveOutputButton.Height));
            };

            buttons.Controls.Add(copyOutputButton);
            buttons.Controls.Add(saveOutputButton);
            header.Controls.Add(contextSummaryLabel, 0, 0);
            header.Controls.Add(buttons, 1, 0);

            outputTextBox = new RichTextBox();
            outputTextBox.Dock = DockStyle.Fill;
            outputTextBox.ReadOnly = true;
            outputTextBox.BackColor = Color.White;
            outputTextBox.BorderStyle = BorderStyle.None;
            outputTextBox.DetectUrls = true;
            outputTextBox.Font = new Font("Microsoft YaHei", 9F);
            outputTextBox.HideSelection = false;
            outputTextBox.AccessibleName = "当前会话对话记录";
            outputTextBox.MouseWheel += delegate
            {
                if (activeGenerationSequence != 0)
                {
                    followStreamTail = false;
                }
            };
            outputTextBox.MouseDown += delegate
            {
                if (activeGenerationSequence != 0)
                {
                    followStreamTail = false;
                }
            };
            outputTextBox.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (activeGenerationSequence == 0)
                {
                    return;
                }

                if (args.KeyCode == Keys.End && args.Control)
                {
                    followStreamTail = true;
                }
                else if (args.KeyCode == Keys.Up ||
                    args.KeyCode == Keys.PageUp || args.KeyCode == Keys.Home)
                {
                    followStreamTail = false;
                }
            };

            ContextMenuStrip outputMenu = new ContextMenuStrip();
            ToolStripMenuItem copySelectionItem =
                new ToolStripMenuItem("复制选中内容");
            copySelectionItem.Click += delegate
            {
                if (outputTextBox.SelectionLength > 0)
                {
                    outputTextBox.Copy();
                    SetStatus("选中内容已复制到剪贴板");
                }
            };
            ToolStripMenuItem copyLatestItem =
                new ToolStripMenuItem("复制最新回复");
            copyLatestItem.Click += OnCopyOutputClick;
            ToolStripMenuItem loadPromptItem =
                new ToolStripMenuItem("\u8f7d\u5165\u4e0a\u4e00\u6761\u6307\u4ee4");
            loadPromptItem.Click += delegate { LoadLastPromptForEditing(); };
            ToolStripMenuItem selectAllItem =
                new ToolStripMenuItem("全选");
            selectAllItem.Click += delegate { outputTextBox.SelectAll(); };
            outputMenu.Items.Add(copySelectionItem);
            outputMenu.Items.Add(copyLatestItem);
            outputMenu.Items.Add(new ToolStripSeparator());
            outputMenu.Items.Add(loadPromptItem);
            outputMenu.Items.Add(new ToolStripSeparator());
            outputMenu.Items.Add(selectAllItem);
            outputMenu.Opening += delegate
            {
                copySelectionItem.Enabled = outputTextBox.SelectionLength > 0;
                copyLatestItem.Enabled =
                    !string.IsNullOrEmpty(GetLatestAssistantOutput());

                loadPromptItem.Enabled = !IsBusy && HasLastUserMessage();            };
            outputTextBox.ContextMenuStrip = outputMenu;

            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(outputTextBox, 0, 1);
            group.Controls.Add(layout);
            return group;
        }

        private Control CreateStatusBar()
        {
            StatusStrip strip = new StatusStrip();
            strip.Dock = DockStyle.Fill;
            strip.SizingGrip = false;
            strip.BackColor = UiTheme.WindowBackground;

            statusLabel = new ToolStripStatusLabel();
            statusLabel.Text = "就绪";
            statusLabel.ForeColor = UiTheme.TextSecondary;
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;

            progressBar = new ToolStripProgressBar();
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.MarqueeAnimationSpeed = 25;
            progressBar.Visible = false;
            progressBar.Width = 120;

            strip.Items.Add(statusLabel);
            strip.Items.Add(progressBar);
            return strip;
        }

        private static TextBox CreateInputBox()
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 3, 3, 3);
            box.BackColor = Color.White;
            return box;
        }

        private static Button CreateButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 27;
            button.Margin = new Padding(3);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = UiTheme.BorderColor;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor =
                UiTheme.ButtonHover;
            button.FlatAppearance.MouseDownBackColor =
                UiTheme.ButtonPressed;
            button.BackColor = UiTheme.ButtonBackground;
            button.ForeColor = UiTheme.ButtonText;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private void ShowSettingsDialog()
        {
            ShowSettingsDialog(null, string.Empty);
        }

        private void ShowSettingsDialog(
            Control focusControl,
            string validationMessage)
        {
            if (settingsDialog == null || settingsDialog.IsDisposed ||
                isClosing)
            {
                return;
            }

            if (IsBusy)
            {
                SetStatus("当前有任务运行，请完成或停止后再修改设置。");
                return;
            }

            string previousEndpoint = endpointTextBox.Text;
            string previousKey = apiKeyTextBox.Text;
            string previousModel = modelTextBox.Text;
            string previousShortcut = sendShortcutMode ?? "Both";
            settingsDialog.SendShortcutMode = previousShortcut;
            settingsDialog.SetContextSummary(BuildContextSummary());
            settingsDialog.SetExtensionSummary(BuildExtensionSummary());
            settingsDialog.PrepareForOpen(focusControl, validationMessage);
            settingsDialog.DialogResult = DialogResult.None;

            DialogResult result = settingsDialog.ShowDialog(this);
            if (result == DialogResult.OK)
            {
                sendShortcutMode = settingsDialog.SendShortcutMode;
                UpdatePromptHint();
                UpdateSendShortcutMenuChecks();
                SaveSettings();
                connectionStatusLabel.Text = BuildConnectionStatus();
                UpdateContextSummary();
                SetStatus("设置已保存到本机当前 Windows 用户。");
                return;
            }

            endpointTextBox.Text = previousEndpoint;
            apiKeyTextBox.Text = previousKey;
            modelTextBox.Text = previousModel;
            sendShortcutMode = previousShortcut;
            settingsDialog.SendShortcutMode = previousShortcut;
            UpdatePromptHint();
            UpdateSendShortcutMenuChecks();
            SaveSettings();
            connectionStatusLabel.Text = BuildConnectionStatus();
            UpdateContextSummary();
        }

        private void ShowPathInputDialog()
        {
            if (pathInputDialog == null || pathInputDialog.IsDisposed ||
                IsBusy || isClosing)
            {
                return;
            }

            pathInputDialog.DialogResult = DialogResult.None;
            if (pathInputDialog.ShowDialog(this) == DialogResult.OK)
            {
                OnReadPathClick(readPathButton, EventArgs.Empty);
            }
        }

        private IWin32Window GetSettingsActionOwner()
        {
            return settingsDialog != null &&
                !settingsDialog.IsDisposed &&
                settingsDialog.Visible
                    ? (IWin32Window)settingsDialog
                    : this;
        }

        private bool HasCompleteConnectionSettings()
        {
            return endpointTextBox != null &&
                apiKeyTextBox != null &&
                modelTextBox != null &&
                !string.IsNullOrWhiteSpace(endpointTextBox.Text) &&
                !string.IsNullOrWhiteSpace(apiKeyTextBox.Text) &&
                !string.IsNullOrWhiteSpace(modelTextBox.Text);
        }

        private bool IsBusy
        {
            get
            {
                return generationCancellation != null ||
                    connectionTestCancellation != null ||
                    isAddingFiles;
            }
        }

        private void OnMainKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Control && args.KeyCode == Keys.N &&
                !IsBusy)
            {
                OnNewSessionClick(this, EventArgs.Empty);
                args.SuppressKeyPress = true;
            }
            else if (args.Control && args.KeyCode == Keys.F)
            {
                if (sessionSearchTextBox != null)
                {
                    sessionSearchTextBox.Focus();
                    sessionSearchTextBox.SelectAll();
                }

                args.SuppressKeyPress = true;
            }
            else if (args.KeyCode == Keys.Escape &&
                generationCancellation != null)
            {
                generationCancellation.Cancel();
                args.SuppressKeyPress = true;
            }
        }

        private void OnSessionDrawItem(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0 || args.Index >= sessionListBox.Items.Count)
            {
                return;
            }

            ConversationSession session =
                sessionListBox.Items[args.Index] as ConversationSession;
            if (session == null)
            {
                return;
            }

            bool selected =
                (args.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color background = selected
                ? UiTheme.SelectionBackground
                : sessionListBox.BackColor;
            Color titleColor = selected
                ? UiTheme.SelectionText
                : UiTheme.ButtonText;
            Color metaColor = selected
                ? UiTheme.SelectionMuted
                : UiTheme.TextMuted;

            using (SolidBrush brush = new SolidBrush(background))
            {
                args.Graphics.FillRectangle(brush, args.Bounds);
            }

            Rectangle titleBounds = new Rectangle(
                args.Bounds.Left + 8,
                args.Bounds.Top + 5,
                Math.Max(20, args.Bounds.Width - 16),
                20);
            Rectangle metaBounds = new Rectangle(
                args.Bounds.Left + 8,
                args.Bounds.Top + 26,
                Math.Max(20, args.Bounds.Width - 16),
                16);
            using (Font titleFont = new Font(Font, FontStyle.Bold))
            using (Font metaFont = new Font(
                Font.FontFamily,
                Math.Max(8F, Font.Size - 1F),
                FontStyle.Regular))
            {
                TextRenderer.DrawText(
                    args.Graphics,
                    session.Title ?? "新会话",
                    titleFont,
                    titleBounds,
                    titleColor,
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.VerticalCenter);
                int messageCount = session.Messages == null
                    ? 0
                    : session.Messages.Count;
                DateTime updated = session.UpdatedAt.Kind == DateTimeKind.Utc
                    ? session.UpdatedAt.ToLocalTime()
                    : session.UpdatedAt;
                string time = updated.Date == DateTime.Today
                    ? updated.ToString("HH:mm")
                    : updated.ToString("MM-dd");
                TextRenderer.DrawText(
                    args.Graphics,
                    time + "  ·  " + messageCount + " 条消息",
                    metaFont,
                    metaBounds,
                    metaColor,
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.VerticalCenter);
            }

            if (selected)
            {
                using (SolidBrush accentBrush = new SolidBrush(UiTheme.Accent))
                {
                    args.Graphics.FillRectangle(
                        accentBrush,
                        new Rectangle(args.Bounds.Left, args.Bounds.Top, 3, args.Bounds.Height));
                }
                args.DrawFocusRectangle();
            }
        }

        private bool SessionMatchesFilter(
            ConversationSession session,
            string filter)
        {
            if (session == null || string.IsNullOrWhiteSpace(filter))
            {
                return session != null;
            }

            string query = filter.Trim();
            if ((session.Title ?? string.Empty).IndexOf(
                query,
                StringComparison.CurrentCultureIgnoreCase) >= 0)
            {
                return true;
            }

            if (session.Messages == null)
            {
                return false;
            }

            const int maximumInspectedCharacters = 20000;
            int inspectedCharacters = 0;
            for (int index = session.Messages.Count - 1;
                index >= 0 &&
                    inspectedCharacters < maximumInspectedCharacters;
                index--)
            {
                ConversationMessage message = session.Messages[index];
                string content = message == null
                    ? string.Empty
                    : (message.Content ?? string.Empty);
                int inspectLength = Math.Min(
                    content.Length,
                    maximumInspectedCharacters - inspectedCharacters);
                inspectedCharacters += inspectLength;
                if (inspectLength > 0 &&
                    content.IndexOf(
                        query,
                        0,
                        inspectLength,
                        StringComparison.CurrentCultureIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void SaveCurrentDraft()
        {
            ConversationSession session = conversationStore.CurrentSession;
            if (session == null || promptTextBox == null)
            {
                return;
            }

            string prompt = promptTextBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt) && inputItems.Count == 0)
            {
                sessionDrafts.Remove(session.Id);
                return;
            }

            SessionDraft draft = new SessionDraft();
            draft.Prompt = prompt;
            draft.Items = new List<InputItem>(inputItems);
            sessionDrafts[session.Id] = draft;
        }

        private void RestoreCurrentDraft()
        {
            ConversationSession session = conversationStore.CurrentSession;
            promptTextBox.Clear();
            inputItems.Clear();
            inputListView.Items.Clear();
            if (session != null)
            {
                SessionDraft draft;
                if (sessionDrafts.TryGetValue(session.Id, out draft) &&
                    draft != null)
                {
                    promptTextBox.Text = draft.Prompt ?? string.Empty;
                    if (draft.Items != null)
                    {
                        foreach (InputItem item in draft.Items)
                        {
                            AddInputItem(item);
                        }
                    }
                }
            }

            UpdateInputStatus();
        }

        private void ClearDraft(string sessionId)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                sessionDrafts.Remove(sessionId);
            }
        }

        private void EnsureInitialSession()
        {
            if (conversationStore.Sessions.Count == 0)
            {
                if (!conversationStore.IsWriteBlocked)
                {
                    conversationStore.CreateSession("新会话");
                }

                return;
            }

            if (conversationStore.CurrentSession == null)
            {
                conversationStore.SelectSession(
                    conversationStore.Sessions[0].Id);
            }
        }

        private void RefreshSessionList()
        {
            if (sessionListBox == null)
            {
                return;
            }

            string selectedId = conversationStore.CurrentSessionId;
            isLoadingSession = true;
            sessionListBox.BeginUpdate();
            try
            {
                sessionListBox.Items.Clear();
                string filter = sessionSearchTextBox == null
                    ? string.Empty
                    : sessionSearchTextBox.Text;
                IEnumerable<ConversationSession> visibleSessions =
                    conversationStore.Sessions
                        .Where(session => SessionMatchesFilter(session, filter))
                        .OrderByDescending(session => session.UpdatedAt);
                foreach (ConversationSession session in visibleSessions)
                {
                    sessionListBox.Items.Add(session);
                }

                for (int i = 0; i < sessionListBox.Items.Count; i++)
                {
                    ConversationSession item =
                        sessionListBox.Items[i] as ConversationSession;
                    if (item != null && string.Equals(
                        item.Id,
                        selectedId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        sessionListBox.SelectedIndex = i;
                        break;
                    }
                }
            }
            finally
            {
                sessionListBox.EndUpdate();
                isLoadingSession = false;
            }

            UpdateSessionButtons();
        }

        private void LoadCurrentSession()
        {
            ConversationSession session = conversationStore.CurrentSession;
            if (session == null)
            {
                return;
            }

            sessionTitleLabel.Text = session.Title;
            connectionStatusLabel.Text = BuildConnectionStatus();
            RenderConversation(session);
            UpdateContextSummary();
            UpdateSessionButtons();
        }

        private void RenderConversation(ConversationSession session)
        {
            outputTextBox.Clear();
            renderedSessionId = session == null ? null : session.Id;
            renderedMessageCount = session == null || session.Messages == null
                ? 0
                : session.Messages.Count;
            renderedSessionCharacterEstimate =
                CalculateSessionCharacterEstimate(session);
            streamedTurnStart = -1;
            streamedContentStart = -1;
            if (session == null || session.Messages == null ||
                session.Messages.Count == 0)
            {
                AppendEmptyConversation();
                UpdateOutputButtons(generationCancellation != null);
                UpdateContextSummary();
                return;
            }

            foreach (ConversationMessage message in session.Messages)
            {
                if (message == null)
                {
                    continue;
                }

                string role = message.Role == "assistant"
                    ? "模型"
                    : (message.Role == "system" ? "系统" : "你");
                Color roleColor = message.Role == "assistant"
                    ? UiTheme.RoleAssistant
                    : (message.Role == "system"
                        ? UiTheme.RoleSystem
                        : UiTheme.RoleUser);
                AppendTranscriptMessage(
                    role,
                    FormatMessageForDisplay(message),
                    roleColor,
                    string.Equals(
                        message.Role,
                        "assistant",
                        StringComparison.OrdinalIgnoreCase));
            }

            outputTextBox.SelectionStart = outputTextBox.TextLength;
            outputTextBox.ScrollToCaret();
            UpdateOutputButtons(generationCancellation != null);
            UpdateContextSummary();
        }

        private void AppendEmptyConversation()
        {
            outputTextBox.SelectionStart = 0;
            outputTextBox.SelectionColor = UiTheme.Placeholder;
            using (Font emptyFont = new Font(
                outputTextBox.Font,
                FontStyle.Italic))
            {
                outputTextBox.SelectionFont = emptyFont;
                outputTextBox.AppendText(
                    "暂无会话内容。添加资料并输入指令后即可发送。\r\n");
            }

            outputTextBox.SelectionColor = Color.Black;
            outputTextBox.SelectionFont = outputTextBox.Font;
        }

        private void AppendTranscriptMessage(
            string role,
            string content,
            Color roleColor,
            bool renderMarkdown = false)
        {
            AppendTranscriptHeader(role, roleColor);
            string value = (content ?? string.Empty).TrimEnd();
            if (renderMarkdown)
            {
                MarkdownRichTextRenderer.Append(outputTextBox, value);
                outputTextBox.AppendText("\r\n");
            }
            else
            {
                outputTextBox.AppendText(value);
                outputTextBox.AppendText("\r\n\r\n");
            }
        }

        private void AppendTranscriptHeader(string role, Color roleColor)
        {
            outputTextBox.SelectionStart = outputTextBox.TextLength;
            outputTextBox.SelectionColor = roleColor;
            using (Font headerFont = new Font(
                outputTextBox.Font,
                FontStyle.Bold))
            {
                outputTextBox.SelectionFont = headerFont;
                outputTextBox.AppendText("【" + role + "】\r\n");
            }

            outputTextBox.SelectionColor = UiTheme.TextPrimary;
            outputTextBox.SelectionFont = outputTextBox.Font;
        }

        private void PrepareStreamingTurn(
            ConversationSession session,
            string visibleUserMessage)
        {
            int messageCount = session == null || session.Messages == null
                ? 0
                : session.Messages.Count;
            bool currentViewMatches = session != null &&
                string.Equals(
                    renderedSessionId,
                    session.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                renderedMessageCount == messageCount;
            if (!currentViewMatches ||
                (messageCount > 0 && outputTextBox.TextLength == 0))
            {
                RenderConversation(session);
            }

            if (messageCount == 0)
            {
                outputTextBox.Clear();
            }

            streamedTurnStart = outputTextBox.TextLength;
            followStreamTail = true;
            AppendTranscriptMessage(
                "你",
                visibleUserMessage,
                UiTheme.RoleUser);
            AppendTranscriptHeader(
                "模型",
                UiTheme.RoleAssistant);
            streamedContentStart = outputTextBox.TextLength;
            outputTextBox.SelectionStart = outputTextBox.TextLength;
            outputTextBox.ScrollToCaret();
        }

        private void FinalizeStreamingTurn(
            string userContent,
            string assistantContent)
        {
            if (outputTextBox == null || streamedTurnStart < 0 ||
                streamedTurnStart > outputTextBox.TextLength)
            {
                return;
            }

            int previousSelectionStart = outputTextBox.SelectionStart;
            int previousSelectionLength = outputTextBox.SelectionLength;
            NativePoint previousScrollPosition =
                GetRichTextScrollPosition(outputTextBox);
            bool restoreView = !followStreamTail;
            outputTextBox.SuspendLayout();
            try
            {
                outputTextBox.Select(
                    streamedTurnStart,
                    outputTextBox.TextLength - streamedTurnStart);
                outputTextBox.SelectedText = string.Empty;
                outputTextBox.SelectionStart = outputTextBox.TextLength;
                outputTextBox.SelectionLength = 0;
                AppendTranscriptMessage(
                    "你",
                    FormatMessageForDisplay(
                        new ConversationMessage(
                            "user",
                            userContent ?? string.Empty)),
                    UiTheme.RoleUser);
                AppendTranscriptMessage(
                    "模型",
                    assistantContent ?? string.Empty,
                    UiTheme.RoleAssistant,
                    true);
                outputTextBox.SelectionStart = outputTextBox.TextLength;
                outputTextBox.SelectionLength = 0;
                if (followStreamTail)
                {
                    outputTextBox.ScrollToCaret();
                }
                else
                {
                    outputTextBox.Select(
                        Math.Min(
                            previousSelectionStart,
                            outputTextBox.TextLength),
                        Math.Min(
                            previousSelectionLength,
                            Math.Max(
                                0,
                                outputTextBox.TextLength -
                                    previousSelectionStart)));
                }
            }
            finally
            {
                outputTextBox.ResumeLayout(true);
            }

            if (restoreView)
            {
                SetRichTextScrollPosition(
                    outputTextBox,
                    previousScrollPosition);
            }
        }

        private void RemoveStreamingTurnPreview(
            ConversationSession session)
        {
            if (outputTextBox == null || streamedTurnStart < 0 ||
                streamedTurnStart > outputTextBox.TextLength)
            {
                RenderConversation(session);
                return;
            }

            int previousSelectionStart = outputTextBox.SelectionStart;
            int previousSelectionLength = outputTextBox.SelectionLength;
            NativePoint previousScrollPosition =
                GetRichTextScrollPosition(outputTextBox);
            bool restoreView = !followStreamTail;
            outputTextBox.Select(
                streamedTurnStart,
                outputTextBox.TextLength - streamedTurnStart);
            outputTextBox.SelectedText = string.Empty;
            if (outputTextBox.TextLength == 0 &&
                (session == null || session.Messages == null ||
                    session.Messages.Count == 0))
            {
                AppendEmptyConversation();
            }

            outputTextBox.SelectionStart = outputTextBox.TextLength;
            outputTextBox.SelectionLength = 0;
            if (followStreamTail)
            {
                outputTextBox.ScrollToCaret();
            }
            else
            {
                outputTextBox.Select(
                    Math.Min(previousSelectionStart, outputTextBox.TextLength),
                    Math.Min(
                        previousSelectionLength,
                        Math.Max(
                            0,
                            outputTextBox.TextLength -
                                previousSelectionStart)));
            }
            if (restoreView)
            {
                SetRichTextScrollPosition(
                    outputTextBox,
                    previousScrollPosition);
            }
            streamedTurnStart = -1;
            streamedContentStart = -1;
            UpdateOutputButtons(false);
            UpdateContextSummary();
        }

        private static string FormatMessageForDisplay(
            ConversationMessage message)
        {
            if (message == null)
            {
                return string.Empty;
            }

            string content = message.Content ?? string.Empty;
            if (!string.Equals(
                message.Role,
                "user",
                StringComparison.OrdinalIgnoreCase))
            {
                return content;
            }

            const string attachmentMarker =
                "以下资料由用户主动拖入或粘贴后提取";
            int markerIndex = content.IndexOf(
                attachmentMarker,
                StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                string visible = content.Substring(0, markerIndex).Trim();
                return visible +
                    "\r\n\r\n[本轮包含用户已授权的资料，正文已在会话视图中折叠]";
            }

            if (content.Length > MaxDisplayedUserCharacters)
            {
                return content.Substring(0, MaxDisplayedUserCharacters) +
                    "\r\n\r\n[较长的用户内容已在会话视图中折叠]";
            }

            return content;
        }

        private string GetLatestAssistantOutput()
        {
            ConversationSession session = conversationStore.CurrentSession;
            if (session == null || session.Messages == null)
            {
                return string.Empty;
            }

            ConversationMessage latest = session.Messages
                .Where(message => message != null &&
                    string.Equals(
                        message.Role,
                        "assistant",
                        StringComparison.OrdinalIgnoreCase))
                .LastOrDefault();
            return latest == null
                ? string.Empty
                : (latest.Content ?? string.Empty);
        }

        private void UpdateOutputButtons(bool generating)
        {
            if (copyOutputButton == null)
            {
                return;
            }

            bool hasReply = !string.IsNullOrEmpty(GetLatestAssistantOutput());
            copyOutputButton.Enabled = !generating && hasReply;
            saveOutputButton.Enabled = !generating && hasReply;
        }

        private string BuildConnectionStatus()
        {
            string endpoint = endpointTextBox == null
                ? string.Empty
                : endpointTextBox.Text.Trim();
            string key = apiKeyTextBox == null
                ? string.Empty
                : apiKeyTextBox.Text.Trim();
            string model = modelTextBox == null
                ? string.Empty
                : modelTextBox.Text.Trim();
            if (string.IsNullOrEmpty(endpoint) ||
                string.IsNullOrEmpty(key) ||
                string.IsNullOrEmpty(model))
            {
                return "尚未配置完整 · 请填写 URL / Key / 模型";
            }

            return "配置就绪 · " + model;
        }

        private string BuildContextSummary()
        {
            ConversationSession session = conversationStore.CurrentSession;
            int messageCount = session == null || session.Messages == null
                ? 0
                : session.Messages.Count;
            string model = modelTextBox == null
                ? string.Empty
                : modelTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                model = "未配置模型";
            }

            long currentTurnCharacters = promptTextBox == null
                ? 0L
                : promptTextBox.TextLength;
            bool cachedSession = session != null &&
                string.Equals(
                    renderedSessionId,
                    session.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                renderedMessageCount == messageCount;
            long historyCharacters;
            if (cachedSession)
            {
                historyCharacters = renderedSessionCharacterEstimate;
            }
            else
            {
                historyCharacters = CalculateSessionCharacterEstimate(session);
            }

            foreach (InputItem item in inputItems)
            {
                if (item != null && item.Kind == InputKind.Text)
                {
                    currentTurnCharacters += string.IsNullOrEmpty(
                        item.TextContent)
                        ? 0L
                        : item.TextContent.Length;
                }
            }

            currentTurnCharacters += extensionPromptCharacterEstimate;

            // Keep the default line scannable at 900px. The tooltip and
            // AccessibleDescription retain the full character estimates.
            return "模型 " + model +
                "  ·  " + messageCount + " 条消息" +
                "  ·  " + BuildExtensionSummary() +
                "  ·  资料 " + inputItems.Count + " 项" +
                "  ·  历史 " + historyCharacters.ToString("N0") +
                "  ·  本轮 " + currentTurnCharacters.ToString("N0") +
                "  ·  上限 " +
                MaximumConversationContextCharacters.ToString("N0");
        }

        private string BuildCompactContextSummary()
        {
            ConversationSession session = conversationStore.CurrentSession;
            int messageCount = session == null || session.Messages == null
                ? 0
                : session.Messages.Count;
            long historyCharacters = session != null &&
                string.Equals(
                    renderedSessionId,
                    session.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                renderedMessageCount == messageCount
                    ? renderedSessionCharacterEstimate
                    : CalculateSessionCharacterEstimate(session);
            long currentTurnCharacters = promptTextBox == null
                ? 0L
                : promptTextBox.TextLength;
            foreach (InputItem item in inputItems)
            {
                if (item != null && item.Kind == InputKind.Text)
                {
                    currentTurnCharacters = AddSaturated(
                        currentTurnCharacters,
                        string.IsNullOrEmpty(item.TextContent)
                            ? 0L
                            : item.TextContent.Length);
                }
            }

            currentTurnCharacters = AddSaturated(
                currentTurnCharacters,
                extensionPromptCharacterEstimate);
            long totalCharacters = AddSaturated(
                historyCharacters,
                currentTurnCharacters);
            return messageCount + " 条消息" +
                "  ·  资料 " + inputItems.Count +
                "  ·  上下文 " + totalCharacters.ToString("N0") +
                " / " + MaximumConversationContextCharacters.ToString("N0");
        }

        private void UpdateContextSummary()
        {
            string summary = BuildContextSummary();
            if (contextSummaryLabel != null)
            {
                contextSummaryLabel.Text = BuildCompactContextSummary();
                contextSummaryLabel.AccessibleDescription = summary;
                if (contextSummaryToolTip != null)
                {
                    contextSummaryToolTip.SetToolTip(
                        contextSummaryLabel,
                        summary);
                }
            }

            if (settingsDialog != null && !settingsDialog.IsDisposed)
            {
                settingsDialog.SetContextSummary(summary);
                settingsDialog.SetExtensionSummary(BuildExtensionSummary());
            }
        }

        private void ScheduleContextSummaryUpdate()
        {
            if (contextSummaryTimer == null)
            {
                UpdateContextSummary();
                return;
            }

            contextSummaryTimer.Stop();
            contextSummaryTimer.Start();
        }

        private static long CalculateSessionCharacterEstimate(
            ConversationSession session)
        {
            long total = 0L;
            if (session == null || session.Messages == null)
            {
                return total;
            }

            foreach (ConversationMessage message in session.Messages)
            {
                if (message != null &&
                    !string.IsNullOrEmpty(message.Content))
                {
                    total += message.Content.Length;
                }
            }

            return total;
        }

        private static long CalculateExtensionPromptCharacterEstimate(
            ExtensionSettings settings)
        {
            if (settings == null)
            {
                return 0L;
            }

            string prompt = settings.BuildSystemPrompt();
            return string.IsNullOrEmpty(prompt) ? 0L : prompt.Length;
        }

        private void OnConnectionSettingChanged(object sender, EventArgs args)
        {
            if (connectionStatusLabel != null &&
                generationCancellation == null)
            {
                connectionStatusLabel.Text = BuildConnectionStatus();
            }

            if (testConnectionButton != null)
            {
                testConnectionButton.Enabled =
                    !IsBusy &&
                    HasCompleteConnectionSettings();
            }

            ScheduleContextSummaryUpdate();
        }

        private void UpdateSessionButtons()
        {
            bool hasSession = conversationStore.CurrentSession != null;
            if (deleteSessionButton != null)
            {
                deleteSessionButton.Enabled = hasSession &&
                    conversationStore.Sessions.Count > 1;
            }

            if (renameSessionButton != null)
            {
                renameSessionButton.Enabled = hasSession;
            }

            if (backupSessionsButton != null)
            {
                backupSessionsButton.Enabled = hasSession &&
                    generationCancellation == null;
            }

            if (restoreSessionsButton != null)
            {
                restoreSessionsButton.Enabled =
                    generationCancellation == null;
            }

            UpdateOutputButtons(generationCancellation != null);
        }

        private void LoadSavedSettings()
        {
            AppSettings settings = AppSettings.Load();
            endpointTextBox.Text = settings.EndpointUrl;
            apiKeyTextBox.Text = settings.ApiKey;
            modelTextBox.Text = settings.ModelName;
            sendShortcutMode = IsValidSendShortcutMode(settings.SendShortcut)
                ? settings.SendShortcut
                : "Both";
            UpdatePromptHint();
            UpdateSendShortcutMenuChecks();
            settingsDialog.SendShortcutMode = sendShortcutMode;
            connectionStatusLabel.Text = BuildConnectionStatus();
            testConnectionButton.Enabled = HasCompleteConnectionSettings();
            UpdateContextSummary();
        }

        private void SaveSettings()
        {
            try
            {
                AppSettings settings = new AppSettings();
                settings.EndpointUrl = endpointTextBox.Text.Trim();
                settings.ApiKey = apiKeyTextBox.Text.Trim();
                settings.ModelName = modelTextBox.Text.Trim();
                settings.SendShortcut = sendShortcutMode ?? "Both";
                settings.Save();
            }
            catch (Exception exception)
            {
                SetStatus("配置未能保存：" + exception.Message);
            }
        }

        private static bool IsValidSendShortcutMode(string mode)
        {
            return mode == "Enter" ||
                mode == "CtrlEnter" ||
                mode == "Both";
        }

        private bool IsEnterSendShortcut(bool control, bool shift)
        {
            if (shift)
            {
                return false;
            }

            if (sendShortcutMode == "Enter")
            {
                return !control;
            }

            if (sendShortcutMode == "CtrlEnter")
            {
                return control;
            }

            return true;
        }

        private static string SendShortcutDisplay(string mode)
        {
            if (mode == "Enter")
            {
                return "回车（Enter）";
            }

            if (mode == "CtrlEnter")
            {
                return "Ctrl+Enter";
            }

            return "回车 与 Ctrl+Enter";
        }

        private void SetSendShortcutMode(string mode)
        {
            if (!IsValidSendShortcutMode(mode))
            {
                mode = "Both";
            }

            sendShortcutMode = mode;
            if (settingsDialog != null && !settingsDialog.IsDisposed)
            {
                settingsDialog.SendShortcutMode = mode;
            }
            UpdatePromptHint();
            UpdateSendShortcutMenuChecks();
            SaveSettings();
            SetStatus("发送快捷键已设为：" + SendShortcutDisplay(mode));
        }

        private void UpdatePromptHint()
        {
            if (promptGroup == null)
            {
                return;
            }

            promptGroup.Text = "输入消息";
            if (sendShortcutMode == "Enter")
            {
                promptGroup.AccessibleDescription =
                    "回车发送，Shift+Enter 换行";
            }
            else if (sendShortcutMode == "CtrlEnter")
            {
                promptGroup.AccessibleDescription =
                    "Ctrl+Enter 发送，Enter 换行";
            }
            else
            {
                promptGroup.AccessibleDescription =
                    "回车或 Ctrl+Enter 发送，Shift+Enter 换行";
            }
        }

        private void UpdateSendShortcutMenuChecks()
        {
            ContextMenuStrip menu = generateButton == null
                ? null
                : generateButton.ContextMenuStrip;
            if (menu == null)
            {
                return;
            }

            foreach (ToolStripItem item in menu.Items)
            {
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.Tag != null)
                {
                    menuItem.Checked =
                        IsValidSendShortcutMode(menuItem.Tag as string) &&
                        (string)menuItem.Tag == sendShortcutMode;
                }
            }
        }

        private ContextMenuStrip CreatePromptActionsMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            AddPromptAction(menu, "\u603b\u7ed3\u5f53\u524d\u8d44\u6599", "\u8bf7\u603b\u7ed3\u5df2\u6dfb\u52a0\u8d44\u6599\uff0c\u7ed9\u51fa\u7ed3\u8bba\u3001\u5173\u952e\u4e8b\u5b9e\u548c\u5f85\u529e\u4e8b\u9879\u3002");
            AddPromptAction(menu, "\u63d0\u53d6\u5173\u952e\u70b9", "\u8bf7\u63d0\u53d6\u5185\u5bb9\u7684\u5173\u952e\u70b9\uff0c\u6309\u91cd\u8981\u6027\u6392\u5e8f\u5e76\u6807\u51fa\u4e0d\u786e\u5b9a\u9879\u3002");
            AddPromptAction(menu, "\u7ffb\u8bd1\u6210\u4e2d\u6587", "\u8bf7\u5c06\u5185\u5bb9\u51c6\u786e\u7ffb\u8bd1\u6210\u7b80\u4f53\u4e2d\u6587\uff0c\u4fdd\u7559\u6807\u9898\u3001\u8868\u683c\u548c\u4ee3\u7801\u683c\u5f0f\u3002");
            AddPromptAction(menu, "\u7ffb\u8bd1\u6210\u82f1\u6587", "\u8bf7\u5c06\u5185\u5bb9\u7ffb\u8bd1\u6210\u81ea\u7136\u3001\u4e13\u4e1a\u7684\u82f1\u6587\uff0c\u4fdd\u7559\u539f\u6709\u7ed3\u6784\u3002");
            AddPromptAction(menu, "\u751f\u6210 PPT \u5927\u7eb2", "\u8bf7\u5c06\u5185\u5bb9\u6574\u7406\u6210 PowerPoint \u6f14\u793a\u5927\u7eb2\uff0c\u6bcf\u9875\u5305\u542b\u6807\u9898\u3001\u8981\u70b9\u548c\u5efa\u8bae\u56fe\u8868\u3002");
            AddPromptAction(menu, "\u751f\u6210 XMind \u7ed3\u6784", "\u8bf7\u5c06\u5185\u5bb9\u6574\u7406\u6210 XMind \u601d\u7ef4\u5bfc\u56fe\u5c42\u7ea7\uff0c\u53ea\u8f93\u51fa\u6e05\u6670\u7684\u6811\u72b6\u7ed3\u6784\u3002");
            AddPromptAction(menu, "\u6574\u7406\u4e3a Markdown \u8868\u683c", "\u8bf7\u8bc6\u522b\u53ef\u7ed3\u6784\u5316\u7684\u4fe1\u606f\uff0c\u6574\u7406\u6210\u89c4\u8303\u7684 Markdown \u8868\u683c\u3002");
            return menu;
        }

        private void AddPromptAction(ContextMenuStrip menu, string title, string prompt)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(title);
            item.Click += delegate { ApplyPromptTemplate(prompt); };
            menu.Items.Add(item);
        }

        private void ApplyPromptTemplate(string template)
        {
            if (IsBusy || promptTextBox == null) { return; }
            string existing = promptTextBox.Text ?? string.Empty;
            promptTextBox.Text = string.IsNullOrWhiteSpace(existing)
                ? (template ?? string.Empty)
                : (template ?? string.Empty) + "\r\n\r\n" + existing;
            SaveCurrentDraft();
            promptTextBox.Focus();
            promptTextBox.SelectionStart = promptTextBox.TextLength;
            SetStatus("\u5feb\u6377\u6307\u4ee4\u5df2\u586b\u5165\u8f93\u5165\u6846\uff0c\u8bf7\u786e\u8ba4\u540e\u53d1\u9001");
        }

        private bool HasLastUserMessage()
        {
            ConversationSession session = conversationStore.CurrentSession;
            if (session == null || session.Messages == null) { return false; }
            for (int index = session.Messages.Count - 1; index >= 0; index--)
            {
                ConversationMessage message = session.Messages[index];
                if (message != null && message.Role == "user" &&
                    !string.IsNullOrWhiteSpace(message.Content)) { return true; }
            }
            return false;
        }

        private void LoadLastPromptForEditing()
        {
            if (IsBusy || promptTextBox == null) { return; }
            ConversationSession session = conversationStore.CurrentSession;
            ConversationMessage latest = null;
            if (session != null && session.Messages != null)
            {
                for (int index = session.Messages.Count - 1; index >= 0; index--)
                {
                    ConversationMessage message = session.Messages[index];
                    if (message != null && message.Role == "user" &&
                        !string.IsNullOrWhiteSpace(message.Content))
                    {
                        latest = message;
                        break;
                    }
                }
            }
            if (latest == null) { return; }

            string content = latest.Content ?? string.Empty;
            const string userHeader = "\u7528\u6237\u8981\u6c42\uff1a";
            int start = content.IndexOf(userHeader, StringComparison.Ordinal);
            if (start >= 0)
            {
                start += userHeader.Length;
                while (start < content.Length &&
                    (content[start] == '\r' || content[start] == '\n' || content[start] == ' ')) { start++; }
                int end = FindPromptMetadataStart(content, start);
                content = end >= 0 ? content.Substring(start, end - start) : content.Substring(start);
            }
            promptTextBox.Text = content.Trim();
            SaveCurrentDraft();
            promptTextBox.Focus();
            promptTextBox.SelectionStart = promptTextBox.TextLength;
            SetStatus("\u4e0a\u4e00\u6761\u6307\u4ee4\u5df2\u8f7d\u5165\uff0c\u53ef\u7f16\u8f91\u540e\u91cd\u65b0\u53d1\u9001");
        }

        private static int FindPromptMetadataStart(string content, int start)
        {
            string[] markers =
            {
                "\r\n\u4ee5\u4e0b\u8d44\u6599\u7531\u7528\u6237",
                "\n\u4ee5\u4e0b\u8d44\u6599\u7531\u7528\u6237",
                "\r\n\u4ee5\u4e0b\u56fe\u7247\u6216\u5185\u8054\u6587\u4ef6\u4ec5\u968f\u672c\u8f6e\u53d1\u9001",
                "\n\u4ee5\u4e0b\u56fe\u7247\u6216\u5185\u8054\u6587\u4ef6\u4ec5\u968f\u672c\u8f6e\u53d1\u9001"
            };
            int earliest = -1;
            foreach (string marker in markers)
            {
                int candidate = content.IndexOf(
                    marker,
                    start,
                    StringComparison.Ordinal);
                if (candidate >= 0 &&
                    (earliest < 0 || candidate < earliest))
                {
                    earliest = candidate;
                }
            }

            return earliest;
        }

        private ContextMenuStrip CreateSendShortcutMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem enterItem =
                new ToolStripMenuItem("回车（Enter）发送");
            enterItem.Tag = "Enter";
            enterItem.Click += delegate
            {
                SetSendShortcutMode("Enter");
            };
            ToolStripMenuItem ctrlEnterItem =
                new ToolStripMenuItem("Ctrl+Enter 发送");
            ctrlEnterItem.Tag = "CtrlEnter";
            ctrlEnterItem.Click += delegate
            {
                SetSendShortcutMode("CtrlEnter");
            };
            ToolStripMenuItem bothItem =
                new ToolStripMenuItem("回车 与 Ctrl+Enter 都发送");
            bothItem.Tag = "Both";
            bothItem.Click += delegate
            {
                SetSendShortcutMode("Both");
            };
            menu.Items.Add(enterItem);
            menu.Items.Add(ctrlEnterItem);
            menu.Items.Add(bothItem);
            return menu;
        }

        private void OnExtensionsClick(object sender, EventArgs args)
        {
            if (IsBusy || isClosing)
            {
                return;
            }

            ExtensionSettings candidate = (extensionSettings ??
                new ExtensionSettings()).Clone();
            IWin32Window owner = GetSettingsActionOwner();
            while (true)
            {
                using (ExtensionsDialog dialog = new ExtensionsDialog(
                    candidate))
                {
                    if (dialog.ShowDialog(owner) != DialogResult.OK ||
                        dialog.Settings == null)
                    {
                        return;
                    }

                    candidate = dialog.Settings.Clone();
                    try
                    {
                        extensionStore.Save(candidate);
                        extensionSettings = candidate.Clone();
                        extensionPromptCharacterEstimate =
                            CalculateExtensionPromptCharacterEstimate(
                                extensionSettings);
                        UpdateContextSummary();
                        SetStatus(
                            "新建或导入的 MCP 默认停用，需手动勾选启用 · " +
                            "扩展已保存 · " + BuildExtensionSummary());
                        return;
                    }
                    catch (Exception exception)
                    {
                        MessageBox.Show(
                            owner,
                            "扩展配置保存失败：" + exception.Message +
                            "\r\n\r\n编辑内容已保留，可以重试或取消。",
                            "技能 / MCP",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }
        }

        private string BuildExtensionSummary()
        {
            ExtensionSettings current = extensionSettings ??
                new ExtensionSettings();
            int skills = (current.Skills ??
                new List<SkillDefinition>()).Count(skill =>
                    skill != null && skill.Enabled);
            int servers = (current.McpServers ??
                new List<McpServerDefinition>()).Count(server =>
                server != null && server.Enabled);
            return "技能 " + skills + " · MCP " + servers;
        }

        private void OnUninstallClick(object sender, EventArgs args)
        {
            if (IsBusy)
            {
                SetStatus("当前有任务运行，请先停止后再卸载。");
                return;
            }

            string appDirectory =
                Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            string packageRoot = Directory.GetParent(
                appDirectory.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)).FullName;
            string uninstallerPath = Path.Combine(
                packageRoot,
                "Uninstall-FilePromptAI.exe");
            IWin32Window owner = GetSettingsActionOwner();
            if (!File.Exists(uninstallerPath))
            {
                MessageBox.Show(
                    owner,
                    "当前目录没有找到卸载器。便携版可以直接删除完整程序目录；" +
                    "源码目录不会被此操作处理。",
                    "卸载 FilePrompt AI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (!ConfirmExitIfNeeded())
            {
                SetStatus("已取消卸载，程序和未发送内容保持不变。");
                return;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = uninstallerPath;
                startInfo.Arguments = "--from-app " +
                    Process.GetCurrentProcess().Id.ToString();
                startInfo.WorkingDirectory = packageRoot;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
                exitConfirmationGranted = true;
                if (settingsDialog != null && settingsDialog.Visible)
                {
                    settingsDialog.DialogResult = DialogResult.Cancel;
                    settingsDialog.Hide();
                    BeginInvoke(new MethodInvoker(Close));
                }
                else
                {
                    Close();
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    owner,
                    "无法启动卸载器：\r\n\r\n" + exception.Message,
                    "卸载 FilePrompt AI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OnModelProfilesClick(object sender, EventArgs args)
        {
            IWin32Window owner = GetSettingsActionOwner();
            using (ModelProfilesDialog dialog = new ModelProfilesDialog(
                modelProfiles,
                CreateCurrentModelProfile()))
            {
                DialogResult result = dialog.ShowDialog(owner);
                IList<ModelProfile> updatedProfiles = dialog.Profiles;
                if (result == DialogResult.OK && dialog.Changed)
                {
                    try
                    {
                        modelProfileStore.Save(updatedProfiles);
                        modelProfiles = updatedProfiles;
                    }
                    catch (Exception exception)
                    {
                        SetStatus("模型配置未能保存：" + exception.Message);
                        MessageBox.Show(
                            owner,
                            "模型配置未能保存：\r\n\r\n" + exception.Message,
                            "模型配置",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }
                }

                if (result == DialogResult.OK && dialog.SelectedProfile != null)
                {
                    ApplyModelProfile(dialog.SelectedProfile);
                    SetStatus("已切换模型配置：" + dialog.SelectedProfile.Name);
                }

            }
        }

        private ModelProfile CreateCurrentModelProfile()
        {
            return new ModelProfile
            {
                EndpointUrl = endpointTextBox == null
                    ? string.Empty
                    : endpointTextBox.Text.Trim(),
                ApiKey = apiKeyTextBox == null
                    ? string.Empty
                    : apiKeyTextBox.Text.Trim(),
                ModelName = modelTextBox == null
                    ? string.Empty
                    : modelTextBox.Text.Trim()
            };
        }

        private void ApplyModelProfile(ModelProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            endpointTextBox.Text = profile.EndpointUrl ?? string.Empty;
            apiKeyTextBox.Text = profile.ApiKey ?? string.Empty;
            modelTextBox.Text = profile.ModelName ?? string.Empty;
            SaveSettings();
            connectionStatusLabel.Text = BuildConnectionStatus();
            testConnectionButton.Enabled = HasCompleteConnectionSettings();
            UpdateContextSummary();
        }

        private async void OnTestConnectionClick(object sender, EventArgs args)
        {
            if (connectionTestCancellation != null ||
                generationCancellation != null ||
                isAddingFiles)
            {
                return;
            }

            string endpoint = endpointTextBox.Text.Trim();
            string key = apiKeyTextBox.Text.Trim();
            string model = modelTextBox.Text.Trim();
            IWin32Window owner = GetSettingsActionOwner();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                ShowValidation("请先填写完整请求 URL。", endpointTextBox);
                return;
            }

            Uri endpointUri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttp &&
                    endpointUri.Scheme != Uri.UriSchemeHttps))
            {
                ShowValidation(
                    "请求 URL 必须是完整的 http:// 或 https:// 地址。",
                    endpointTextBox);
                return;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                ShowValidation("请先填写 API Key。", apiKeyTextBox);
                return;
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                ShowValidation("请先填写模型名称。", modelTextBox);
                return;
            }

            SaveSettings();
            CancellationTokenSource cancellation =
                new CancellationTokenSource();
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            connectionTestCancellation = cancellation;
            SetConnectionTestingState(true);
            connectionStatusLabel.Text = "正在测试 · " + model;
            SetStatus("正在测试模型连接…");
            try
            {
                await modelClient.TestConnectionAsync(
                    endpoint,
                    key,
                    model,
                    cancellation.Token);
                connectionStatusLabel.Text = "连接成功 · " + model;
                SetStatus("连接测试成功，配置已保存在本机");
            }
            catch (OperationCanceledException)
            {
                connectionStatusLabel.Text = "连接超时 · 请检查网络或 URL";
                MessageBox.Show(
                    owner,
                    "连接测试在 30 秒内没有完成。请检查 URL、内网连通性和模型服务状态。",
                    "连接测试超时",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch (ModelCallException exception)
            {
                connectionStatusLabel.Text = "连接失败 · 请检查配置";
                MessageBox.Show(
                    owner,
                    exception.Message,
                    "连接测试失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception exception)
            {
                connectionStatusLabel.Text = "连接失败 · 请检查配置";
                MessageBox.Show(
                    owner,
                    "连接测试失败：" + exception.Message,
                    "连接测试失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (ReferenceEquals(connectionTestCancellation, cancellation))
                {
                    connectionTestCancellation = null;
                }

                cancellation.Dispose();
                if (!IsDisposed)
                {
                    SetConnectionTestingState(false);
                }
            }
        }

        private void SetConnectionTestingState(bool testing)
        {
            testConnectionButton.Enabled = !testing &&
                !isAddingFiles &&
                generationCancellation == null &&
                HasCompleteConnectionSettings();
            endpointTextBox.Enabled = !testing;
            apiKeyTextBox.Enabled = !testing;
            modelTextBox.Enabled = !testing;
            settingsDialog.ModelProfilesButton.Enabled = !testing;
            settingsDialog.UninstallButton.Enabled = !testing;
            settingsDialog.SendShortcutComboBox.Enabled = !testing;
            if (extensionsButton != null)
            {
                extensionsButton.Enabled = !testing &&
                    generationCancellation == null &&
                    !isAddingFiles;
            }
            generateButton.Enabled = !testing &&
                generationCancellation == null &&
                !isAddingFiles;
            SetInputButtonsEnabled(!testing && !isAddingFiles);
            SetSessionNavigationEnabled(!testing && !isAddingFiles &&
                generationCancellation == null);
            if (testing)
            {
                renameSessionButton.Enabled = false;
                deleteSessionButton.Enabled = false;
                backupSessionsButton.Enabled = false;
                restoreSessionsButton.Enabled = false;
            }
            else
            {
                UpdateSessionButtons();
            }

            progressBar.Visible = testing || isAddingFiles ||
                generationCancellation != null;
        }

        private void OnNewSessionClick(object sender, EventArgs args)
        {
            if (IsBusy)
            {
                return;
            }

            if (conversationStore.IsWriteBlocked)
            {
                ShowConversationWriteBlocked();
                return;
            }

            SaveCurrentDraft();
            ConversationSession session =
                conversationStore.CreateSession("新会话");
            RefreshSessionList();
            conversationStore.SelectSession(session.Id);
            LoadCurrentSession();
            RestoreCurrentDraft();
            promptTextBox.Focus();
            SetStatus("已创建新会话");
        }

        private void OnSessionSelected(object sender, EventArgs args)
        {
            if (isLoadingSession || sessionListBox.SelectedItem == null)
            {
                return;
            }

            ConversationSession session =
                sessionListBox.SelectedItem as ConversationSession;
            if (session == null)
            {
                return;
            }

            SaveCurrentDraft();
            if (conversationStore.SelectSession(session.Id))
            {
                LoadCurrentSession();
                RestoreCurrentDraft();
                SetStatus("已切换到：" + session.Title);
            }
        }

        private void OnRenameSessionClick(object sender, EventArgs args)
        {
            if (IsBusy)
            {
                return;
            }

            ConversationSession session = conversationStore.CurrentSession;
            if (session == null)
            {
                return;
            }

            string title = PromptForText(
                this,
                "重命名会话",
                "会话名称：",
                session.Title);
            if (title == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            conversationStore.RenameSession(session.Id, title.Trim());
            RefreshSessionList();
            LoadCurrentSession();
        }

        private void OnDeleteSessionClick(object sender, EventArgs args)
        {
            if (IsBusy)
            {
                return;
            }

            ConversationSession session = conversationStore.CurrentSession;
            if (session == null || conversationStore.Sessions.Count <= 1)
            {
                return;
            }

            DialogResult result = MessageBox.Show(
                this,
                "确定删除会话“" + session.Title + "”吗？",
                "删除会话",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (result != DialogResult.Yes)
            {
                return;
            }

            ClearDraft(session.Id);
            conversationStore.DeleteSession(session.Id);
            RefreshSessionList();
            LoadCurrentSession();
            RestoreCurrentDraft();
            SetStatus("会话已删除");
        }

        private void OnBackupSessionsClick(object sender, EventArgs args)
        {
            if (IsBusy)
            {
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                IWin32Window owner = GetSettingsActionOwner();
                dialog.Title = "备份全部会话";
                dialog.Filter = "FilePrompt AI 会话备份|*.fpc";
                dialog.DefaultExt = "fpc";
                dialog.AddExtension = true;
                dialog.FileName = "FilePromptAI会话备份_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".fpc";
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    int count = conversationStore.ExportBackup(dialog.FileName);
                    SetStatus("已备份 " + count + " 个会话；不包含 URL、Key 和模型配置");
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        owner,
                        "备份失败：" + exception.Message,
                        "备份会话",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private void OnRestoreSessionsClick(object sender, EventArgs args)
        {
            if (IsBusy)
            {
                return;
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                IWin32Window owner = GetSettingsActionOwner();
                dialog.Title = "恢复会话备份";
                dialog.Filter = "FilePrompt AI 会话备份|*.fpc";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    int count = conversationStore.ImportBackup(dialog.FileName);
                    if (sessionSearchTextBox != null)
                    {
                        sessionSearchTextBox.Clear();
                        if (sessionSearchTimer != null)
                        {
                            sessionSearchTimer.Stop();
                        }
                    }

                    RefreshSessionList();
                    LoadCurrentSession();
                    SetStatus(count == 0
                        ? "备份中没有可恢复的会话"
                        : "已恢复 " + count + " 个会话；原有会话已保留");
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        owner,
                        "恢复失败：" + exception.Message,
                        "恢复会话",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private void InitializeFileDropTarget()
        {
            if (IsDisposed || Disposing || fileDropTargetPanel == null ||
                fileDropTargetPanel.IsDisposed ||
                fileDropTargetPanel.AllowDrop)
            {
                return;
            }

            try
            {
                // Register one stable, always-visible HWND only. Registering
                // the form, GroupBox and ListView separately can fail while
                // Win7 recreates child handles, and can throw again while
                // those controls are disposed.
                if (Application.OleRequired() != ApartmentState.STA)
                {
                    SetStatus(
                        "当前线程无法启用文件拖拽；仍可用“+ 添加”选择文件或按路径添加。");
                    return;
                }

                fileDropTargetPanel.CreateControl();
                fileDropTargetPanel.AllowDrop = true;
            }
            catch (Exception exception)
            {
                try
                {
                    fileDropTargetPanel.AllowDrop = false;
                }
                catch
                {
                    // The window remains usable through the explicit file
                    // picker and path controls even if OLE cleanup also fails.
                }

                SetStatus(
                    "文件拖拽未启用，可改用“+ 添加”选择文件或按路径添加：" +
                    exception.Message);
            }
        }

        private void DisableFileDropTarget()
        {
            if (fileDropTargetPanel == null ||
                fileDropTargetPanel.IsDisposed ||
                !fileDropTargetPanel.AllowDrop)
            {
                return;
            }

            try
            {
                // Clear the WinForms state before this control is disposed so
                // a failed Win7 RevokeDragDrop does not become a fatal
                // shutdown exception.
                fileDropTargetPanel.AllowDrop = false;
            }
            catch
            {
                // Closing must continue even when OLE has already torn down.
            }
        }

        private void OnDragEnter(object sender, DragEventArgs args)
        {
            if (args == null)
            {
                return;
            }

            try
            {
                args.Effect = !IsBusy &&
                    args.Data != null &&
                    args.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
            }
            catch (Exception exception)
            {
                args.Effect = DragDropEffects.None;
                SetStatus("无法读取拖入内容：" + exception.Message);
            }
        }

        private async void OnDragDrop(object sender, DragEventArgs args)
        {
            if (args == null || IsBusy)
            {
                return;
            }

            try
            {
                if (args.Data == null ||
                    !args.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    return;
                }

                string[] paths = args.Data.GetData(
                    DataFormats.FileDrop) as string[];
                if (paths == null || paths.Length == 0)
                {
                    SetStatus("拖入内容中没有可读取的文件路径。");
                    return;
                }

                await AddFilesAsync(paths);
            }
            catch (Exception exception)
            {
                if (!isClosing && !IsDisposed && !Disposing)
                {
                    MessageBox.Show(
                        this,
                        "读取拖入文件失败：" + exception.Message +
                        "\r\n\r\n可改用“+ 添加”选择文件，或打开路径窗口后点击“添加”。",
                        "添加文件",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    SetStatus("拖入文件未能读取，请检查文件权限或改用文件选择器。");
                }
            }
        }

        private async void OnAddFileClick(object sender, EventArgs args)
        {
            if (IsBusy)
            {
                return;
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要交给模型的文件";
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;
                dialog.Filter =
                    "支持的文件|*.txt;*.md;*.csv;*.json;*.xml;*.yaml;*.yml;*.log;*.sql;*.java;*.cs;*.cpp;*.h;*.py;*.js;*.ts;*.html;*.css;*.pdf;*.doc;*.docx;*.rtf;*.xls;*.xlsx;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|" +
                    "所有文件|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    await AddFilesAsync(dialog.FileNames);
                }
            }
        }

        private async void OnReadPathClick(object sender, EventArgs args)
        {
            if (IsBusy)
            {
                return;
            }

            string raw = pathTextBox == null ? string.Empty : pathTextBox.Text;
            string[] paths = ParsePastedPaths(raw);
            if (paths.Length == 0)
            {
                ShowValidation(
                    "请先输入至少一个文件路径（每行一个），再点击“添加”。",
                    pathTextBox);
                return;
            }

            FileAddResult addResult = await AddFilesAsync(paths);
            if (addResult == null)
            {
                return;
            }

            if (addResult.FailedPaths.Count == 0 &&
                addResult.AddedCount + addResult.SkippedCount > 0)
            {
                pathTextBox.Clear();
            }
            else if (addResult.FailedPaths.Count > 0 &&
                pathTextBox != null && !IsDisposed)
            {
                pathTextBox.Text = string.Join(
                    Environment.NewLine,
                    addResult.FailedPaths.ToArray());
                SetStatus(
                    "已读取 " + addResult.AddedCount +
                    " 个文件；未读取的路径已保留，可修正后重试。");
            }
        }

        private static string[] ParsePastedPaths(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new string[0];
            }

            // Explorer and command prompts commonly wrap a path in quotes.
            // Keep parsing line-oriented so spaces and semicolons in a Windows
            // filename remain valid; users can paste multiple lines directly.
            return raw
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim().Trim('"'))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private async Task<FileAddResult> AddFilesAsync(
            IEnumerable<string> paths)
        {
            FileAddResult outcome = new FileAddResult();
            if (IsBusy || isClosing)
            {
                if (!isClosing)
                {
                    SetStatus("当前任务尚未完成，请稍候。");
                }
                return outcome;
            }

            string[] candidates = (paths ?? new string[0])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (candidates.Length == 0)
            {
                return outcome;
            }

            // Bound raw input separately from the real-file limit. This lets
            // a pasted list contain harmless duplicates or missing paths
            // without causing a valid file to be rejected before resolution.
            if (candidates.Length > MaximumPathCandidates)
            {
                MessageBox.Show(
                    this,
                    "一次最多检查 " + MaximumPathCandidates +
                    " 个路径；实际文件最多添加 " + MaximumFilesPerAdd +
                    " 个，请分批处理。",
                    "文件过多",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                foreach (string candidate in candidates)
                {
                    AddFailedPath(outcome, candidate);
                }
                return outcome;
            }

            isAddingFiles = true;
            SetInputButtonsEnabled(false);
            SetSessionNavigationEnabled(false);
            testConnectionButton.Enabled = false;
            extensionsButton.Enabled = false;
            progressBar.Visible = true;
            List<string> errors = new List<string>();
            CancellationTokenSource addCancellation =
                new CancellationTokenSource();
            fileAddCancellation = addCancellation;
            try
            {
                CancellationToken cancellationToken = addCancellation.Token;
                SetStatus("正在检查文件路径...");
                Task<FileResolutionResult> resolution = Task.Run(
                    delegate
                    {
                        return ResolveExistingFiles(
                            candidates,
                            cancellationToken);
                    },
                    cancellationToken);
                Task completed = await Task.WhenAny(
                    resolution,
                    Task.Delay(PathResolutionTimeoutMilliseconds));
                if (!ReferenceEquals(completed, resolution))
                {
                    addCancellation.Cancel();
                    SetStatus(
                        "文件路径检查超时，请确认网络路径可访问后重试。");
                    outcome.TimedOut = true;
                    foreach (string candidate in candidates)
                    {
                        AddFailedPath(outcome, candidate);
                    }
                    return outcome;
                }

                FileResolutionResult resolved = await resolution;
                foreach (string rejected in resolved.RejectedPaths)
                {
                    AddFailedPath(outcome, rejected);
                }

                if (resolved.TooManyFiles)
                {
                    MessageBox.Show(
                        this,
                        "规范化后一次最多添加 " + MaximumFilesPerAdd +
                        " 个文件，请分批处理。",
                        "文件过多",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    foreach (string candidate in candidates)
                    {
                        AddFailedPath(outcome, candidate);
                    }

                    return outcome;
                }

                if (resolved.Files.Count == 0)
                {
                    SetStatus(
                        "没有找到可读取的文件；目录不会被自动扫描。");
                    return outcome;
                }

                foreach (string path in resolved.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string displayName = Path.GetFileName(path);
                    SetStatus("正在提取：" + displayName);
                    try
                    {
                        InputItem item = await Task.Run(
                            delegate { return extractor.ExtractFile(path); },
                            cancellationToken);
                        if (item != null)
                        {
                            item.SourcePath = path;
                            if (AddInputItem(item))
                            {
                                outcome.AddedCount++;
                            }
                            else
                            {
                                outcome.SkippedCount++;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        if (exception is OperationCanceledException &&
                            cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }

                        AddFailedPath(outcome, path);
                        errors.Add(displayName + "：" + exception.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!isClosing)
                {
                    SetStatus("文件读取已取消，请重新添加需要处理的文件。");
                }
            }
            finally
            {
                if (ReferenceEquals(fileAddCancellation, addCancellation))
                {
                    fileAddCancellation = null;
                }

                addCancellation.Dispose();
                isAddingFiles = false;
                if (!isClosing && !IsDisposed && !Disposing)
                {
                    SetInputButtonsEnabled(true);
                    SetSessionNavigationEnabled(
                        generationCancellation == null &&
                        connectionTestCancellation == null);
                    testConnectionButton.Enabled =
                        generationCancellation == null &&
                        connectionTestCancellation == null &&
                        HasCompleteConnectionSettings();
                    extensionsButton.Enabled = generationCancellation == null &&
                        connectionTestCancellation == null;
                    progressBar.Visible = generationCancellation != null ||
                        connectionTestCancellation != null;
                    UpdateInputStatus();
                }
            }

            if (!isClosing && !IsDisposed && errors.Count > 0)
            {
                MessageBox.Show(
                    "以下内容未能添加：\r\n\r\n" +
                    string.Join("\r\n", errors.ToArray()),
                    "部分文件处理失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else if (!isClosing && !IsDisposed && outcome.SkippedCount > 0)
            {
                SetStatus(
                    "已添加 " + outcome.AddedCount +
                    " 个文件；已跳过 " + outcome.SkippedCount +
                    " 个重复文件。");
            }

            // A drag/drop or file-picker batch has no dedicated source field
            // to restore failed entries into. Keep them in the path editor
            // when it is idle so the user can correct and retry them.
            if (!isClosing && !IsDisposed &&
                outcome.FailedPaths.Count > 0 &&
                pathTextBox != null &&
                string.IsNullOrWhiteSpace(pathTextBox.Text))
            {
                pathTextBox.Text = string.Join(
                    Environment.NewLine,
                    outcome.FailedPaths.ToArray());
                SetStatus("部分路径未读取，失败项已保留在路径窗口中。");
            }

            return outcome;
        }

        private static FileResolutionResult ResolveExistingFiles(
            IEnumerable<string> paths)
        {
            return ResolveExistingFiles(paths, CancellationToken.None);
        }

        private static FileResolutionResult ResolveExistingFiles(
            IEnumerable<string> paths,
            CancellationToken cancellationToken)
        {
            FileResolutionResult result = new FileResolutionResult();
            List<string> normalizedPaths = new List<string>();
            HashSet<string> seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths ?? new string[0])
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // File.Exists returns false for directories, so this never
                    // expands or scans a folder supplied by the user.
                    string fullPath = Path.GetFullPath(path);
                    if (seen.Add(fullPath))
                    {
                        normalizedPaths.Add(fullPath);
                    }
                }
                catch (Exception)
                {
                    result.RejectedPaths.Add(path);
                }
            }

            foreach (string path in normalizedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(path))
                    {
                        result.Files.Add(path);
                        if (result.Files.Count > MaximumFilesPerAdd)
                        {
                            result.TooManyFiles = true;
                            return result;
                        }
                    }
                    else
                    {
                        result.RejectedPaths.Add(path);
                    }
                }
                catch (Exception)
                {
                    result.RejectedPaths.Add(path);
                }
            }

            return result;
        }

        private static void AddFailedPath(
            FileAddResult result,
            string path)
        {
            if (result == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!result.FailedPaths.Contains(path))
            {
                result.FailedPaths.Add(path);
            }
        }

        private async void OnPasteClick(object sender, EventArgs args)
        {
            if (IsBusy)
            {
                return;
            }

            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    StringCollection collection = Clipboard.GetFileDropList();
                    await AddFilesAsync(collection.Cast<string>().ToArray());
                    return;
                }

                if (Clipboard.ContainsImage())
                {
                    using (Image image = Clipboard.GetImage())
                    {
                        if (image != null)
                        {
                            AddInputItem(extractor.CreateClipboardImage(image));
                            UpdateInputStatus();
                            return;
                        }
                    }
                }

                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        AddInputItem(extractor.CreateClipboardText(text));
                        UpdateInputStatus();
                        return;
                    }
                }

                MessageBox.Show(
                    "剪贴板中没有可用的文字、图片或文件。",
                    "粘贴内容",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "读取剪贴板失败：" + exception.Message,
                    "粘贴内容",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private bool AddInputItem(InputItem item)
        {
            if (item == null)
            {
                return false;
            }

            string sourcePath = NormalizeSourcePath(item.SourcePath);
            if (!string.IsNullOrEmpty(sourcePath) &&
                inputItems.Any(existing => string.Equals(
                    NormalizeSourcePath(
                        existing == null ? null : existing.SourcePath),
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            EnsureInputItemRetentionBudget(item);
            item.SourcePath = sourcePath;
            inputItems.Add(item);
            ListViewItem row = new ListViewItem(item.Name);
            row.SubItems.Add(item.GetKindText());
            row.SubItems.Add(item.GetSizeText());
            row.SubItems.Add(item.Note ?? string.Empty);
            row.Tag = item;
            inputListView.Items.Add(row);
            return true;
        }

        private void EnsureInputItemRetentionBudget(InputItem candidate)
        {
            if (candidate == null || candidate.BinaryData == null ||
                candidate.BinaryData.Length == 0)
            {
                return;
            }

            HashSet<InputItem> retained = new HashSet<InputItem>();
            long totalBytes = 0L;
            foreach (InputItem item in inputItems)
            {
                if (item != null && retained.Add(item) &&
                    item.BinaryData != null)
                {
                    totalBytes = AddSaturated(
                        totalBytes,
                        item.BinaryData.LongLength);
                }
            }

            foreach (SessionDraft draft in sessionDrafts.Values)
            {
                if (draft == null || draft.Items == null)
                {
                    continue;
                }

                foreach (InputItem item in draft.Items)
                {
                    if (item != null && retained.Add(item) &&
                        item.BinaryData != null)
                    {
                        totalBytes = AddSaturated(
                            totalBytes,
                            item.BinaryData.LongLength);
                    }
                }
            }

            if (retained.Add(candidate))
            {
                totalBytes = AddSaturated(
                    totalBytes,
                    candidate.BinaryData.LongLength);
            }

            if (totalBytes > MaximumRetainedBinaryBytes)
            {
                throw new InvalidOperationException(
                    "当前运行期保留的二进制附件合计不能超过 20 MB。" +
                    "请先发送或移除附件；其他会话有草稿时，" +
                    "请切换过去清理后再添加。");
            }
        }

        private static string NormalizeSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path.Trim();
            }
        }

        private void OnRemoveClick(object sender, EventArgs args)
        {
            RemoveSelectedItems();
        }

        private void OnPreviewInputItem(object sender, EventArgs args)
        {
            if (inputListView.SelectedItems.Count == 0)
            {
                return;
            }

            InputItem item = inputListView.SelectedItems[0].Tag as InputItem;
            if (item != null)
            {
                InputPreviewDialog.ShowPreview(this, item);
            }
        }

        private void RemoveSelectedItems()
        {
            ListViewItem[] selected = inputListView.SelectedItems
                .Cast<ListViewItem>()
                .ToArray();
            foreach (ListViewItem row in selected)
            {
                InputItem item = row.Tag as InputItem;
                if (item != null)
                {
                    inputItems.Remove(item);
                }

                inputListView.Items.Remove(row);
            }

            UpdateInputStatus();
        }

        private void OnClearClick(object sender, EventArgs args)
        {
            inputItems.Clear();
            inputListView.Items.Clear();
            UpdateInputStatus();
        }

        private void UpdateInputStatus()
        {
            int textCount = inputItems.Count(item => item.Kind == InputKind.Text);
            int imageCount = inputItems.Count(item => item.Kind == InputKind.Image);
            int fileCount = inputItems.Count(item => item.Kind == InputKind.File);
            SetStatus(
                "已添加 " + inputItems.Count +
                " 项（文本 " + textCount +
                "、图片 " + imageCount +
                "、文件 " + fileCount + "）");
            UpdateConversationAreaRows();
            UpdateContextSummary();
        }

        private async void StartGeneration()
        {
            if (IsBusy)
            {
                return;
            }

            string endpoint = endpointTextBox.Text.Trim();
            string key = apiKeyTextBox.Text.Trim();
            string model = modelTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                ShowSettingsDialog(
                    endpointTextBox,
                    "请先填写完整请求 URL。");
                return;
            }

            Uri endpointUri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttp &&
                    endpointUri.Scheme != Uri.UriSchemeHttps))
            {
                ShowSettingsDialog(
                    endpointTextBox,
                    "请求 URL 必须是完整的 http:// 或 https:// 地址。");
                return;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                ShowSettingsDialog(apiKeyTextBox, "请先填写 API Key。");
                return;
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                ShowSettingsDialog(modelTextBox, "请先填写模型名称。");
                return;
            }

            if (inputItems.Count == 0 &&
                string.IsNullOrWhiteSpace(promptTextBox.Text))
            {
                ShowValidation(
                    "请拖入或粘贴内容，或者输入文字描述。",
                    promptTextBox);
                return;
            }

            string submittedPromptText = promptTextBox.Text ?? string.Empty;
            string instruction = GetCurrentInstruction();
            List<InputItem> attachments = inputItems
                .Where(item => item != null && item.Kind != InputKind.Text)
                .ToList();
            string systemPrompt = extensionSettings == null
                ? string.Empty
                : extensionSettings.BuildSystemPrompt();
            string prompt;
            string promptValidationMessage;
            bool fileTextWasTrimmed;
            if (!TryBuildCombinedPrompt(
                instruction,
                systemPrompt,
                out prompt,
                out promptValidationMessage,
                out fileTextWasTrimmed))
            {
                ShowValidation(promptValidationMessage, promptTextBox);
                return;
            }

            ConversationSession session = conversationStore.CurrentSession;
            if (session == null)
            {
                if (conversationStore.IsWriteBlocked)
                {
                    ShowConversationWriteBlocked();
                    return;
                }

                session = conversationStore.CreateSession("新会话");
                RefreshSessionList();
            }

            string visibleUserMessage = BuildVisibleUserMessage(instruction);
            ConversationContextSelection contextSelection =
                SelectConversationTextContext(
                    session.Messages,
                    systemPrompt,
                    prompt);
            bool historyWasTrimmed = contextSelection.WasTruncated;
            ModelRequest request = new ModelRequest();
            request.EndpointUrl = endpoint;
            request.ApiKey = key;
            request.ModelName = model;
            request.Prompt = prompt;
            request.SystemPrompt = systemPrompt;
            request.Attachments = attachments;
            request.ConversationMessages = contextSelection.Messages;

            SaveSettings();
            PrepareStreamingTurn(session, visibleUserMessage);
            lock (streamOutputSync)
            {
                streamedResponse = new StringBuilder();
                pendingStreamOutput.Length = 0;
            }
            generationCancellation = new CancellationTokenSource();
            generationSequence++;
            if (generationSequence <= 0)
            {
                generationSequence = 1;
            }

            activeGenerationSequence = generationSequence;
            if (streamFlushTimer != null)
            {
                streamFlushTimer.Start();
            }

            SetGeneratingState(true);
            connectionStatusLabel.Text = "正在请求 · " + model;
            UpdateContextSummary();
            McpRuntime mcpRuntime = null;
            bool stdioStartupRejected = false;

            try
            {
                IList<McpServerDefinition> enabledServers =
                    (extensionSettings == null ||
                        extensionSettings.McpServers == null)
                    ? new List<McpServerDefinition>()
                    : extensionSettings.McpServers
                        .Where(server => server != null && server.Enabled)
                        .Select(server => server.Clone())
                        .ToList();
                string result;
                if (enabledServers.Count > 0)
                {
                    generationCancellation.Token.ThrowIfCancellationRequested();
                    if (!ShowStdioMcpStartupApproval(enabledServers))
                    {
                        stdioStartupRejected = true;
                        throw new OperationCanceledException();
                    }

                    mcpRuntime = await McpRuntime.ConnectAsync(
                        enabledServers,
                        SetStatus,
                        generationCancellation.Token);
                    if (mcpRuntime.Tools.Count > 0)
                    {
                        McpRuntime activeRuntime = mcpRuntime;
                        result = await modelClient.GenerateWithToolsAsync(
                            request,
                            activeRuntime.Tools,
                            delegate(
                                ModelToolCall toolCall,
                                CancellationToken cancellationToken)
                            {
                                return ExecuteMcpToolAsync(
                                    activeRuntime,
                                    toolCall,
                                    cancellationToken);
                            },
                            AppendOutput,
                            SetStatus,
                            generationCancellation.Token);
                    }
                    else
                    {
                        result = await modelClient.GenerateAsync(
                            request,
                            AppendOutput,
                            SetStatus,
                            generationCancellation.Token);
                    }
                }
                else
                {
                    result = await modelClient.GenerateAsync(
                        request,
                        AppendOutput,
                        SetStatus,
                        generationCancellation.Token);
                }

                string accumulatedResponse = DeactivateStreamingOutput();
                string finalResponse = !string.IsNullOrEmpty(result)
                    ? result
                    : accumulatedResponse;

                string updatedTitle = BuildAutoTitle(session, instruction);
                bool saved = conversationStore.AddTurn(
                    session.Id,
                    new ConversationMessage("user", prompt),
                    new ConversationMessage(
                        "assistant",
                        finalResponse),
                    updatedTitle);
                if (!saved)
                {
                    throw new InvalidOperationException(
                        "当前会话已不存在，生成结果未能保存。");
                }

                FinalizeStreamingTurn(prompt, finalResponse);
                renderedSessionId = session.Id;
                renderedMessageCount = session.Messages == null
                    ? 0
                    : session.Messages.Count;
                renderedSessionCharacterEstimate =
                    CalculateSessionCharacterEstimate(session);
                streamedTurnStart = -1;
                streamedContentStart = -1;
                RefreshSessionList();
                sessionTitleLabel.Text = session.Title;
                ClearDraft(session.Id);
                bool promptUnchanged = string.Equals(
                    promptTextBox.Text,
                    submittedPromptText,
                    StringComparison.Ordinal);
                if (promptUnchanged)
                {
                    promptTextBox.Clear();
                }

                inputItems.Clear();
                inputListView.Items.Clear();
                UpdateContextSummary();
                SetStatus(
                    "生成完成，共 " +
                    finalResponse.Length.ToString("N0") +
                    " 字符" +
                    (promptUnchanged
                        ? string.Empty
                        : "；已保留等待期间输入的下一条指令") +
                    (historyWasTrimmed
                        ? "；会话较长，已保留最近完整问答"
                        : string.Empty) +
                    (fileTextWasTrimmed
                        ? "；文件正文超出本轮预算，已截断"
                        : string.Empty));
                promptTextBox.Focus();
            }
            catch (OperationCanceledException)
            {
                DeactivateStreamingOutput();
                RemoveStreamingTurnPreview(session);
                SetStatus(stdioStartupRejected
                    ? "已拒绝启动本地 MCP，本次生成已取消。"
                    : "已停止，本次内容未写入会话。");
            }
            catch (ModelCallException exception)
            {
                DeactivateStreamingOutput();
                RemoveStreamingTurnPreview(session);
                SetStatus("生成失败");
                MessageBox.Show(
                    exception.Message,
                    "模型调用失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (McpException exception)
            {
                DeactivateStreamingOutput();
                RemoveStreamingTurnPreview(session);
                SetStatus("MCP 调用失败");
                MessageBox.Show(
                    this,
                    exception.Message,
                    "MCP 调用失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception exception)
            {
                DeactivateStreamingOutput();
                RemoveStreamingTurnPreview(session);
                SetStatus("生成失败");
                MessageBox.Show(
                    "发生错误：" + exception.Message,
                    "生成失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                DeactivateStreamingOutput();
                if (mcpRuntime != null)
                {
                    mcpRuntime.Dispose();
                }

                if (generationCancellation != null)
                {
                    generationCancellation.Dispose();
                    generationCancellation = null;
                }
                lock (streamOutputSync)
                {
                    streamedResponse = null;
                    pendingStreamOutput.Length = 0;
                }
                streamedTurnStart = -1;
                streamedContentStart = -1;
                SetGeneratingState(false);
                connectionStatusLabel.Text = BuildConnectionStatus();
                UpdateContextSummary();
            }
        }

        private static string BuildAutoTitle(
            ConversationSession session,
            string prompt)
        {
            if (session == null ||
                session.Messages == null ||
                session.Messages.Count > 0 ||
                !string.Equals(session.Title, "新会话",
                    StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string title = (prompt ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (title.Length > 22)
            {
                title = title.Substring(0, 22) + "…";
            }

            return string.IsNullOrWhiteSpace(title)
                ? string.Empty
                : title;
        }

        private bool ShowStdioMcpStartupApproval(
            IList<McpServerDefinition> servers)
        {
            IList<McpServerDefinition> stdioServers = (servers ??
                new List<McpServerDefinition>())
                .Where(server => server != null && server.Enabled &&
                    string.Equals(
                        server.Transport,
                        "stdio",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (stdioServers.Count == 0)
            {
                return true;
            }

            string details = BuildStdioMcpStartupDetails(stdioServers);
            using (Form dialog = new Form())
            {
                Rectangle workingArea = Screen.FromControl(this).WorkingArea;
                int width = Math.Min(
                    780,
                    Math.Max(360, workingArea.Width - 48));
                int height = Math.Min(
                    600,
                    Math.Max(300, workingArea.Height - 48));
                dialog.Text = "确认启动本地 MCP 服务";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimumSize = new Size(
                    Math.Min(520, width),
                    Math.Min(360, height));
                dialog.Size = new Size(width, height);
                dialog.Font = Font;
                dialog.BackColor = UiTheme.WindowBackground;
                dialog.ForeColor = UiTheme.ButtonText;
                dialog.AutoScaleMode = AutoScaleMode.None;
                dialog.ShowInTaskbar = false;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(12);
                layout.ColumnCount = 1;
                layout.RowCount = 4;
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

                Label warning = new Label();
                warning.Dock = DockStyle.Fill;
                warning.TextAlign = ContentAlignment.MiddleLeft;
                warning.ForeColor = UiTheme.ButtonText;
                warning.Text =
                    "本地 stdio MCP 会在工具调用确认之前启动，并以当前用户权限运行。" +
                    "请完整核对下面的命令；仅在信任配置时允许。";
                warning.AccessibleName = "本地 MCP 启动安全提示";

                Label detailsLabel = new Label();
                detailsLabel.Dock = DockStyle.Fill;
                detailsLabel.TextAlign = ContentAlignment.MiddleLeft;
                detailsLabel.ForeColor = UiTheme.TextSecondary;
                detailsLabel.Text =
                    "即将启动 " + stdioServers.Count +
                    " 个服务（完整启动配置）";

                TextBox detailsBox = new TextBox();
                detailsBox.Dock = DockStyle.Fill;
                detailsBox.Multiline = true;
                detailsBox.ReadOnly = true;
                detailsBox.AcceptsReturn = true;
                detailsBox.WordWrap = false;
                detailsBox.ScrollBars = ScrollBars.Both;
                detailsBox.MaxLength = int.MaxValue;
                detailsBox.BackColor = Color.White;
                detailsBox.ForeColor = UiTheme.TextPrimary;
                detailsBox.Text = details;
                detailsBox.AccessibleName = "本地 MCP 完整启动配置";

                FlowLayoutPanel actions = new FlowLayoutPanel();
                actions.Dock = DockStyle.Fill;
                actions.FlowDirection = FlowDirection.RightToLeft;
                actions.WrapContents = false;
                Button reject = CreateButton("拒绝", 88);
                reject.DialogResult = DialogResult.No;
                reject.BackColor = UiTheme.Accent;
                reject.FlatAppearance.BorderSize = 0;
                reject.AccessibleName = "拒绝启动本地 MCP 服务";
                Button approve = CreateButton("允许本次启动", 120);
                approve.DialogResult = DialogResult.Yes;
                approve.AccessibleName = "仅允许本次启动本地 MCP 服务";
                actions.Controls.Add(reject);
                actions.Controls.Add(approve);

                layout.Controls.Add(warning, 0, 0);
                layout.Controls.Add(detailsLabel, 0, 1);
                layout.Controls.Add(detailsBox, 0, 2);
                layout.Controls.Add(actions, 0, 3);
                dialog.Controls.Add(layout);
                dialog.AcceptButton = reject;
                dialog.CancelButton = reject;
                dialog.Shown += delegate { reject.Select(); };

                return dialog.ShowDialog(this) == DialogResult.Yes;
            }
        }

        private static string BuildStdioMcpStartupDetails(
            IList<McpServerDefinition> servers)
        {
            StringBuilder details = new StringBuilder();
            foreach (McpServerDefinition server in servers ??
                new List<McpServerDefinition>())
            {
                if (server == null)
                {
                    continue;
                }

                if (details.Length > 0)
                {
                    details.AppendLine();
                    details.AppendLine(
                        "------------------------------------------------------------");
                    details.AppendLine();
                }

                details.Append("服务名称：");
                details.AppendLine(FormatMcpStartupValue(server.Name));
                details.Append("启动命令：");
                details.AppendLine(FormatMcpStartupValue(server.Command));
                details.Append("工作目录：");
                details.AppendLine(string.IsNullOrWhiteSpace(
                    server.WorkingDirectory)
                    ? "（未设置，继承当前工作目录）"
                    : FormatMcpStartupValue(server.WorkingDirectory));
                details.AppendLine(
                    "完整参数（控制字符以转义形式显示）：");
                IList<string> arguments = server.Arguments ??
                    new List<string>();
                if (arguments.Count == 0)
                {
                    details.AppendLine("  （无）");
                }
                else
                {
                    for (int index = 0; index < arguments.Count; index++)
                    {
                        details.Append("  [");
                        details.Append(index + 1);
                        details.Append("] ");
                        details.AppendLine(
                            FormatMcpStartupValue(arguments[index]));
                    }
                }

                details.AppendLine("环境变量名称（值不显示）：");
                IList<string> environmentNames = (server.Environment ??
                    new Dictionary<string, string>())
                    .Keys
                    .OrderBy(
                        name => name,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (environmentNames.Count == 0)
                {
                    details.AppendLine("  （无）");
                }
                else
                {
                    foreach (string name in environmentNames)
                    {
                        details.Append("  ");
                        details.AppendLine(FormatMcpStartupValue(name));
                    }
                }
            }

            return details.ToString();
        }

        private static string FormatMcpStartupValue(string value)
        {
            StringBuilder formatted = new StringBuilder();
            formatted.Append('"');
            foreach (char current in value ?? string.Empty)
            {
                switch (current)
                {
                    case '\\':
                        formatted.Append("\\\\");
                        break;
                    case '"':
                        formatted.Append("\\\"");
                        break;
                    case '\r':
                        formatted.Append("\\r");
                        break;
                    case '\n':
                        formatted.Append("\\n");
                        break;
                    case '\t':
                        formatted.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(current) ||
                            current == '\u2028' ||
                            current == '\u2029')
                        {
                            formatted.Append("\\u");
                            formatted.Append(((int)current).ToString("x4"));
                        }
                        else
                        {
                            formatted.Append(current);
                        }

                        break;
                }
            }

            formatted.Append('"');
            return formatted.ToString();
        }

        private async Task<McpToolResult> ExecuteMcpToolAsync(
            McpRuntime runtime,
            ModelToolCall call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            McpToolDefinition tool = runtime.GetTool(call.Name);
            if (tool == null)
            {
                throw new McpException(
                    "模型请求了未知 MCP 工具：“" + call.Name + "”。");
            }

            if (tool.RequireConfirmation)
            {
                bool approved = await ConfirmMcpToolCallAsync(
                    tool,
                    call.ArgumentsJson,
                    cancellationToken);
                if (!approved)
                {
                    return new McpToolResult
                    {
                        IsError = true,
                        Content = "用户拒绝了本次工具调用。"
                    };
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await runtime.CallToolAsync(
                call.Name,
                call.ArgumentsJson,
                cancellationToken);
        }

        private Task<bool> ConfirmMcpToolCallAsync(
            McpToolDefinition tool,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            if (!InvokeRequired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(ShowMcpApproval(tool, argumentsJson));
            }

            TaskCompletionSource<bool> completion =
                new TaskCompletionSource<bool>();
            CancellationTokenRegistration registration =
                cancellationToken.Register(delegate
                {
                    completion.TrySetCanceled();
                });
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (cancellationToken.IsCancellationRequested ||
                        IsDisposed)
                    {
                        completion.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        completion.TrySetResult(
                            ShowMcpApproval(tool, argumentsJson));
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }

            return DisposeRegistrationAfterAsync(
                completion.Task,
                registration);
        }

        private static async Task<bool> DisposeRegistrationAfterAsync(
            Task<bool> task,
            CancellationTokenRegistration registration)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
            }
        }

        private bool ShowMcpApproval(
            McpToolDefinition tool,
            string argumentsJson)
        {
            string arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? "{}"
                : argumentsJson.Trim();

            using (Form dialog = new Form())
            {
                dialog.Text = "确认 MCP 工具调用";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimumSize = new Size(620, 400);
                dialog.Size = new Size(760, 540);
                dialog.Font = Font;
                dialog.BackColor = UiTheme.WindowBackground;
                dialog.ForeColor = UiTheme.ButtonText;
                dialog.AutoScaleMode = AutoScaleMode.None;
                dialog.ShowInTaskbar = false;
                dialog.MinimizeBox = false;

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(12);
                layout.ColumnCount = 1;
                layout.RowCount = 4;
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

                Label identity = new Label();
                identity.Dock = DockStyle.Fill;
                identity.TextAlign = ContentAlignment.MiddleLeft;
                identity.AutoEllipsis = true;
                identity.ForeColor = UiTheme.ButtonText;
                identity.Text =
                    "服务：" + (tool.ServerName ?? string.Empty) +
                    "\r\n工具：" + (tool.ToolName ?? tool.PublicName);

                Label argumentsLabel = new Label();
                argumentsLabel.Dock = DockStyle.Fill;
                argumentsLabel.TextAlign = ContentAlignment.MiddleLeft;
                argumentsLabel.ForeColor = UiTheme.TextSecondary;
                argumentsLabel.Text = "调用参数（完整内容）";

                TextBox argumentsBox = new TextBox();
                argumentsBox.Dock = DockStyle.Fill;
                argumentsBox.Multiline = true;
                argumentsBox.ReadOnly = true;
                argumentsBox.AcceptsReturn = true;
                argumentsBox.WordWrap = false;
                argumentsBox.ScrollBars = ScrollBars.Both;
                argumentsBox.BackColor = Color.White;
                argumentsBox.ForeColor = UiTheme.TextPrimary;
                argumentsBox.Text = arguments;
                argumentsBox.AccessibleName = "MCP 工具完整调用参数";

                FlowLayoutPanel actions = new FlowLayoutPanel();
                actions.Dock = DockStyle.Fill;
                actions.FlowDirection = FlowDirection.RightToLeft;
                actions.WrapContents = false;
                Button reject = CreateButton("拒绝", 88);
                reject.DialogResult = DialogResult.No;
                Button approve = CreateButton("仅允许本次", 104);
                approve.DialogResult = DialogResult.Yes;
                approve.BackColor = UiTheme.Accent;
                approve.FlatAppearance.BorderSize = 0;
                actions.Controls.Add(reject);
                actions.Controls.Add(approve);

                layout.Controls.Add(identity, 0, 0);
                layout.Controls.Add(argumentsLabel, 0, 1);
                layout.Controls.Add(argumentsBox, 0, 2);
                layout.Controls.Add(actions, 0, 3);
                dialog.Controls.Add(layout);
                dialog.CancelButton = reject;
                dialog.Shown += delegate { reject.Focus(); };

                return dialog.ShowDialog(this) == DialogResult.Yes;
            }
        }

        private string GetCurrentInstruction()
        {
            string instruction = promptTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(instruction))
            {
                return instruction;
            }

            return inputItems.Count > 0
                ? "请分析已添加的资料，并给出清晰、有用的结果。"
                : "请分析以下内容，并给出清晰、有用的结果。";
        }

        private string BuildVisibleUserMessage(string instruction)
        {
            StringBuilder visible = new StringBuilder();
            visible.Append(string.IsNullOrWhiteSpace(instruction)
                ? "请分析已添加的资料。"
                : instruction.Trim());
            if (inputItems.Count == 0)
            {
                return visible.ToString();
            }

            visible.AppendLine();
            visible.AppendLine();
            visible.AppendLine("已授权资料：");
            int displayed = Math.Min(inputItems.Count, 8);
            for (int index = 0; index < displayed; index++)
            {
                InputItem item = inputItems[index];
                visible.Append("- ");
                visible.Append(item.Name);
                visible.Append("（");
                visible.Append(item.GetKindText());
                visible.Append("，");
                visible.Append(item.GetSizeText());
                visible.AppendLine("）");
            }

            if (inputItems.Count > displayed)
            {
                visible.Append("- 另有 ");
                visible.Append(inputItems.Count - displayed);
                visible.AppendLine(" 项");
            }

            if (inputItems.Any(item => item != null &&
                item.Kind != InputKind.Text))
            {
                visible.AppendLine();
                visible.Append(
                    "图片或内联文件只随本轮发送；后续轮次需再次添加。");
            }

            return visible.ToString().TrimEnd();
        }

        private bool TryBuildCombinedPrompt(
            string instruction,
            string systemPrompt,
            out string prompt,
            out string validationMessage,
            out bool fileTextWasTrimmed)
        {
            prompt = string.Empty;
            validationMessage = string.Empty;
            fileTextWasTrimmed = false;

            if (string.IsNullOrWhiteSpace(instruction))
            {
                instruction = "请分析以下内容，并根据内容给出清晰、有用的结果。";
            }
            else
            {
                instruction = instruction.Trim();
            }

            string normalizedSystemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                ? string.Empty
                : systemPrompt.Trim();
            long systemCharacters = ConversationContextBudget.CountCharacters(
                normalizedSystemPrompt);
            if (systemCharacters >= MaximumConversationContextCharacters)
            {
                validationMessage =
                    "已启用技能生成的系统提示达到或超过 48,000 字符预算。" +
                    "请在“技能 / MCP”中缩短或停用相关技能后再发送。";
                return false;
            }

            long promptCharacterBudget =
                MaximumConversationContextCharacters - systemCharacters;
            string prefix = "用户要求：\r\n" + instruction + "\r\n";
            long prefixCharacters = ConversationContextBudget.CountCharacters(
                prefix);
            if (prefixCharacters > promptCharacterBudget)
            {
                validationMessage =
                    "当前文字描述与已启用技能合计超过 48,000 字符预算。" +
                    "请缩短文字描述或停用部分技能后再发送。";
                return false;
            }

            IList<InputItem> textItems = inputItems
                .Where(item => item != null && item.Kind == InputKind.Text)
                .ToList();
            string textIntroduction = textItems.Count == 0
                ? string.Empty
                : "\r\n以下资料由用户主动拖入或粘贴后提取，" +
                    "只包含文件名和实际内容，不包含本地路径：\r\n";
            string binarySuffix = BuildBinaryPromptSuffix();
            long fixedPromptCharacters = AddSaturated(
                prefixCharacters,
                AddSaturated(
                    ConversationContextBudget.CountCharacters(textIntroduction),
                    ConversationContextBudget.CountCharacters(binarySuffix)));
            if (fixedPromptCharacters > promptCharacterBudget)
            {
                validationMessage =
                    "当前文字描述、资料名称与已启用技能合计超过 " +
                    "48,000 字符预算。请缩短文字描述、减少资料或停用部分技能。";
                return false;
            }

            StringBuilder textBody = new StringBuilder();
            long availableTextBodyCharacters = promptCharacterBudget -
                fixedPromptCharacters;
            fileTextWasTrimmed = !TextItemsFitWithinCharacterBudget(
                textItems,
                availableTextBodyCharacters);
            long textContentBudget = availableTextBodyCharacters;
            if (fileTextWasTrimmed)
            {
                long noticeCharacters = ConversationContextBudget.CountCharacters(
                    TruncatedFileTextNotice);
                if (noticeCharacters > availableTextBodyCharacters)
                {
                    validationMessage =
                        "当前文字描述与已启用技能没有为文件正文留下足够的字符预算。" +
                        "请缩短文字描述或停用部分技能后再发送。";
                    return false;
                }

                textContentBudget -= noticeCharacters;
            }

            long usedTextBodyCharacters = 0L;
            foreach (InputItem item in textItems)
            {
                string header = "\r\n===== 内容开始：" + item.Name +
                    " =====\r\n";
                string footer = "\r\n===== 内容结束：" + item.Name +
                    " =====\r\n";
                long framingCharacters = AddSaturated(
                    ConversationContextBudget.CountCharacters(header),
                    ConversationContextBudget.CountCharacters(footer));
                long remainingCharacters = textContentBudget -
                    usedTextBodyCharacters;
                if (framingCharacters > remainingCharacters)
                {
                    break;
                }

                string content = item.TextContent ?? string.Empty;
                long contentBudget = remainingCharacters - framingCharacters;
                string includedContent = TakeLeadingCharacters(
                    content,
                    contentBudget);
                textBody.Append(header);
                textBody.Append(includedContent);
                textBody.Append(footer);
                usedTextBodyCharacters = AddSaturated(
                    usedTextBodyCharacters,
                    AddSaturated(
                        framingCharacters,
                        ConversationContextBudget.CountCharacters(
                            includedContent)));
                if (includedContent.Length < content.Length)
                {
                    break;
                }
            }

            if (fileTextWasTrimmed)
            {
                textBody.Append(TruncatedFileTextNotice);
            }

            prompt = prefix + textIntroduction + textBody.ToString() +
                binarySuffix;
            return true;
        }

        private string BuildBinaryPromptSuffix()
        {
            IList<InputItem> binaryItems = inputItems
                .Where(item => item != null && item.Kind != InputKind.Text)
                .ToList();
            if (binaryItems.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder result = new StringBuilder();
            result.AppendLine();
            result.AppendLine(
                "以下图片或内联文件仅随本轮发送；后续轮次如需重新分析，" +
                "请再次主动添加：");
            foreach (InputItem item in binaryItems)
            {
                result.Append("- ");
                result.Append(item.Name ?? string.Empty);
                result.Append("（");
                result.Append(item.GetKindText());
                result.Append("，");
                result.Append(item.GetSizeText());
                result.AppendLine("）");
            }

            return result.ToString();
        }

        private ConversationContextSelection SelectConversationTextContext(
            IList<ConversationMessage> messages,
            string systemPrompt,
            string prompt)
        {
            // Binary data URI size is a transport concern, not a text-token
            // estimate. ModelClient keeps the independent 32 MB request guard.
            return ConversationContextBudget.SelectRecentCompleteTurns(
                messages,
                MaximumConversationContextCharacters,
                ConversationContextBudget.CountCharacters(systemPrompt),
                ConversationContextBudget.CountCharacters(prompt),
                0L);
        }

        private static bool TextItemsFitWithinCharacterBudget(
            IEnumerable<InputItem> textItems,
            long maximumCharacters)
        {
            long usedCharacters = 0L;
            foreach (InputItem item in textItems ?? new InputItem[0])
            {
                string header = "\r\n===== 内容开始：" + item.Name +
                    " =====\r\n";
                string footer = "\r\n===== 内容结束：" + item.Name +
                    " =====\r\n";
                long framingCharacters = AddSaturated(
                    ConversationContextBudget.CountCharacters(header),
                    ConversationContextBudget.CountCharacters(footer));
                long remainingCharacters = maximumCharacters - usedCharacters;
                if (framingCharacters > remainingCharacters)
                {
                    return false;
                }

                usedCharacters += framingCharacters;
                remainingCharacters = maximumCharacters - usedCharacters;
                long contentCharacters = CountCharactersUpTo(
                    item.TextContent,
                    remainingCharacters + 1L);
                if (contentCharacters > remainingCharacters)
                {
                    return false;
                }

                usedCharacters += contentCharacters;
            }

            return true;
        }

        private static long CountCharactersUpTo(
            string value,
            long maximumCharacters)
        {
            if (string.IsNullOrEmpty(value) || maximumCharacters <= 0L)
            {
                return 0L;
            }

            long count = 0L;
            for (int index = 0;
                index < value.Length && count < maximumCharacters;
                index++)
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

        private static string TakeLeadingCharacters(
            string value,
            long maximumCharacters)
        {
            if (string.IsNullOrEmpty(value) || maximumCharacters <= 0L)
            {
                return string.Empty;
            }

            int end = 0;
            long count = 0L;
            while (end < value.Length && count < maximumCharacters)
            {
                if (char.IsHighSurrogate(value[end]) &&
                    end + 1 < value.Length &&
                    char.IsLowSurrogate(value[end + 1]))
                {
                    end += 2;
                }
                else
                {
                    end++;
                }

                count++;
            }

            return end == value.Length
                ? value
                : value.Substring(0, end);
        }

        private static long AddSaturated(long left, long right)
        {
            return left > long.MaxValue - right
                ? long.MaxValue
                : left + right;
        }

        private void SetGeneratingState(bool generating)
        {
            generateButton.Enabled = !generating;
            stopButton.Enabled = generating;
            promptActionsButton.Enabled = !generating && !isAddingFiles;
            SetInputButtonsEnabled(!generating && !isAddingFiles);
            SetSessionNavigationEnabled(!generating && !isAddingFiles);
            deleteSessionButton.Enabled = !generating &&
                conversationStore.Sessions.Count > 1;
            renameSessionButton.Enabled = !generating &&
                conversationStore.CurrentSession != null;
            backupSessionsButton.Enabled = !generating &&
                conversationStore.Sessions.Count > 0;
            restoreSessionsButton.Enabled = !generating;
            testConnectionButton.Enabled = !generating &&
                HasCompleteConnectionSettings();
            endpointTextBox.Enabled = !generating;
            apiKeyTextBox.Enabled = !generating;
            modelTextBox.Enabled = !generating;
            settingsDialog.ModelProfilesButton.Enabled = !generating;
            settingsDialog.UninstallButton.Enabled = !generating;
            settingsDialog.SendShortcutComboBox.Enabled = !generating;
            if (extensionsButton != null)
            {
                extensionsButton.Enabled = !generating &&
                    connectionTestCancellation == null &&
                    !isAddingFiles;
            }
            progressBar.Visible = generating || isAddingFiles;
            UpdateOutputButtons(generating);
        }

        private void SetSessionNavigationEnabled(bool enabled)
        {
            if (sessionListBox != null)
            {
                sessionListBox.Enabled = enabled;
            }

            if (sessionSearchTextBox != null)
            {
                sessionSearchTextBox.Enabled = enabled;
            }

            if (newSessionButton != null)
            {
                newSessionButton.Enabled = enabled;
            }

            if (settingsButton != null)
            {
                settingsButton.Enabled = enabled;
            }
        }

        private void SetInputButtonsEnabled(bool enabled)
        {
            bool actual = enabled &&
                generationCancellation == null &&
                connectionTestCancellation == null &&
                !isAddingFiles;
            if (addFileButton != null)
            {
                addFileButton.Enabled = actual;
            }
            if (fileDropTargetPanel != null)
            {
                fileDropTargetPanel.Enabled = actual;
            }
            if (pathTextBox != null)
            {
                pathTextBox.Enabled = actual;
            }
            if (readPathButton != null)
            {
                readPathButton.Enabled = actual;
            }
            if (inputListView != null)
            {
                inputListView.Enabled = actual;
            }
        }

        private string DeactivateStreamingOutput()
        {
            activeGenerationSequence = 0;
            if (streamFlushTimer != null)
            {
                streamFlushTimer.Stop();
            }

            lock (streamOutputSync)
            {
                string accumulated = streamedResponse == null
                    ? string.Empty
                    : streamedResponse.ToString();
                pendingStreamOutput.Length = 0;
                return accumulated;
            }
        }

        private void AppendOutput(string value)
        {
            if (string.IsNullOrEmpty(value) || IsDisposed)
            {
                return;
            }

            int sequence = activeGenerationSequence;
            if (sequence == 0)
            {
                return;
            }

            AppendOutputForGeneration(value, sequence);
        }

        private void AppendOutputForGeneration(string value, int sequence)
        {
            if (string.IsNullOrEmpty(value) || IsDisposed ||
                sequence == 0 || sequence != activeGenerationSequence)
            {
                return;
            }

            lock (streamOutputSync)
            {
                if (sequence != activeGenerationSequence ||
                    streamedResponse == null)
                {
                    return;
                }

                streamedResponse.Append(value);
                pendingStreamOutput.Append(value);
            }
        }

        private void FlushPendingOutput()
        {
            if (outputTextBox == null || IsDisposed)
            {
                return;
            }

            int sequence = activeGenerationSequence;
            string value;
            lock (streamOutputSync)
            {
                if (sequence == 0 ||
                    sequence != activeGenerationSequence ||
                    pendingStreamOutput.Length == 0)
                {
                    return;
                }

                value = pendingStreamOutput.ToString();
                pendingStreamOutput.Length = 0;
            }

            int previousSelectionStart = outputTextBox.SelectionStart;
            int previousSelectionLength = outputTextBox.SelectionLength;
            NativePoint previousScrollPosition =
                GetRichTextScrollPosition(outputTextBox);
            outputTextBox.SelectionStart = outputTextBox.TextLength;
            outputTextBox.SelectionLength = 0;
            outputTextBox.AppendText(value);
            if (followStreamTail)
            {
                outputTextBox.SelectionStart = outputTextBox.TextLength;
                outputTextBox.ScrollToCaret();
            }
            else
            {
                outputTextBox.Select(
                    Math.Min(previousSelectionStart, outputTextBox.TextLength),
                    Math.Min(
                        previousSelectionLength,
                        Math.Max(
                            0,
                            outputTextBox.TextLength -
                                previousSelectionStart)));
                SetRichTextScrollPosition(
                    outputTextBox,
                    previousScrollPosition);
            }
        }

        private static NativePoint GetRichTextScrollPosition(
            RichTextBox textBox)
        {
            NativePoint position = new NativePoint();
            if (textBox != null && textBox.IsHandleCreated)
            {
                SendMessage(
                    textBox.Handle,
                    EmGetScrollPosition,
                    IntPtr.Zero,
                    ref position);
            }

            return position;
        }

        private static void SetRichTextScrollPosition(
            RichTextBox textBox,
            NativePoint position)
        {
            if (textBox == null || !textBox.IsHandleCreated)
            {
                return;
            }

            SendMessage(
                textBox.Handle,
                EmSetScrollPosition,
                IntPtr.Zero,
                ref position);
        }

        private void SetStatus(string value)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<string>(SetStatus), value);
                }
                catch (InvalidOperationException)
                {
                    // Window is closing.
                }

                return;
            }

            statusLabel.Text = string.IsNullOrEmpty(value) ? "就绪" : value;
        }

        private static void ShowValidation(string message, Control control)
        {
            MessageBox.Show(
                message,
                "请检查输入",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            if (control != null)
            {
                control.Focus();
            }
        }

        private void OnCopyOutputClick(object sender, EventArgs args)
        {
            string output = GetLatestAssistantOutput();
            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            try
            {
                Clipboard.SetText(output);
                SetStatus("最新回复已复制到剪贴板");
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "复制失败：" + exception.Message,
                    "复制结果",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private ContextMenuStrip CreateExportMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(CreateExportItem(
                "最新回复 · Markdown",
                delegate { ExportLatestText(true); }));
            menu.Items.Add(CreateExportItem(
                "最新回复 · 文本",
                delegate { ExportLatestText(false); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(CreateExportItem(
                "最新回复 · Word",
                delegate { ExportDocument(false, false); }));
            menu.Items.Add(CreateExportItem(
                "最新回复 · PDF",
                delegate { ExportDocument(false, true); }));
            menu.Items.Add(CreateExportItem(
                "整个会话 · Word",
                delegate { ExportDocument(true, false); }));
            menu.Items.Add(CreateExportItem(
                "整个会话 · PDF",
                delegate { ExportDocument(true, true); }));

            menu.Items.Add(CreateExportItem(
                "\u6700\u65b0\u56de\u590d \u00b7 PowerPoint",
                delegate { ExportPresentation(false); }));
            menu.Items.Add(CreateExportItem(
                "\u6574\u4e2a\u4f1a\u8bdd \u00b7 PowerPoint",
                delegate { ExportPresentation(true); }));
            menu.Items.Add(CreateExportItem(
                "\u6700\u65b0\u56de\u590d \u00b7 XMind \u601d\u7ef4\u5bfc\u56fe",
                delegate { ExportMindMap(false); }));
            menu.Items.Add(CreateExportItem(
                "\u6574\u4e2a\u4f1a\u8bdd \u00b7 XMind \u601d\u7ef4\u5bfc\u56fe",
                delegate { ExportMindMap(true); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(CreateExportItem(
                "表格 · Excel 工作簿",
                delegate { ExportTable(true); }));
            menu.Items.Add(CreateExportItem(
                "表格 · CSV",
                delegate { ExportTable(false); }));
            return menu;
        }

        private ToolStripMenuItem CreateExportItem(
            string text,
            EventHandler handler)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += handler;
            return item;
        }

        private void OnSaveOutputClick(object sender, EventArgs args)
        {
            ExportLatestText(true);
        }

        private void OnExportWordClick(object sender, EventArgs args)
        {
            ExportDocument(false, false);
        }

        private void OnExportTableClick(object sender, EventArgs args)
        {
            ExportTable(true);
        }

        private void ExportLatestText(bool markdown)
        {
            string output = GetLatestAssistantOutput();
            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            string extension = markdown ? ".md" : ".txt";
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = markdown ? "保存 Markdown 回复" : "保存文本回复";
                dialog.Filter = markdown
                    ? "Markdown 文件|*.md|所有文件|*.*"
                    : "文本文件|*.txt|所有文件|*.*";
                dialog.DefaultExt = extension.TrimStart('.');
                dialog.AddExtension = true;
                dialog.FileName = "模型输出_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + extension;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    AtomicFile.WriteAllText(
                        dialog.FileName,
                        output,
                        new UTF8Encoding(true));
                    SetStatus(markdown ? "Markdown 已导出" : "文本已导出");
                }
                catch (Exception exception)
                {
                    ShowSaveError(exception);
                }
            }
        }

        private void ExportDocument(bool entireConversation, bool pdf)
        {
            string content = entireConversation
                ? BuildConversationMarkdown()
                : GetLatestAssistantOutput();
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            string extension = pdf ? ".pdf" : ".docx";
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = (entireConversation ? "导出整个会话 · " :
                    "导出最新回复 · ") + (pdf ? "PDF" : "Word");
                dialog.Filter = pdf
                    ? "PDF 文档|*.pdf"
                    : "Word 文档|*.docx";
                dialog.DefaultExt = extension.TrimStart('.');
                dialog.AddExtension = true;
                dialog.FileName = (entireConversation
                    ? "FilePromptAI会话_"
                    : "FilePromptAI回复_") +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + extension;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    if (pdf)
                    {
                        PdfExporter.Export(content, dialog.FileName);
                    }
                    else
                    {
                        DocxExporter.Export(content, dialog.FileName);
                    }

                    SetStatus((pdf ? "PDF" : "Word") + " 已导出");
                }
                catch (Exception exception)
                {
                    ShowSaveError(exception);
                }
            }
        }

        private void ExportPresentation(bool entireConversation)
        {
            string content = entireConversation
                ? BuildConversationMarkdown()
                : GetLatestAssistantOutput();
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = (entireConversation ? "\u5bfc\u51fa\u6574\u4e2a\u4f1a\u8bdd \u00b7 " :
                    "\u5bfc\u51fa\u6700\u65b0\u56de\u590d \u00b7 ") + "PowerPoint";
                dialog.Filter = "PowerPoint \u6f14\u793a\u6587\u7a3f|*.pptx";
                dialog.DefaultExt = "pptx";
                dialog.AddExtension = true;
                dialog.FileName = (entireConversation
                    ? "FilePromptAI\u4f1a\u8bdd_"
                    : "FilePromptAI\u56de\u590d_") +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pptx";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    PptxExporter.Export(content, dialog.FileName);
                    SetStatus("PowerPoint \u5df2\u5bfc\u51fa");
                }
                catch (Exception exception)
                {
                    ShowSaveError(exception);
                }
            }
        }

        private void ExportMindMap(bool entireConversation)
        {
            string content = entireConversation
                ? BuildConversationMarkdown()
                : GetLatestAssistantOutput();
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = (entireConversation ? "\u5bfc\u51fa\u6574\u4e2a\u4f1a\u8bdd \u00b7 " :
                    "\u5bfc\u51fa\u6700\u65b0\u56de\u590d \u00b7 ") + "XMind \u601d\u7ef4\u5bfc\u56fe";
                dialog.Filter = "XMind \u601d\u7ef4\u5bfc\u56fe|*.xmind";
                dialog.DefaultExt = "xmind";
                dialog.AddExtension = true;
                dialog.FileName = (entireConversation
                    ? "FilePromptAI\u4f1a\u8bdd_"
                    : "FilePromptAI\u56de\u590d_") +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xmind";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    XMindExporter.Export(content, dialog.FileName);
                    SetStatus("XMind \u601d\u7ef4\u5bfc\u56fe\u5df2\u5bfc\u51fa");
                }
                catch (Exception exception)
                {
                    ShowSaveError(exception);
                }
            }
        }
        private void ExportTable(bool xlsx)
        {
            string output = GetLatestAssistantOutput();
            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            MarkdownDocument document = MarkdownDocument.Parse(output);
            if (document.Tables == null || document.Tables.Count == 0)
            {
                MessageBox.Show(
                    "当前输出中没有识别到 Markdown 表格。",
                    "导出表格",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            string extension = xlsx ? ".xlsx" : ".csv";
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = xlsx ? "导出 Excel 工作簿" : "导出 CSV 表格";
                dialog.Filter = xlsx
                    ? "Excel 工作簿|*.xlsx"
                    : "CSV 表格|*.csv";
                dialog.DefaultExt = extension.TrimStart('.');
                dialog.AddExtension = true;
                dialog.FileName = "FilePromptAI表格_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + extension;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    if (xlsx)
                    {
                        XlsxExporter.Export(document, dialog.FileName);
                    }
                    else
                    {
                        CsvExporter.Export(document, dialog.FileName);
                    }

                    SetStatus(xlsx ? "Excel 工作簿已导出" : "CSV 已导出");
                }
                catch (Exception exception)
                {
                    ShowSaveError(exception);
                }
            }
        }

        private string BuildConversationMarkdown()
        {
            ConversationSession session = conversationStore.CurrentSession;
            if (session == null || session.Messages == null ||
                session.Messages.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder markdown = new StringBuilder();
            markdown.AppendLine("# " + session.Title);
            markdown.AppendLine();
            foreach (ConversationMessage message in session.Messages)
            {
                if (message == null)
                {
                    continue;
                }

                markdown.AppendLine(
                    message.Role == "assistant" ? "## 模型" : "## 你");
                markdown.AppendLine();
                // Export the complete stored message. The on-screen transcript
                // intentionally folds long user material, but a whole-session
                // document must retain the source text and table data.
                markdown.AppendLine(message.Content ?? string.Empty);
                markdown.AppendLine();
            }

            return markdown.ToString();
        }

        private void ShowSaveError(Exception exception)
        {
            MessageBox.Show(
                "保存失败：" + exception.Message,
                "保存结果",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs args)
        {
            if (!isClosing && !exitConfirmationGranted &&
                !ConfirmExitIfNeeded())
            {
                args.Cancel = true;
                return;
            }

            isClosing = true;
            DisableFileDropTarget();

            if (sessionSearchTimer != null)
            {
                sessionSearchTimer.Stop();
                sessionSearchTimer.Dispose();
                sessionSearchTimer = null;
            }

            if (streamFlushTimer != null)
            {
                streamFlushTimer.Stop();
                streamFlushTimer.Dispose();
                streamFlushTimer = null;
            }

            if (contextSummaryTimer != null)
            {
                contextSummaryTimer.Stop();
                contextSummaryTimer.Dispose();
                contextSummaryTimer = null;
            }

            if (contextSummaryToolTip != null)
            {
                contextSummaryToolTip.Dispose();
                contextSummaryToolTip = null;
            }

            activeGenerationSequence = 0;
            lock (streamOutputSync)
            {
                pendingStreamOutput.Length = 0;
            }

            if (generationCancellation != null)
            {
                generationCancellation.Cancel();
            }

            if (connectionTestCancellation != null)
            {
                connectionTestCancellation.Cancel();
            }

            if (fileAddCancellation != null)
            {
                fileAddCancellation.Cancel();
            }

            SaveSettings();
            try
            {
                if (!conversationStore.IsWriteBlocked)
                {
                    conversationStore.Save();
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "会话记录最后一次保存失败：\r\n\r\n" +
                        exception.Message,
                    "退出 FilePrompt AI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            modelClient.Dispose();
            if (settingsDialog != null)
            {
                settingsDialog.Dispose();
                settingsDialog = null;
            }
            if (pathInputDialog != null)
            {
                pathInputDialog.Dispose();
                pathInputDialog = null;
            }
        }

        private bool NeedsExitConfirmation()
        {
            return IsBusy || HasUnsentInput();
        }

        private bool ConfirmExitIfNeeded()
        {
            if (!NeedsExitConfirmation())
            {
                return true;
            }

            string message = IsBusy
                ? "当前仍有任务运行，关闭程序会停止任务，" +
                    "并丢弃尚未写入会话的本轮内容。\r\n\r\n" +
                    "确定要退出吗？"
                : "当前还有未发送的文字或资料，关闭后不会保留。" +
                    "\r\n\r\n确定要退出吗？";
            return MessageBox.Show(
                GetSettingsActionOwner(),
                message,
                "退出 FilePrompt AI",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private bool HasUnsentInput()
        {
            if ((promptTextBox != null &&
                    !string.IsNullOrWhiteSpace(promptTextBox.Text)) ||
                (inputItems != null && inputItems.Count > 0))
            {
                return true;
            }

            foreach (SessionDraft draft in sessionDrafts.Values)
            {
                if (draft != null &&
                    (!string.IsNullOrWhiteSpace(draft.Prompt) ||
                        (draft.Items != null && draft.Items.Count > 0)))
                {
                    return true;
                }
            }

            return false;
        }

        private void ShowConversationWriteBlocked()
        {
            string message = conversationStore.LoadWarning;
            MessageBox.Show(
                this,
                string.IsNullOrWhiteSpace(message)
                    ? "会话历史处于只读保护状态，请处理原文件后重新启动程序。"
                    : message,
                "会话历史只读保护",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            SetStatus("会话历史处于只读保护状态，当前操作未执行。");
        }

        private static string PromptForText(
            IWin32Window owner,
            string title,
            string labelText,
            string initialValue)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = title;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(360, 116);

                Label label = new Label();
                label.Text = labelText;
                label.AutoSize = true;
                label.Location = new Point(12, 14);

                TextBox input = new TextBox();
                input.Text = initialValue ?? string.Empty;
                input.Location = new Point(12, 38);
                input.Width = 336;

                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(188, 76);
                ok.Width = 74;

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(274, 76);
                cancel.Width = 74;

                dialog.Controls.Add(label);
                dialog.Controls.Add(input);
                dialog.Controls.Add(ok);
                dialog.Controls.Add(cancel);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                return dialog.ShowDialog(owner) == DialogResult.OK
                    ? input.Text
                    : null;
            }
        }
    }
}
