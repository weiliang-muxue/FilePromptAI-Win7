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
        private const int MaxCombinedTextCharacters = 4000000;
        private const int MaxDisplayedUserCharacters = 8000;
        private const int MaximumFilesPerAdd = 128;
        private const int MaximumPathCandidates = 4096;
        private const int PathResolutionTimeoutMilliseconds = 15000;
        // Keep enough room below ModelClient's 32 MB serialized-request limit
        // for JSON, tool definitions, and provider-specific envelope fields.
        private const long MaximumConversationContextCharacters = 48000L;
        private const float ExpandedSettingsHeight = 94F;
        private const float MinimumOutputAreaHeight = 104F;
        // The compact composer still keeps both the action and path rows
        // usable; only the empty ListView is collapsed.
        private const float CompactInputsHeight = 96F;
        private const float ExpandedInputsHeight = 150F;
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
        private RowStyle settingsRowStyle;
        private RowStyle inputsAreaRowStyle;
        private RowStyle promptAreaRowStyle;
        private Control settingsPanel;
        private Control inputsPanel;
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
        private Button pasteButton;
        private Button removeButton;
        private Button clearButton;
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
        private Button toggleSettingsButton;
        private Button testConnectionButton;
        private Button extensionsButton;
        private Button moreButton;
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
        private bool isLoadingSession;
        private bool isUpdatingConversationRows;
        private bool settingsExpanded;
        private bool followStreamTail;
        private string sendShortcutMode;
        private GroupBox promptGroup;

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
            AllowDrop = true;
            KeyPreview = true;

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
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
            Shown += delegate { ResizeInputColumns(); };
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
            backupSessionsButton = CreateButton("备份", 96);
            backupSessionsButton.Click += OnBackupSessionsClick;
            restoreSessionsButton = CreateButton("恢复", 96);
            restoreSessionsButton.Click += OnRestoreSessionsClick;

            ContextMenuStrip sessionMenu = new ContextMenuStrip();
            ToolStripMenuItem renameItem =
                new ToolStripMenuItem("重命名会话...");
            renameItem.Click += OnRenameSessionClick;
            ToolStripMenuItem deleteItem =
                new ToolStripMenuItem("删除当前会话...");
            deleteItem.ForeColor = UiTheme.Danger;
            deleteItem.Click += OnDeleteSessionClick;
            ToolStripMenuItem backupItem =
                new ToolStripMenuItem("备份全部会话...");
            backupItem.Click += OnBackupSessionsClick;
            ToolStripMenuItem restoreItem =
                new ToolStripMenuItem("恢复会话备份...");
            restoreItem.Click += OnRestoreSessionsClick;
            sessionMenu.Items.Add(renameItem);
            sessionMenu.Items.Add(deleteItem);
            sessionMenu.Items.Add(new ToolStripSeparator());
            sessionMenu.Items.Add(backupItem);
            sessionMenu.Items.Add(restoreItem);
            sessionMenu.Opening += delegate
            {
                bool hasSession = conversationStore.CurrentSession != null;
                renameItem.Enabled = hasSession && !IsBusy;
                deleteItem.Enabled = hasSession &&
                    conversationStore.Sessions.Count > 1 && !IsBusy;
                backupItem.Enabled = hasSession && !IsBusy;
                restoreItem.Enabled = !IsBusy;
            };
            sessionListBox.ContextMenuStrip = sessionMenu;
            layout.Controls.Add(sessionListBox, 0, 3);

            TableLayoutPanel actions = new TableLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.ColumnCount = 1;
            actions.RowCount = 1;
            actions.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100F));
            Button sessionActionsButton = CreateButton("会话管理", 120);
            sessionActionsButton.Dock = DockStyle.Fill;
            sessionActionsButton.AccessibleName = "打开会话管理菜单";
            sessionActionsButton.Click += delegate
            {
                sessionMenu.Show(
                    sessionActionsButton,
                    new Point(0, -sessionMenu.Height));
            };
            actions.Controls.Add(sessionActionsButton, 0, 0);
            layout.Controls.Add(actions, 0, 4);

            panel.Controls.Add(layout);
            return panel;
        }

        private Control CreateWorkspace()
        {
            workspaceLayout = new TableLayoutPanel();
            workspaceLayout.Dock = DockStyle.Fill;
            workspaceLayout.Padding = new Padding(16, 12, 16, 8);
            workspaceLayout.ColumnCount = 1;
            workspaceLayout.RowCount = 5;
            workspaceLayout.RowStyles.Add(
                new RowStyle(SizeType.Absolute, 52F));
            settingsRowStyle = new RowStyle(
                SizeType.Absolute,
                0F);
            workspaceLayout.RowStyles.Add(
                new RowStyle(SizeType.Absolute, 40F));
            workspaceLayout.RowStyles.Add(settingsRowStyle);
            workspaceLayout.RowStyles.Add(
                new RowStyle(SizeType.Percent, 100F));
            workspaceLayout.RowStyles.Add(
                new RowStyle(SizeType.Absolute, 24F));

            settingsPanel = CreateSettingsPanel();
            settingsExpanded = false;
            settingsPanel.Visible = false;
            workspaceLayout.Controls.Add(CreateHeader(), 0, 0);
            workspaceLayout.Controls.Add(CreateContextSummaryPanel(), 0, 1);
            workspaceLayout.Controls.Add(settingsPanel, 0, 2);
            workspaceLayout.Controls.Add(CreateConversationArea(), 0, 3);
            workspaceLayout.Controls.Add(CreateStatusBar(), 0, 4);
            workspaceLayout.Resize += delegate
            {
                UpdateHeaderActionsLayout();
            };
            return workspaceLayout;
        }

        private Control CreateHeader()
        {
            TableLayoutPanel panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = UiTheme.WindowBackground;
            panel.ColumnCount = 2;
            panel.RowCount = 1;
            panel.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(
                new ColumnStyle(SizeType.AutoSize));

            Panel titlePanel = new Panel();
            titlePanel.Dock = DockStyle.Fill;

            sessionTitleLabel = new Label();
            sessionTitleLabel.Text = "新会话";
            sessionTitleLabel.ForeColor = UiTheme.TextPrimary;
            sessionTitleLabel.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
            sessionTitleLabel.AutoSize = false;
            sessionTitleLabel.Location = new Point(4, 4);
            sessionTitleLabel.Size = new Size(440, 24);
            sessionTitleLabel.AutoEllipsis = true;
            sessionTitleLabel.Anchor = AnchorStyles.Left |
                AnchorStyles.Top | AnchorStyles.Right;

            connectionStatusLabel = new Label();
            connectionStatusLabel.Text = "未连接";
            connectionStatusLabel.ForeColor = UiTheme.TextMuted;
            connectionStatusLabel.AutoSize = false;
            connectionStatusLabel.Location = new Point(5, 29);
            connectionStatusLabel.Size = new Size(440, 20);
            connectionStatusLabel.AutoEllipsis = true;
            connectionStatusLabel.Anchor = AnchorStyles.Left |
                AnchorStyles.Top | AnchorStyles.Right;

            titlePanel.Resize += delegate
            {
                int width = Math.Max(40, titlePanel.ClientSize.Width - 8);
                sessionTitleLabel.Width = width;
                connectionStatusLabel.Width = width;
            };

            titlePanel.Controls.Add(sessionTitleLabel);
            titlePanel.Controls.Add(connectionStatusLabel);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = false;
            actions.AutoSize = true;
            actions.Padding = new Padding(0, 8, 0, 0);

            testConnectionButton = CreateButton("测试连接", 84);
            testConnectionButton.AccessibleName = "测试模型连接";
            testConnectionButton.Click += OnTestConnectionClick;

            extensionsButton = CreateButton("技能 / MCP", 92);
            extensionsButton.AccessibleName = "管理离线技能和 MCP 服务";
            extensionsButton.Click += OnExtensionsClick;

            toggleSettingsButton = CreateButton("连接设置", 84);
            toggleSettingsButton.AccessibleName = "展开或收起连接配置";
            toggleSettingsButton.Click += delegate
            {
                SetSettingsExpanded(!settingsExpanded);
            };

            moreButton = CreateButton("更多", 64);
            moreButton.AccessibleName = "更多应用操作";
            ContextMenuStrip moreMenu = new ContextMenuStrip();
            ToolStripMenuItem profilesItem =
                new ToolStripMenuItem("模型配置...");
            profilesItem.AccessibleName = "保存、选择或删除模型配置";
            profilesItem.Click += OnModelProfilesClick;
            moreMenu.Items.Add(profilesItem);
            ToolStripMenuItem testItem =
                new ToolStripMenuItem("测试模型连接");
            testItem.AccessibleName = "测试当前模型连接";
            testItem.Click += OnTestConnectionClick;
            moreMenu.Items.Add(testItem);
            ToolStripMenuItem extensionsItem =
                new ToolStripMenuItem("技能 / MCP...");
            extensionsItem.AccessibleName = "管理离线技能和 MCP 服务";
            extensionsItem.Click += OnExtensionsClick;
            moreMenu.Items.Add(extensionsItem);
            ToolStripMenuItem settingsItem =
                new ToolStripMenuItem("连接设置...");
            settingsItem.AccessibleName = "展开或收起连接配置";
            settingsItem.Click += delegate
            {
                SetSettingsExpanded(!settingsExpanded);
            };
            moreMenu.Items.Add(settingsItem);
            moreMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem uninstallItem =
                new ToolStripMenuItem("卸载 FilePrompt AI...");
            uninstallItem.AccessibleName = "卸载 FilePrompt AI";
            uninstallItem.Click += OnUninstallClick;
            moreMenu.Items.Add(uninstallItem);
            moreButton.ContextMenuStrip = moreMenu;
            moreButton.Click += delegate
            {
                moreMenu.Show(
                    moreButton,
                    new Point(0, moreButton.Height));
            };

            actions.Controls.Add(extensionsButton);
            actions.Controls.Add(toggleSettingsButton);
            actions.Controls.Add(moreButton);
            panel.Controls.Add(titlePanel, 0, 0);
            panel.Controls.Add(actions, 1, 0);
            UpdateHeaderActionsLayout();
            return panel;
        }

        private void UpdateHeaderActionsLayout()
        {
            if (workspaceLayout == null || extensionsButton == null ||
                toggleSettingsButton == null || moreButton == null)
            {
                return;
            }

            // At the minimum supported width, keep the title and context
            // visible and expose the secondary actions from one menu.
            bool compact = workspaceLayout.ClientSize.Width < 720;
            extensionsButton.Visible = !compact;
            toggleSettingsButton.Visible = !compact;
            moreButton.Text = compact ? "菜单" : "更多";
        }

        private Control CreateContextSummaryPanel()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = UiTheme.PanelAltBackground;
            panel.Padding = new Padding(10, 4, 10, 4);
            panel.Margin = new Padding(0, 0, 0, 4);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label heading = new Label();
            heading.Text = "上下文";
            heading.Dock = DockStyle.Fill;
            heading.TextAlign = ContentAlignment.MiddleLeft;
            heading.ForeColor = UiTheme.Accent;
            heading.Font = new Font(Font, FontStyle.Bold);

            contextSummaryLabel = new Label();
            contextSummaryLabel.Text =
                "模型未配置 · 0 条消息 · 技能 0 · MCP 0 · 资料 0 · 历史 0 · 本轮 0 · 上限 48,000";
            contextSummaryLabel.Dock = DockStyle.Fill;
            contextSummaryLabel.TextAlign = ContentAlignment.MiddleLeft;
            contextSummaryLabel.ForeColor = UiTheme.TextSecondary;
            contextSummaryLabel.Font = new Font(Font.FontFamily, 8.5F);
            contextSummaryLabel.AutoEllipsis = true;
            contextSummaryLabel.AccessibleName = "当前会话上下文摘要";
            contextSummaryToolTip = new ToolTip();
            contextSummaryToolTip.InitialDelay = 300;
            contextSummaryToolTip.ReshowDelay = 100;
            contextSummaryToolTip.AutoPopDelay = 15000;
            contextSummaryToolTip.ShowAlways = true;

            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(contextSummaryLabel, 1, 0);
            panel.Controls.Add(layout);
            return panel;
        }

        private Control CreateSettingsPanel()
        {
            GroupBox group = new GroupBox();
            group.Text = "连接设置  ·  仅保存在本机当前用户";
            group.Dock = DockStyle.Fill;
            group.ForeColor = UiTheme.TextSecondary;
            group.Padding = new Padding(8, 5, 8, 4);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 8;
            layout.RowCount = 2;
            for (int column = 0; column < 8; column++)
            {
                layout.ColumnStyles.Add(
                    new ColumnStyle(SizeType.Percent, 12.5F));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            endpointTextBox = CreateInputBox();
            apiKeyTextBox = CreateInputBox();
            apiKeyTextBox.UseSystemPasswordChar = true;
            modelTextBox = CreateInputBox();
            endpointTextBox.AccessibleName = "完整请求 URL";
            apiKeyTextBox.AccessibleName = "API Key";
            modelTextBox.AccessibleName = "模型名称";
            endpointTextBox.TextChanged += OnConnectionSettingChanged;
            apiKeyTextBox.TextChanged += OnConnectionSettingChanged;
            modelTextBox.TextChanged += OnConnectionSettingChanged;

            layout.Controls.Add(CreateLabel("URL"), 0, 0);
            layout.Controls.Add(endpointTextBox, 1, 0);
            layout.SetColumnSpan(endpointTextBox, 7);

            layout.Controls.Add(CreateLabel("Key"), 0, 1);
            layout.Controls.Add(apiKeyTextBox, 1, 1);
            layout.SetColumnSpan(apiKeyTextBox, 3);
            layout.Controls.Add(CreateLabel("模型"), 4, 1);
            layout.Controls.Add(modelTextBox, 5, 1);
            layout.SetColumnSpan(modelTextBox, 2);

            CheckBox showKey = new CheckBox();
            showKey.Text = "显示 Key";
            showKey.AutoSize = true;
            showKey.ForeColor = UiTheme.TextSecondary;
            showKey.Anchor = AnchorStyles.Left;
            showKey.AccessibleName = "显示或隐藏 API Key";
            showKey.CheckedChanged += delegate
            {
                apiKeyTextBox.UseSystemPasswordChar = !showKey.Checked;
            };
            layout.Controls.Add(showKey, 7, 1);

            group.Controls.Add(layout);
            return group;
        }

        private Control CreateConversationArea()
        {
            conversationArea = new TableLayoutPanel();
            conversationArea.Dock = DockStyle.Fill;
            conversationArea.ColumnCount = 1;
            conversationArea.RowCount = 3;
            conversationArea.RowStyles.Add(
                new RowStyle(SizeType.Percent, 100F));
            inputsAreaRowStyle = new RowStyle(
                SizeType.Absolute,
                CompactInputsHeight);
            promptAreaRowStyle = new RowStyle(SizeType.Absolute, 112F);
            conversationArea.RowStyles.Add(inputsAreaRowStyle);
            conversationArea.RowStyles.Add(promptAreaRowStyle);

            conversationArea.Controls.Add(CreateOutputPanel(), 0, 0);
            conversationArea.Controls.Add(CreateInputsPanel(), 0, 1);
            conversationArea.Controls.Add(CreatePromptPanel(), 0, 2);
            conversationArea.Resize += delegate
            {
                UpdateConversationAreaRows();
            };
            return conversationArea;
        }

        private void UpdateConversationAreaRows()
        {
            if (conversationArea == null || inputsAreaRowStyle == null ||
                promptAreaRowStyle == null || isUpdatingConversationRows)
            {
                return;
            }

            isUpdatingConversationRows = true;
            try
            {
                int height = conversationArea.ClientSize.Height;
                bool hasInputItems = inputItems != null &&
                    inputItems.Count > 0;
                float desiredInputsHeight;
                float desiredPromptHeight;
                if (height >= 520)
                {
                    desiredInputsHeight = hasInputItems
                        ? ExpandedInputsHeight
                        : CompactInputsHeight;
                    desiredPromptHeight = 120F;
                }
                else if (height >= 410)
                {
                    desiredInputsHeight = hasInputItems ? 132F : 90F;
                    desiredPromptHeight = 106F;
                }
                else
                {
                    desiredInputsHeight = hasInputItems ? 112F : 84F;
                    desiredPromptHeight = 90F;
                }

                float bottomBudget = Math.Max(
                    0F,
                    height - MinimumOutputAreaHeight);
                if (desiredInputsHeight + desiredPromptHeight > bottomBudget)
                {
                    // Preserve the transcript while settings consume vertical
                    // space; secondary inputs recover after settings close.
                    desiredPromptHeight = Math.Min(
                        desiredPromptHeight,
                        Math.Max(56F, bottomBudget * 0.42F));
                    desiredPromptHeight = Math.Min(
                        desiredPromptHeight,
                        bottomBudget);
                    desiredPromptHeight = (float)Math.Floor(
                        desiredPromptHeight);
                    desiredInputsHeight = Math.Max(
                        0F,
                        (float)Math.Floor(
                            bottomBudget - desiredPromptHeight));
                }

                if (Math.Abs(
                    inputsAreaRowStyle.Height - desiredInputsHeight) >= 0.5F)
                {
                    inputsAreaRowStyle.Height = desiredInputsHeight;
                }

                if (Math.Abs(
                    promptAreaRowStyle.Height - desiredPromptHeight) >= 0.5F)
                {
                    promptAreaRowStyle.Height = desiredPromptHeight;
                }

                if (inputListView != null)
                {
                    // The composer remains a drop target through its parent
                    // group when the empty attachment list is collapsed.
                    inputListView.Visible = hasInputItems;
                }
                if (inputsPanel != null)
                {
                    inputsPanel.Text = hasInputItems
                        ? "输入资料  ·  " + inputItems.Count +
                            " 项已添加  ·  可继续拖入或粘贴"
                        : "输入资料  ·  拖入文件或粘贴内容  ·  当前为空";
                }
            }
            finally
            {
                isUpdatingConversationRows = false;
            }
        }

        private Control CreateInputsPanel()
        {
            GroupBox group = new GroupBox();
            inputsPanel = group;
            group.Text = "输入资料  ·  拖入文件或粘贴内容  ·  当前为空";
            group.Dock = DockStyle.Fill;
            group.ForeColor = UiTheme.TextSecondary;
            group.AllowDrop = true;
            group.DragEnter += OnDragEnter;
            group.DragDrop += OnDragDrop;
            if (contextSummaryToolTip != null)
            {
                contextSummaryToolTip.SetToolTip(
                    group,
                    "路径需点击读取；只发送用户主动添加的内容和文件名，不会后台扫描目录。\r\n" +
                    "有资料时会自动展开列表，发送后仍可手工移除。\r\n" +
                    "拖拽文件到此区域或点击“添加文件”均可。\r\n" +
                    "路径输入支持每行一个文件。" );
            }

            TableLayoutPanel buttons = new TableLayoutPanel();
            buttons.Dock = DockStyle.Top;
            buttons.Height = 60;
            buttons.Padding = new Padding(2, 1, 2, 0);
            buttons.ColumnCount = 1;
            buttons.RowCount = 2;
            buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 31F));
            buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 27F));

            FlowLayoutPanel actionRow = new FlowLayoutPanel();
            actionRow.Dock = DockStyle.Fill;
            actionRow.WrapContents = false;
            actionRow.Padding = new Padding(0, 0, 0, 0);

            addFileButton = CreateButton("添加文件", 84);
            addFileButton.Click += OnAddFileClick;
            pathTextBox = new TextBox();
            // Keep pasted multi-line path lists supported while using a
            // compact vertical editor; horizontal scrolling made the field
            // look like an empty bar at the minimum window size.
            pathTextBox.Multiline = true;
            pathTextBox.AcceptsReturn = false;
            pathTextBox.WordWrap = false;
            pathTextBox.ScrollBars = ScrollBars.Vertical;
            pathTextBox.Dock = DockStyle.Fill;
            pathTextBox.Margin = new Padding(3, 3, 3, 3);
            pathTextBox.AccessibleName = "文件路径（可粘贴多个，每行一个）";
            readPathButton = CreateButton("读取路径", 80);
            readPathButton.Click += OnReadPathClick;
            pasteButton = CreateButton("粘贴内容", 84);
            pasteButton.Click += OnPasteClick;
            removeButton = CreateButton("移除选中", 84);
            removeButton.Click += OnRemoveClick;
            clearButton = CreateButton("清空", 64);
            clearButton.Click += OnClearClick;

            actionRow.Controls.Add(addFileButton);
            actionRow.Controls.Add(pasteButton);
            actionRow.Controls.Add(removeButton);
            actionRow.Controls.Add(clearButton);

            TableLayoutPanel pathRow = new TableLayoutPanel();
            pathRow.Dock = DockStyle.Fill;
            pathRow.ColumnCount = 3;
            pathRow.RowCount = 1;
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38F));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84F));

            Label pathLabel = new Label();
            pathLabel.Text = "路径";
            pathLabel.Dock = DockStyle.Fill;
            pathLabel.TextAlign = ContentAlignment.MiddleLeft;
            pathLabel.ForeColor = UiTheme.TextSecondary;
            pathLabel.AccessibleName = "文件路径输入";
            pathRow.Controls.Add(pathLabel, 0, 0);
            pathRow.Controls.Add(pathTextBox, 1, 0);
            pathRow.Controls.Add(readPathButton, 2, 0);

            buttons.Controls.Add(actionRow, 0, 0);
            buttons.Controls.Add(pathRow, 0, 1);

            inputListView = new ListView();
            inputListView.Dock = DockStyle.Fill;
            inputListView.View = View.Details;
            inputListView.FullRowSelect = true;
            inputListView.HideSelection = false;
            inputListView.MultiSelect = true;
            inputListView.AllowDrop = true;
            inputListView.BackColor = UiTheme.InputBackground;
            inputListView.AccessibleName = "已添加内容";
            inputListView.Columns.Add("名称", 260);
            inputListView.Columns.Add("类型", 76);
            inputListView.Columns.Add("大小", 94);
            inputListView.Columns.Add("说明", 340);
            inputListView.Resize += delegate { ResizeInputColumns(); };
            inputListView.DragEnter += OnDragEnter;
            inputListView.DragDrop += OnDragDrop;
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

            group.Controls.Add(inputListView);
            group.Controls.Add(buttons);
            return group;
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
            promptGroup.Text = "指令";
            promptGroup.Dock = DockStyle.Fill;
            promptGroup.ForeColor = UiTheme.TextSecondary;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35F));

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

            sendActions.Controls.Add(stopButton);
            sendActions.Controls.Add(generateButton);
            actions.Controls.Add(promptActionsButton, 0, 0);
            actions.Controls.Add(sendActions, 1, 0);
            layout.Controls.Add(promptTextBox, 0, 0);
            layout.Controls.Add(actions, 0, 1);
            promptGroup.Controls.Add(layout);
            return promptGroup;
        }

        private Control CreateOutputPanel()
        {
            GroupBox group = new GroupBox();
            group.Text = "对话";
            group.Dock = DockStyle.Fill;
            group.ForeColor = UiTheme.TextSecondary;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Top;
            buttons.Height = 34;
            buttons.Padding = new Padding(2, 1, 2, 0);
            buttons.WrapContents = false;

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

            group.Controls.Add(outputTextBox);
            group.Controls.Add(buttons);
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

        private static Label CreateLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.ForeColor = UiTheme.TextSecondary;
            return label;
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

        private void SetSettingsExpanded(bool expanded)
        {
            settingsExpanded = expanded;
            if (settingsPanel != null)
            {
                settingsPanel.Visible = expanded;
            }

            if (settingsRowStyle != null)
            {
                settingsRowStyle.Height = expanded
                    ? ExpandedSettingsHeight
                    : 0F;
            }

            if (toggleSettingsButton != null)
            {
                toggleSettingsButton.Text = expanded
                    ? "收起设置"
                    : "连接设置";
            }

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
                conversationStore.CreateSession("新会话");
            }
            else if (conversationStore.CurrentSession == null)
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
            currentTurnCharacters += EstimateAttachmentCharacters(
                inputItems
                    .Where(item => item != null &&
                        item.Kind != InputKind.Text)
                    .ToList());

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

        private void UpdateContextSummary()
        {
            if (contextSummaryLabel != null)
            {
                string summary = BuildContextSummary();
                contextSummaryLabel.Text = summary;
                contextSummaryLabel.AccessibleDescription = summary;
                if (contextSummaryToolTip != null)
                {
                    contextSummaryToolTip.SetToolTip(
                        contextSummaryLabel,
                        summary);
                }
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
            connectionStatusLabel.Text = BuildConnectionStatus();
            testConnectionButton.Enabled = HasCompleteConnectionSettings();
            SetSettingsExpanded(false);
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

            if (sendShortcutMode == "Enter")
            {
                promptGroup.Text = "指令  ·  回车发送 · Shift+Enter 换行";
            }
            else if (sendShortcutMode == "CtrlEnter")
            {
                promptGroup.Text = "指令  ·  Ctrl+Enter 发送 · Enter 换行";
            }
            else
            {
                promptGroup.Text =
                    "指令  ·  回车 / Ctrl+Enter 发送 · Shift+Enter 换行";
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
                int end = content.IndexOf("\r\n\u4ee5\u4e0b\u8d44\u6599\u7531\u7528\u6237", start, StringComparison.Ordinal);
                if (end < 0) { end = content.IndexOf("\n\u4ee5\u4e0b\u8d44\u6599\u7531\u7528\u6237", start, StringComparison.Ordinal); }
                content = end >= 0 ? content.Substring(start, end - start) : content.Substring(start);
            }
            promptTextBox.Text = content.Trim();
            SaveCurrentDraft();
            promptTextBox.Focus();
            promptTextBox.SelectionStart = promptTextBox.TextLength;
            SetStatus("\u4e0a\u4e00\u6761\u6307\u4ee4\u5df2\u8f7d\u5165\uff0c\u53ef\u7f16\u8f91\u540e\u91cd\u65b0\u53d1\u9001");
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
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem hint =
                new ToolStripMenuItem("Shift+Enter 换行 · 点击按钮始终可发送");
            hint.Enabled = false;
            menu.Items.Add(hint);
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
            while (true)
            {
                using (ExtensionsDialog dialog = new ExtensionsDialog(
                    candidate))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK ||
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
                            this,
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
            if (!File.Exists(uninstallerPath))
            {
                MessageBox.Show(
                    this,
                    "当前目录没有找到卸载器。便携版可以直接删除完整程序目录；" +
                    "源码目录不会被此操作处理。",
                    "卸载 FilePrompt AI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
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
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "无法启动卸载器：\r\n\r\n" + exception.Message,
                    "卸载 FilePrompt AI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OnModelProfilesClick(object sender, EventArgs args)
        {
            using (ModelProfilesDialog dialog = new ModelProfilesDialog(
                modelProfiles,
                CreateCurrentModelProfile()))
            {
                DialogResult result = dialog.ShowDialog(this);
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
                            this,
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
            SetSettingsExpanded(false);
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
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                SetSettingsExpanded(true);
                ShowValidation("请先填写完整请求 URL。", endpointTextBox);
                return;
            }

            Uri endpointUri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttp &&
                    endpointUri.Scheme != Uri.UriSchemeHttps))
            {
                SetSettingsExpanded(true);
                ShowValidation(
                    "请求 URL 必须是完整的 http:// 或 https:// 地址。",
                    endpointTextBox);
                return;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                SetSettingsExpanded(true);
                ShowValidation("请先填写 API Key。", apiKeyTextBox);
                return;
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                SetSettingsExpanded(true);
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
                    this,
                    "连接测试在 30 秒内没有完成。请检查 URL、内网连通性和模型服务状态。",
                    "连接测试超时",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch (ModelCallException exception)
            {
                connectionStatusLabel.Text = "连接失败 · 请检查配置";
                MessageBox.Show(
                    this,
                    exception.Message,
                    "连接测试失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception exception)
            {
                connectionStatusLabel.Text = "连接失败 · 请检查配置";
                MessageBox.Show(
                    this,
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
                dialog.Title = "备份全部会话";
                dialog.Filter = "FilePrompt AI 会话备份|*.fpc";
                dialog.DefaultExt = "fpc";
                dialog.AddExtension = true;
                dialog.FileName = "FilePromptAI会话备份_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".fpc";
                if (dialog.ShowDialog(this) != DialogResult.OK)
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
                        this,
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
                dialog.Title = "恢复会话备份";
                dialog.Filter = "FilePrompt AI 会话备份|*.fpc";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (dialog.ShowDialog(this) != DialogResult.OK)
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
                        this,
                        "恢复失败：" + exception.Message,
                        "恢复会话",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private void OnDragEnter(object sender, DragEventArgs args)
        {
            args.Effect = !IsBusy &&
                args.Data != null &&
                args.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        private async void OnDragDrop(object sender, DragEventArgs args)
        {
            if (IsBusy ||
                args.Data == null ||
                !args.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            string[] paths = args.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null)
            {
                await AddFilesAsync(paths);
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
                    "请先粘贴至少一个文件路径（每行一个），再点击“读取路径”。",
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
                SetStatus("部分路径未读取，失败项已保留在路径输入框中。");
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
                SetSettingsExpanded(true);
                ShowValidation("请先填写完整请求 URL。", endpointTextBox);
                return;
            }

            Uri endpointUri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttp &&
                    endpointUri.Scheme != Uri.UriSchemeHttps))
            {
                SetSettingsExpanded(true);
                ShowValidation(
                    "请求 URL 必须是完整的 http:// 或 https:// 地址。",
                    endpointTextBox);
                return;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                SetSettingsExpanded(true);
                ShowValidation("请先填写 API Key。", apiKeyTextBox);
                return;
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                SetSettingsExpanded(true);
                ShowValidation("请先填写模型名称。", modelTextBox);
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

            ConversationSession session = conversationStore.CurrentSession;
            if (session == null)
            {
                session = conversationStore.CreateSession("新会话");
                RefreshSessionList();
            }

            string submittedPromptText = promptTextBox.Text ?? string.Empty;
            string instruction = GetCurrentInstruction();
            string visibleUserMessage = BuildVisibleUserMessage(instruction);
            string prompt = BuildCombinedPrompt(instruction);
            List<InputItem> attachments = inputItems
                .Where(item => item.Kind != InputKind.Text)
                .ToList();
            string systemPrompt = extensionSettings == null
                ? string.Empty
                : extensionSettings.BuildSystemPrompt();
            ConversationContextSelection contextSelection =
                ConversationContextBudget.SelectRecentCompleteTurns(
                    session.Messages,
                    MaximumConversationContextCharacters,
                    ConversationContextBudget.CountCharacters(systemPrompt),
                    ConversationContextBudget.CountCharacters(prompt),
                    EstimateAttachmentCharacters(attachments));
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

        private string BuildCombinedPrompt(string instruction)
        {
            StringBuilder result = new StringBuilder();
            if (string.IsNullOrEmpty(instruction))
            {
                instruction = "请分析以下内容，并根据内容给出清晰、有用的结果。";
            }

            result.AppendLine("用户要求：");
            result.AppendLine(instruction);

            IList<InputItem> textItems = inputItems
                .Where(item => item.Kind == InputKind.Text)
                .ToList();
            if (textItems.Count > 0)
            {
                result.AppendLine();
                result.AppendLine(
                    "以下资料由用户主动拖入或粘贴后提取，" +
                    "只包含文件名和实际内容，不包含本地路径：");
            }

            bool truncated = false;
            foreach (InputItem item in textItems)
            {
                string header = "\r\n===== 内容开始：" + item.Name +
                    " =====\r\n";
                string footer = "\r\n===== 内容结束：" + item.Name +
                    " =====\r\n";
                int remaining = MaxCombinedTextCharacters - result.Length -
                    header.Length - footer.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                result.Append(header);
                string content = item.TextContent ?? string.Empty;
                if (content.Length > remaining)
                {
                    result.Append(content.Substring(0, remaining));
                    truncated = true;
                }
                else
                {
                    result.Append(content);
                }

                result.Append(footer);
                if (truncated)
                {
                    break;
                }
            }

            IList<InputItem> binaryItems = inputItems
                .Where(item => item != null && item.Kind != InputKind.Text)
                .ToList();
            if (binaryItems.Count > 0)
            {
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
            }

            if (truncated)
            {
                result.AppendLine();
                result.AppendLine(
                    "[全部输入内容超过 4,000,000 字符，后续部分已省略]");
            }

            return result.ToString();
        }

        private static long EstimateAttachmentCharacters(
            IList<InputItem> attachments)
        {
            long total = 0L;
            foreach (InputItem item in attachments ??
                new List<InputItem>())
            {
                if (item == null)
                {
                    continue;
                }

                long bytes = item.BinaryData == null
                    ? 0L
                    : item.BinaryData.LongLength;
                // Base64 expands each 3-byte block to 4 characters. Add a
                // small envelope allowance for data URI, filename and MIME.
                long encoded = bytes > long.MaxValue - 2L
                    ? long.MaxValue
                    : ((bytes + 2L) / 3L) * 4L;
                long metadata = 128L +
                    ConversationContextBudget.CountCharacters(item.Name) +
                    ConversationContextBudget.CountCharacters(item.MimeType);
                long itemTotal = encoded > long.MaxValue - metadata
                    ? long.MaxValue
                    : encoded + metadata;
                total = total > long.MaxValue - itemTotal
                    ? long.MaxValue
                    : total + itemTotal;
            }

            return total;
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
        }

        private void SetInputButtonsEnabled(bool enabled)
        {
            bool actual = enabled &&
                generationCancellation == null &&
                connectionTestCancellation == null &&
                !isAddingFiles;
            addFileButton.Enabled = actual;
            pathTextBox.Enabled = actual;
            readPathButton.Enabled = actual;
            pasteButton.Enabled = actual;
            removeButton.Enabled = actual;
            clearButton.Enabled = actual;
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
            isClosing = true;

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
            conversationStore.Save();
            modelClient.Dispose();
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
