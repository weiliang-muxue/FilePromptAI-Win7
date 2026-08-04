using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FilePromptWin7
{
    internal sealed class ExtensionsDialog : Form
    {
        private readonly ExtensionSettings working;
        private readonly CheckedListBox skillList;
        private readonly TextBox skillNameBox;
        private readonly TextBox skillDescriptionBox;
        private readonly TextBox skillInstructionsBox;
        private readonly CheckedListBox serverList;
        private readonly TextBox serverNameBox;
        private readonly ComboBox transportBox;
        private readonly TextBox commandBox;
        private readonly TextBox argumentsBox;
        private readonly TextBox workingDirectoryBox;
        private readonly TextBox urlBox;
        private readonly TextBox environmentBox;
        private readonly TextBox headersBox;
        private readonly CheckBox confirmationBox;
        private readonly Button testServerButton;
        private readonly Button okButton;
        private readonly Label dialogStatus;
        private CancellationTokenSource testCancellation;
        private int selectedSkillIndex;
        private int selectedServerIndex;
        private bool refreshing;

        public ExtensionSettings Settings { get; private set; }

        public ExtensionsDialog(ExtensionSettings settings)
        {
            working = (settings ?? new ExtensionSettings()).Clone();
            Settings = null;
            selectedSkillIndex = -1;
            selectedServerIndex = -1;

            Text = "技能 / MCP";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 500);
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(
                Math.Max(
                    MinimumSize.Width,
                    Math.Min(1020, workingArea.Width - 32)),
                Math.Max(
                    MinimumSize.Height,
                    Math.Min(680, workingArea.Height - 32)));
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular);
            BackColor = UiTheme.WindowBackground;
            ForeColor = UiTheme.ButtonText;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            KeyPreview = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(12);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Appearance = TabAppearance.Normal;

            skillList = CreateCheckedList("已安装技能");
            skillNameBox = CreateTextBox(false);
            skillDescriptionBox = CreateTextBox(true);
            skillInstructionsBox = CreateTextBox(true);
            tabs.TabPages.Add(CreateSkillsPage());

            serverList = CreateCheckedList("MCP 服务");
            serverNameBox = CreateTextBox(false);
            transportBox = new ComboBox();
            transportBox.Dock = DockStyle.Fill;
            transportBox.DropDownStyle = ComboBoxStyle.DropDownList;
            transportBox.Items.Add("stdio（本地进程）");
            transportBox.Items.Add("HTTP（Streamable HTTP）");
            commandBox = CreateTextBox(false);
            argumentsBox = CreateTextBox(true);
            workingDirectoryBox = CreateTextBox(false);
            urlBox = CreateTextBox(false);
            environmentBox = CreateTextBox(true);
            headersBox = CreateTextBox(true);
            confirmationBox = new CheckBox();
            confirmationBox.Text = "每次调用前确认";
            confirmationBox.AutoSize = true;
            confirmationBox.ForeColor = ForeColor;
            confirmationBox.Checked = true;
            testServerButton = CreateButton("测试所选", 92);
            tabs.TabPages.Add(CreateMcpPage());

            dialogStatus = new Label();
            dialogStatus.Dock = DockStyle.Fill;
            dialogStatus.TextAlign = ContentAlignment.MiddleLeft;
            dialogStatus.ForeColor = UiTheme.TextMuted;
            dialogStatus.AutoEllipsis = true;

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;

            okButton = CreateButton("保存", 86);
            okButton.BackColor = UiTheme.Accent;
            okButton.FlatAppearance.BorderSize = 0;
            okButton.Click += OnSaveAndClose;
            Button cancelButton = CreateButton("取消", 86);
            cancelButton.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(okButton);
            actions.Controls.Add(cancelButton);

            root.Controls.Add(tabs, 0, 0);
            root.Controls.Add(dialogStatus, 0, 1);
            root.Controls.Add(actions, 0, 2);
            Controls.Add(root);

            AcceptButton = okButton;
            CancelButton = cancelButton;
            FormClosing += OnDialogClosing;
            KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Escape && testCancellation != null)
                {
                    testCancellation.Cancel();
                    args.Handled = true;
                }
            };

            HookSelectionEvents();
            ReloadSkills(working.Skills.Count > 0 ? 0 : -1);
            ReloadServers(working.McpServers.Count > 0 ? 0 : -1);
            UpdateSummary();
        }

        private TabPage CreateSkillsPage()
        {
            TabPage page = CreateTabPage("技能");
            SplitContainer split = CreateSplitContainer();

            TableLayoutPanel left = CreateListLayout();
            FlowLayoutPanel listActions = CreateLeftActions();
            Button add = CreateButton("新建", 72);
            Button paste = CreateButton("从剪贴板安装", 112);
            Button remove = CreateButton("删除", 72);
            add.Click += delegate
            {
                CaptureSkill(selectedSkillIndex);
                working.Skills.Add(new SkillDefinition());
                ReloadSkills(working.Skills.Count - 1);
            };
            paste.Click += OnPasteSkill;
            remove.Click += OnDeleteSkill;
            listActions.Controls.Add(add);
            listActions.Controls.Add(paste);
            listActions.Controls.Add(remove);
            left.Controls.Add(skillList, 0, 0);
            left.Controls.Add(listActions, 0, 1);

            TableLayoutPanel editor = new TableLayoutPanel();
            editor.Dock = DockStyle.Fill;
            editor.Padding = new Padding(12, 8, 8, 8);
            editor.ColumnCount = 1;
            editor.RowCount = 6;
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            editor.Controls.Add(CreateFieldLabel("名称"), 0, 0);
            editor.Controls.Add(skillNameBox, 0, 1);
            editor.Controls.Add(CreateFieldLabel("说明"), 0, 2);
            editor.Controls.Add(skillDescriptionBox, 0, 3);
            editor.Controls.Add(CreateFieldLabel("指令内容"), 0, 4);
            editor.Controls.Add(skillInstructionsBox, 0, 5);

            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(editor);
            page.Controls.Add(split);
            return page;
        }

        private TabPage CreateMcpPage()
        {
            TabPage page = CreateTabPage("MCP");
            SplitContainer split = CreateSplitContainer();

            TableLayoutPanel left = CreateListLayout();
            FlowLayoutPanel listActions = CreateLeftActions();
            Button add = CreateButton("新建", 72);
            Button import = CreateButton("粘贴 JSON", 92);
            Button remove = CreateButton("删除", 72);
            add.Click += delegate
            {
                CaptureServer(selectedServerIndex);
                working.McpServers.Add(new McpServerDefinition());
                ReloadServers(working.McpServers.Count - 1);
            };
            import.Click += OnImportMcp;
            remove.Click += OnDeleteServer;
            listActions.Controls.Add(add);
            listActions.Controls.Add(import);
            listActions.Controls.Add(remove);
            left.Controls.Add(serverList, 0, 0);
            left.Controls.Add(listActions, 0, 1);

            Panel scrollHost = new Panel();
            scrollHost.Dock = DockStyle.Fill;
            scrollHost.AutoScroll = true;
            scrollHost.BackColor = UiTheme.PanelBackground;

            TableLayoutPanel editor = new TableLayoutPanel();
            editor.Dock = DockStyle.Top;
            editor.AutoSize = true;
            editor.Padding = new Padding(12, 8, 12, 12);
            editor.ColumnCount = 2;
            editor.RowCount = 10;
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116F));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

            AddEditorRow(editor, 0, "名称", serverNameBox);
            AddEditorRow(editor, 1, "传输方式", transportBox);
            AddEditorRow(editor, 2, "启动命令", commandBox);
            AddEditorRow(editor, 3, "参数（每行一项）", argumentsBox);
            AddEditorRow(editor, 4, "工作目录", workingDirectoryBox);
            AddEditorRow(editor, 5, "请求 URL", urlBox);
            AddEditorRow(editor, 6, "环境变量", environmentBox);
            AddEditorRow(editor, 7, "请求头", headersBox);
            editor.Controls.Add(CreateFieldLabel("授权"), 0, 8);
            editor.Controls.Add(confirmationBox, 1, 8);

            FlowLayoutPanel testActions = new FlowLayoutPanel();
            testActions.Dock = DockStyle.Fill;
            testActions.FlowDirection = FlowDirection.LeftToRight;
            testActions.WrapContents = false;
            testServerButton.Click += OnTestServer;
            testActions.Controls.Add(testServerButton);
            editor.Controls.Add(testActions, 1, 9);

            scrollHost.Controls.Add(editor);
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(scrollHost);
            page.Controls.Add(split);
            return page;
        }

        private void HookSelectionEvents()
        {
            skillList.SelectedIndexChanged += delegate
            {
                if (refreshing)
                {
                    return;
                }

                CaptureSkill(selectedSkillIndex);
                selectedSkillIndex = skillList.SelectedIndex;
                LoadSkillEditor(selectedSkillIndex);
            };
            skillList.ItemCheck += delegate(object sender, ItemCheckEventArgs args)
            {
                if (!refreshing && args.Index >= 0 &&
                    args.Index < working.Skills.Count)
                {
                    working.Skills[args.Index].Enabled =
                        args.NewValue == CheckState.Checked;
                    BeginInvoke((MethodInvoker)UpdateSummary);
                }
            };

            serverList.SelectedIndexChanged += delegate
            {
                if (refreshing)
                {
                    return;
                }

                int requestedIndex = serverList.SelectedIndex;
                try
                {
                    CaptureServer(selectedServerIndex);
                }
                catch (Exception exception)
                {
                    refreshing = true;
                    serverList.SelectedIndex = selectedServerIndex;
                    refreshing = false;
                    SetDialogStatus(exception.Message, true);
                    ShowError(exception.Message);
                    return;
                }

                selectedServerIndex = requestedIndex;
                LoadServerEditor(selectedServerIndex);
            };
            serverList.ItemCheck += delegate(object sender, ItemCheckEventArgs args)
            {
                if (!refreshing && args.Index >= 0 &&
                    args.Index < working.McpServers.Count)
                {
                    working.McpServers[args.Index].Enabled =
                        args.NewValue == CheckState.Checked;
                    BeginInvoke((MethodInvoker)UpdateSummary);
                }
            };
            transportBox.SelectedIndexChanged += delegate
            {
                UpdateTransportFields();
            };
        }

        private void ReloadSkills(int selectedIndex)
        {
            refreshing = true;
            skillList.Items.Clear();
            for (int index = 0; index < working.Skills.Count; index++)
            {
                SkillDefinition skill = working.Skills[index];
                skillList.Items.Add(skill.Name, skill.Enabled);
            }

            selectedSkillIndex = NormalizeSelection(
                selectedIndex,
                working.Skills.Count);
            skillList.SelectedIndex = selectedSkillIndex;
            refreshing = false;
            LoadSkillEditor(selectedSkillIndex);
            UpdateSummary();
        }

        private void ReloadServers(int selectedIndex)
        {
            refreshing = true;
            serverList.Items.Clear();
            for (int index = 0; index < working.McpServers.Count; index++)
            {
                McpServerDefinition server = working.McpServers[index];
                serverList.Items.Add(server.Name, server.Enabled);
            }

            selectedServerIndex = NormalizeSelection(
                selectedIndex,
                working.McpServers.Count);
            serverList.SelectedIndex = selectedServerIndex;
            refreshing = false;
            LoadServerEditor(selectedServerIndex);
            UpdateSummary();
        }

        private static int NormalizeSelection(int index, int count)
        {
            if (count == 0)
            {
                return -1;
            }

            return Math.Max(0, Math.Min(index, count - 1));
        }

        private void LoadSkillEditor(int index)
        {
            bool available = index >= 0 && index < working.Skills.Count;
            SetEnabled(
                new Control[]
                {
                    skillNameBox,
                    skillDescriptionBox,
                    skillInstructionsBox
                },
                available);
            SkillDefinition skill = available
                ? working.Skills[index]
                : null;
            skillNameBox.Text = skill == null ? string.Empty : skill.Name;
            skillDescriptionBox.Text = skill == null
                ? string.Empty
                : skill.Description;
            skillInstructionsBox.Text = skill == null
                ? string.Empty
                : skill.Instructions;
        }

        private void CaptureSkill(int index)
        {
            if (index < 0 || index >= working.Skills.Count)
            {
                return;
            }

            SkillDefinition skill = working.Skills[index];
            skill.Name = skillNameBox.Text;
            skill.Description = skillDescriptionBox.Text;
            skill.Instructions = skillInstructionsBox.Text;
            if (index < skillList.Items.Count)
            {
                skillList.Items[index] = string.IsNullOrWhiteSpace(skill.Name)
                    ? "未命名技能"
                    : skill.Name.Trim();
            }
        }

        private void LoadServerEditor(int index)
        {
            bool available = index >= 0 && index < working.McpServers.Count;
            SetEnabled(
                new Control[]
                {
                    serverNameBox,
                    transportBox,
                    commandBox,
                    argumentsBox,
                    workingDirectoryBox,
                    urlBox,
                    environmentBox,
                    headersBox,
                    confirmationBox,
                    testServerButton
                },
                available);
            McpServerDefinition server = available
                ? working.McpServers[index]
                : null;
            serverNameBox.Text = server == null ? string.Empty : server.Name;
            transportBox.SelectedIndex = server != null && string.Equals(
                server.Transport,
                "http",
                StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
            commandBox.Text = server == null ? string.Empty : server.Command;
            argumentsBox.Text = server == null
                ? string.Empty
                : string.Join("\r\n", server.Arguments.ToArray());
            workingDirectoryBox.Text = server == null
                ? string.Empty
                : server.WorkingDirectory;
            urlBox.Text = server == null ? string.Empty : server.Url;
            environmentBox.Text = server == null
                ? string.Empty
                : FormatDictionary(server.Environment);
            headersBox.Text = server == null
                ? string.Empty
                : FormatDictionary(server.Headers);
            confirmationBox.Checked = server == null ||
                server.RequireConfirmation;
            UpdateTransportFields();
        }

        private void CaptureServer(int index)
        {
            if (index < 0 || index >= working.McpServers.Count)
            {
                return;
            }

            McpServerDefinition server = working.McpServers[index];
            server.Name = serverNameBox.Text;
            server.Transport = transportBox.SelectedIndex == 1
                ? "http"
                : "stdio";
            server.Command = commandBox.Text;
            server.Arguments = SplitLines(argumentsBox.Text);
            server.WorkingDirectory = workingDirectoryBox.Text;
            server.Url = urlBox.Text;
            server.Environment = ParseDictionary(
                environmentBox.Text,
                "环境变量");
            server.Headers = ParseDictionary(headersBox.Text, "请求头");
            server.RequireConfirmation = confirmationBox.Checked;
            if (index < serverList.Items.Count)
            {
                serverList.Items[index] = string.IsNullOrWhiteSpace(server.Name)
                    ? "未命名 MCP"
                    : server.Name.Trim();
            }
        }

        private void UpdateTransportFields()
        {
            bool hasServer = selectedServerIndex >= 0 &&
                selectedServerIndex < working.McpServers.Count;
            bool http = transportBox.SelectedIndex == 1;
            SetFieldEnabled(commandBox, hasServer && !http);
            SetFieldEnabled(argumentsBox, hasServer && !http);
            SetFieldEnabled(workingDirectoryBox, hasServer && !http);
            SetFieldEnabled(environmentBox, hasServer && !http);
            SetFieldEnabled(urlBox, hasServer && http);
            SetFieldEnabled(headersBox, hasServer && http);
        }

        private void OnPasteSkill(object sender, EventArgs args)
        {
            try
            {
                string content = Clipboard.GetText(TextDataFormat.UnicodeText);
                SkillDefinition skill = ExtensionImport.ParseSkill(content);
                CaptureSkill(selectedSkillIndex);
                working.Skills.Add(skill);
                ReloadSkills(working.Skills.Count - 1);
                SetDialogStatus("技能已从剪贴板添加，保存后生效。", false);
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
        }

        private void OnImportMcp(object sender, EventArgs args)
        {
            try
            {
                string content = Clipboard.GetText(TextDataFormat.UnicodeText);
                IList<McpServerDefinition> imported =
                    ExtensionImport.ParseMcpServers(content);
                CaptureServer(selectedServerIndex);
                foreach (McpServerDefinition server in imported)
                {
                    working.McpServers.Add(server);
                }

                ReloadServers(working.McpServers.Count - imported.Count);
                SetDialogStatus(
                    "已导入 " + imported.Count + " 个 MCP 服务，保存后生效。",
                    false);
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
        }

        private void OnDeleteSkill(object sender, EventArgs args)
        {
            int index = skillList.SelectedIndex;
            if (index < 0 || index >= working.Skills.Count)
            {
                return;
            }

            working.Skills.RemoveAt(index);
            ReloadSkills(index);
        }

        private void OnDeleteServer(object sender, EventArgs args)
        {
            int index = serverList.SelectedIndex;
            if (index < 0 || index >= working.McpServers.Count)
            {
                return;
            }

            working.McpServers.RemoveAt(index);
            ReloadServers(index);
        }

        private async void OnTestServer(object sender, EventArgs args)
        {
            if (testCancellation != null)
            {
                return;
            }

            try
            {
                CaptureServer(selectedServerIndex);
                McpServerDefinition selected = working.McpServers[
                    selectedServerIndex].Clone();
                selected.Enabled = true;
                ExtensionSettings validation = new ExtensionSettings();
                validation.McpServers.Add(selected);
                ExtensionStore.Validate(validation);

                testCancellation = new CancellationTokenSource();
                testCancellation.CancelAfter(TimeSpan.FromSeconds(30));
                SetTesting(true);
                using (McpRuntime runtime = await McpRuntime.ConnectAsync(
                    new[] { selected },
                    delegate(string value)
                    {
                        if (!IsDisposed)
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                SetDialogStatus(value, false);
                            });
                        }
                    },
                    testCancellation.Token))
                {
                    SetDialogStatus(
                        "连接成功，共发现 " + runtime.Tools.Count + " 个工具。",
                        false);
                }
            }
            catch (OperationCanceledException)
            {
                SetDialogStatus("MCP 测试已取消。", false);
            }
            catch (Exception exception)
            {
                SetDialogStatus("测试失败：" + exception.Message, true);
                ShowError(exception.Message);
            }
            finally
            {
                if (testCancellation != null)
                {
                    testCancellation.Dispose();
                    testCancellation = null;
                }

                SetTesting(false);
            }
        }

        private void OnSaveAndClose(object sender, EventArgs args)
        {
            try
            {
                CaptureSkill(selectedSkillIndex);
                CaptureServer(selectedServerIndex);
                ExtensionStore.Validate(working);
                Settings = working.Clone();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                SetDialogStatus(exception.Message, true);
                ShowError(exception.Message);
            }
        }

        private void OnDialogClosing(object sender, FormClosingEventArgs args)
        {
            if (testCancellation != null)
            {
                testCancellation.Cancel();
                args.Cancel = true;
                SetDialogStatus("正在取消 MCP 测试…", false);
            }
        }

        private void SetTesting(bool testing)
        {
            testServerButton.Enabled = !testing && selectedServerIndex >= 0;
            okButton.Enabled = !testing;
            serverList.Enabled = !testing;
            testServerButton.Text = testing ? "测试中…" : "测试所选";
        }

        private void UpdateSummary()
        {
            int skills = working.Skills.Count(item => item.Enabled);
            int servers = working.McpServers.Count(item => item.Enabled);
            SetDialogStatus(
                "已启用 " + skills + " 个技能 · " + servers + " 个 MCP 服务",
                false);
        }

        private void SetDialogStatus(string value, bool error)
        {
            dialogStatus.Text = value ?? string.Empty;
            dialogStatus.ForeColor = error
                ? UiTheme.Danger
                : UiTheme.TextMuted;
        }

        private void ShowError(string message)
        {
            MessageBox.Show(
                this,
                message,
                "技能 / MCP",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private static IList<string> SplitLines(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }

        private static IDictionary<string, string> ParseDictionary(
            string value,
            string description)
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = (value ?? string.Empty).Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.None);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    throw new InvalidOperationException(
                        description + "第 " + (index + 1) + " 行必须是 名称=值。");
                }

                string key = line.Substring(0, separator).Trim();
                result[key] = line.Substring(separator + 1);
            }

            return result;
        }

        private static string FormatDictionary(
            IDictionary<string, string> values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            StringBuilder result = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in values)
            {
                if (result.Length > 0)
                {
                    result.AppendLine();
                }

                result.Append(pair.Key);
                result.Append('=');
                result.Append(pair.Value);
            }

            return result.ToString();
        }

        private static void AddEditorRow(
            TableLayoutPanel layout,
            int row,
            string label,
            Control control)
        {
            layout.Controls.Add(CreateFieldLabel(label), 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private static void SetEnabled(IEnumerable<Control> controls, bool value)
        {
            foreach (Control control in controls)
            {
                control.Enabled = value;
            }
        }

        private static void SetFieldEnabled(TextBox box, bool enabled)
        {
            box.Enabled = enabled;
            box.BackColor = enabled
                ? Color.White
                : UiTheme.InputDisabledBackground;
        }

        private static TabPage CreateTabPage(string text)
        {
            TabPage page = new TabPage(text);
            page.Padding = new Padding(0);
            page.BackColor = UiTheme.PanelBackground;
            page.ForeColor = UiTheme.ButtonText;
            return page;
        }

        private static SplitContainer CreateSplitContainer()
        {
            SplitContainer split = new SplitContainer();
            split.Size = new Size(900, 500);
            split.Dock = DockStyle.Fill;
            split.FixedPanel = FixedPanel.Panel1;
            split.Panel1MinSize = 250;
            split.Panel2MinSize = 450;
            split.SplitterDistance = 270;
            split.SplitterWidth = 5;
            split.Panel1.BackColor = UiTheme.PanelAltBackground;
            split.Panel2.BackColor = UiTheme.PanelBackground;
            return split;
        }

        private static TableLayoutPanel CreateListLayout()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(8);
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));
            return layout;
        }

        private static FlowLayoutPanel CreateLeftActions()
        {
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.LeftToRight;
            panel.WrapContents = true;
            panel.Padding = new Padding(0, 6, 0, 0);
            return panel;
        }

        private static CheckedListBox CreateCheckedList(string accessibleName)
        {
            CheckedListBox list = new CheckedListBox();
            list.Dock = DockStyle.Fill;
            list.CheckOnClick = true;
            list.IntegralHeight = false;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = UiTheme.InputBackground;
            list.ForeColor = UiTheme.TextPrimary;
            list.AccessibleName = accessibleName;
            return list;
        }

        private static TextBox CreateTextBox(bool multiline)
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Multiline = multiline;
            box.AcceptsReturn = multiline;
            box.ScrollBars = multiline
                ? ScrollBars.Vertical
                : ScrollBars.None;
            box.BackColor = Color.White;
            box.ForeColor = UiTheme.TextPrimary;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Margin = new Padding(3, 3, 3, 5);
            return box;
        }

        private static Label CreateFieldLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = UiTheme.TextSecondary;
            label.AutoEllipsis = true;
            return label;
        }

        private static Button CreateButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 28;
            button.Margin = new Padding(3);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = UiTheme.BorderColor;
            button.FlatAppearance.MouseOverBackColor =
                UiTheme.ButtonHover;
            button.FlatAppearance.MouseDownBackColor =
                UiTheme.ButtonPressed;
            button.BackColor = UiTheme.ButtonBackground;
            button.ForeColor = UiTheme.ButtonText;
            button.UseVisualStyleBackColor = false;
            return button;
        }
    }
}
