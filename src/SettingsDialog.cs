using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FilePromptAIWin7
{
    internal sealed class SettingsDialog : Form
    {
        private const int NavigationWidth = 172;

        private readonly Button[] navigationButtons;
        private readonly Panel[] pages;
        private readonly Label validationLabel;
        private Label extensionSummaryLabel;
        private CheckBox showApiKeyCheckBox;
        private Control pendingFocusControl;

        public TextBox EndpointTextBox { get; private set; }

        public TextBox ApiKeyTextBox { get; private set; }

        public ComboBox ModelTextBox { get; private set; }

        public Button FetchModelsButton { get; private set; }

        public Button TestConnectionButton { get; private set; }

        public Button ModelProfilesButton { get; private set; }

        public Button ExtensionsButton { get; private set; }

        public Button BackupSessionsButton { get; private set; }

        public Button RestoreSessionsButton { get; private set; }

        public Button UninstallButton { get; private set; }

        public ComboBox SendShortcutComboBox { get; private set; }

        public Label ContextSummaryLabel { get; private set; }

        public string SendShortcutMode
        {
            get { return GetSendShortcutMode(); }
            set { SetSendShortcutMode(value); }
        }

        public SettingsDialog()
        {
            navigationButtons = new Button[4];
            pages = new Panel[4];

            Text = "设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(780, 520);
            MinimumSize = new Size(780, 520);
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular);
            BackColor = UiTheme.WindowBackground;
            ForeColor = UiTheme.TextPrimary;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(12);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));

            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 2;
            body.RowCount = 1;
            body.ColumnStyles.Add(new ColumnStyle(
                SizeType.Absolute,
                NavigationWidth));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            body.Controls.Add(CreateNavigation(), 0, 0);
            body.Controls.Add(CreatePageHost(), 1, 0);

            validationLabel = new Label();
            validationLabel.Dock = DockStyle.Fill;
            validationLabel.TextAlign = ContentAlignment.MiddleLeft;
            validationLabel.ForeColor = UiTheme.Danger;
            validationLabel.AutoEllipsis = true;
            validationLabel.AccessibleName = "设置校验信息";

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Padding = new Padding(0, 6, 0, 0);

            Button saveButton = CreateButton("保存并关闭", 108);
            saveButton.DialogResult = DialogResult.OK;
            saveButton.BackColor = UiTheme.Accent;
            saveButton.ForeColor = Color.White;
            saveButton.FlatAppearance.BorderSize = 0;
            Button cancelButton = CreateButton("取消", 82);
            cancelButton.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(saveButton);
            actions.Controls.Add(cancelButton);

            root.Controls.Add(body, 0, 0);
            root.Controls.Add(validationLabel, 0, 1);
            root.Controls.Add(actions, 0, 2);
            Controls.Add(root);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
            Shown += delegate { FocusPreparedControl(); };
            SetSendShortcutMode("Both");
            SelectPage(0);
        }

        public void SetSendShortcutMode(string mode)
        {
            if (string.Equals(
                mode,
                "CtrlEnter",
                StringComparison.OrdinalIgnoreCase))
            {
                SendShortcutComboBox.SelectedIndex = 2;
            }
            else if (string.Equals(
                mode,
                "Enter",
                StringComparison.OrdinalIgnoreCase))
            {
                SendShortcutComboBox.SelectedIndex = 1;
            }
            else
            {
                SendShortcutComboBox.SelectedIndex = 0;
            }
        }

        public string GetSendShortcutMode()
        {
            if (SendShortcutComboBox.SelectedIndex == 2)
            {
                return "CtrlEnter";
            }

            return SendShortcutComboBox.SelectedIndex == 1
                ? "Enter"
                : "Both";
        }

        public void PrepareForOpen(
            string focusField,
            string validationMessage)
        {
            ResetApiKeyVisibility();
            validationLabel.Text = validationMessage ?? string.Empty;
            Control focusControl = ResolveFocusControl(focusField);
            SelectPage(PageIndexForControl(focusControl));
            pendingFocusControl = focusControl;
            FocusPreparedControlWhenVisible();
        }

        public void PrepareForOpen(
            Control focusControl,
            string validationMessage)
        {
            ResetApiKeyVisibility();
            validationLabel.Text = validationMessage ?? string.Empty;
            SelectPage(PageIndexForControl(focusControl));
            pendingFocusControl = focusControl;
            FocusPreparedControlWhenVisible();
        }

        private void ResetApiKeyVisibility()
        {
            if (showApiKeyCheckBox != null)
            {
                showApiKeyCheckBox.Checked = false;
            }

            if (ApiKeyTextBox != null)
            {
                ApiKeyTextBox.UseSystemPasswordChar = true;
            }
        }

        public void RefreshContextSummary(string value)
        {
            SetContextSummary(value);
        }

        public void SetContextSummary(string value)
        {
            ContextSummaryLabel.Text = string.IsNullOrWhiteSpace(value)
                ? "暂无上下文摘要"
                : value.Trim();
            ContextSummaryLabel.AccessibleDescription =
                ContextSummaryLabel.Text;
        }

        public void SetExtensionSummary(string value)
        {
            extensionSummaryLabel.Text = string.IsNullOrWhiteSpace(value)
                ? "技能 0 · MCP 0"
                : value.Trim();
            extensionSummaryLabel.AccessibleDescription =
                extensionSummaryLabel.Text;
        }

        public void SetAvailableModels(IList<string> models)
        {
            string current = ModelTextBox.Text ?? string.Empty;
            ModelTextBox.BeginUpdate();
            try
            {
                ModelTextBox.Items.Clear();
                foreach (string model in models ?? new List<string>())
                {
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        ModelTextBox.Items.Add(model);
                    }
                }
            }
            finally
            {
                ModelTextBox.EndUpdate();
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                ModelTextBox.Text = current;
            }
        }

        private Control CreateNavigation()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(10, 12, 10, 12);
            panel.Margin = new Padding(0, 0, 12, 0);
            panel.BackColor = UiTheme.SidebarBackground;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Top;
            layout.AutoSize = true;
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            for (int index = 1; index < 5; index++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            }

            Label heading = new Label();
            heading.Dock = DockStyle.Fill;
            heading.Text = "设置";
            heading.TextAlign = ContentAlignment.MiddleLeft;
            heading.Font = new Font(Font, FontStyle.Bold);
            heading.ForeColor = UiTheme.TextPrimary;
            layout.Controls.Add(heading, 0, 0);

            string[] titles = new string[]
            {
                "模型连接",
                "技能与 MCP",
                "会话与输入",
                "维护"
            };
            for (int index = 0; index < titles.Length; index++)
            {
                int pageIndex = index;
                Button button = CreateNavigationButton(titles[index]);
                button.Click += delegate { SelectPage(pageIndex); };
                navigationButtons[index] = button;
                layout.Controls.Add(button, 0, index + 1);
            }

            panel.Controls.Add(layout);
            return panel;
        }

        private Control CreatePageHost()
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = UiTheme.PanelBackground;
            host.Padding = new Padding(22, 18, 22, 18);
            host.BorderStyle = BorderStyle.FixedSingle;

            pages[0] = CreateModelConnectionPage();
            pages[1] = CreateExtensionsPage();
            pages[2] = CreateConversationPage();
            pages[3] = CreateMaintenancePage();
            foreach (Panel page in pages)
            {
                page.Dock = DockStyle.Fill;
                page.Visible = false;
                host.Controls.Add(page);
            }

            return host;
        }

        private Panel CreateModelConnectionPage()
        {
            Panel page = CreatePage();
            TableLayoutPanel layout = CreatePageLayout(11);
            AddPageHeader(
                layout,
                "模型连接",
                "完整请求 URL、API Key 和模型名称",
                0);

            EndpointTextBox = CreateTextBox("完整请求 URL");
            ApiKeyTextBox = CreateTextBox("API Key");
            ApiKeyTextBox.UseSystemPasswordChar = true;
            ModelTextBox = new ComboBox();
            ModelTextBox.DropDownStyle = ComboBoxStyle.DropDown;
            ModelTextBox.FlatStyle = FlatStyle.Flat;
            ModelTextBox.BackColor = UiTheme.InputBackground;
            ModelTextBox.ForeColor = UiTheme.TextPrimary;
            ModelTextBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            ModelTextBox.AutoCompleteSource = AutoCompleteSource.ListItems;
            ModelTextBox.MaxDropDownItems = 16;
            ModelTextBox.AccessibleName = "模型名称";

            FetchModelsButton = CreateButton("获取模型", 94);
            FetchModelsButton.AccessibleName = "从当前接口获取模型列表";

            TableLayoutPanel modelSelector = new TableLayoutPanel();
            modelSelector.Dock = DockStyle.Fill;
            modelSelector.Margin = new Padding(0);
            modelSelector.ColumnCount = 2;
            modelSelector.RowCount = 1;
            modelSelector.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100F));
            modelSelector.ColumnStyles.Add(
                new ColumnStyle(SizeType.Absolute, 102F));
            ModelTextBox.Dock = DockStyle.Fill;
            ModelTextBox.Margin = new Padding(0, 3, 8, 3);
            FetchModelsButton.Dock = DockStyle.Fill;
            FetchModelsButton.Margin = new Padding(0, 3, 0, 3);
            modelSelector.Controls.Add(ModelTextBox, 0, 0);
            modelSelector.Controls.Add(FetchModelsButton, 1, 0);

            AddField(layout, 2, "完整请求 URL", EndpointTextBox);
            AddField(layout, 4, "API Key", ApiKeyTextBox);
            AddField(layout, 6, "模型名称", modelSelector);

            showApiKeyCheckBox = new CheckBox();
            showApiKeyCheckBox.Text = "显示 Key";
            showApiKeyCheckBox.AutoSize = true;
            showApiKeyCheckBox.ForeColor = UiTheme.TextSecondary;
            showApiKeyCheckBox.Anchor = AnchorStyles.Left;
            showApiKeyCheckBox.CheckedChanged += delegate
            {
                ApiKeyTextBox.UseSystemPasswordChar =
                    !showApiKeyCheckBox.Checked;
            };
            layout.Controls.Add(showApiKeyCheckBox, 0, 8);

            FlowLayoutPanel actions = CreateLeftActions();
            TestConnectionButton = CreateButton("测试连接", 94);
            ModelProfilesButton = CreateButton("模型配置...", 104);
            actions.Controls.Add(TestConnectionButton);
            actions.Controls.Add(ModelProfilesButton);
            layout.Controls.Add(actions, 0, 9);
            layout.SetColumnSpan(actions, 2);
            page.Controls.Add(layout);
            return page;
        }

        private Panel CreateExtensionsPage()
        {
            Panel page = CreatePage();
            TableLayoutPanel layout = CreatePageLayout(6);
            AddPageHeader(
                layout,
                "技能与 MCP",
                "离线技能和模型上下文协议服务",
                0);

            Label summaryHeading = CreateFieldLabel("当前配置");
            layout.Controls.Add(summaryHeading, 0, 2);
            layout.SetColumnSpan(summaryHeading, 2);

            extensionSummaryLabel = CreateSummaryLabel(
                "技能 0 · MCP 0",
                "技能与 MCP 配置摘要");
            layout.Controls.Add(extensionSummaryLabel, 0, 3);
            layout.SetColumnSpan(extensionSummaryLabel, 2);

            ExtensionsButton = CreateButton("管理技能与 MCP...", 152);
            FlowLayoutPanel actions = CreateLeftActions();
            actions.Controls.Add(ExtensionsButton);
            layout.Controls.Add(actions, 0, 4);
            layout.SetColumnSpan(actions, 2);
            page.Controls.Add(layout);
            return page;
        }

        private Panel CreateConversationPage()
        {
            Panel page = CreatePage();
            TableLayoutPanel layout = CreatePageLayout(8);
            AddPageHeader(
                layout,
                "会话与输入",
                "发送方式和当前会话上下文",
                0);

            Label shortcutLabel = CreateFieldLabel("发送快捷键");
            layout.Controls.Add(shortcutLabel, 0, 2);
            layout.SetColumnSpan(shortcutLabel, 2);

            SendShortcutComboBox = new ComboBox();
            SendShortcutComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            SendShortcutComboBox.Width = 240;
            SendShortcutComboBox.Items.Add("Enter 或 Ctrl+Enter");
            SendShortcutComboBox.Items.Add("仅 Enter");
            SendShortcutComboBox.Items.Add("仅 Ctrl+Enter");
            SendShortcutComboBox.AccessibleName = "发送快捷键";
            layout.Controls.Add(SendShortcutComboBox, 0, 3);
            layout.SetColumnSpan(SendShortcutComboBox, 2);

            Label contextHeading = CreateFieldLabel("当前上下文");
            layout.Controls.Add(contextHeading, 0, 5);
            layout.SetColumnSpan(contextHeading, 2);

            ContextSummaryLabel = CreateSummaryLabel(
                "暂无上下文摘要",
                "当前会话上下文摘要");
            layout.Controls.Add(ContextSummaryLabel, 0, 6);
            layout.SetColumnSpan(ContextSummaryLabel, 2);
            page.Controls.Add(layout);
            return page;
        }

        private Panel CreateMaintenancePage()
        {
            Panel page = CreatePage();
            TableLayoutPanel layout = CreatePageLayout(7);
            AddPageHeader(
                layout,
                "维护",
                "会话备份、恢复和程序卸载",
                0);

            Label conversationHeading = CreateFieldLabel("会话数据");
            layout.Controls.Add(conversationHeading, 0, 2);
            layout.SetColumnSpan(conversationHeading, 2);
            FlowLayoutPanel conversationActions = CreateLeftActions();
            BackupSessionsButton = CreateButton("备份会话...", 104);
            RestoreSessionsButton = CreateButton("恢复会话...", 104);
            conversationActions.Controls.Add(BackupSessionsButton);
            conversationActions.Controls.Add(RestoreSessionsButton);
            layout.Controls.Add(conversationActions, 0, 3);
            layout.SetColumnSpan(conversationActions, 2);

            Label applicationHeading = CreateFieldLabel("程序");
            layout.Controls.Add(applicationHeading, 0, 5);
            layout.SetColumnSpan(applicationHeading, 2);
            FlowLayoutPanel applicationActions = CreateLeftActions();
            UninstallButton = CreateButton("卸载程序...", 104);
            UninstallButton.ForeColor = UiTheme.Danger;
            applicationActions.Controls.Add(UninstallButton);
            layout.Controls.Add(applicationActions, 0, 6);
            layout.SetColumnSpan(applicationActions, 2);
            page.Controls.Add(layout);
            return page;
        }

        private void SelectPage(int index)
        {
            index = Math.Max(0, Math.Min(index, pages.Length - 1));
            for (int current = 0; current < pages.Length; current++)
            {
                bool selected = current == index;
                pages[current].Visible = selected;
                navigationButtons[current].BackColor = selected
                    ? UiTheme.SelectionBackground
                    : UiTheme.SidebarBackground;
                navigationButtons[current].ForeColor = selected
                    ? UiTheme.SelectionText
                    : UiTheme.ButtonText;
            }

            pages[index].BringToFront();
        }

        private Control ResolveFocusControl(string focusField)
        {
            string value = (focusField ?? string.Empty).Trim();
            if (string.Equals(
                value,
                "ApiKey",
                StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    "ApiKeyTextBox",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ApiKeyTextBox;
            }

            if (string.Equals(
                value,
                "Model",
                StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    "ModelTextBox",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ModelTextBox;
            }

            if (string.Equals(
                value,
                "SendShortcut",
                StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    "SendShortcutComboBox",
                    StringComparison.OrdinalIgnoreCase))
            {
                return SendShortcutComboBox;
            }

            return EndpointTextBox;
        }

        private int PageIndexForControl(Control control)
        {
            if (control == ExtensionsButton)
            {
                return 1;
            }

            if (control == SendShortcutComboBox || control == ContextSummaryLabel)
            {
                return 2;
            }

            if (control == BackupSessionsButton ||
                control == RestoreSessionsButton ||
                control == UninstallButton)
            {
                return 3;
            }

            return 0;
        }

        private void FocusPreparedControlWhenVisible()
        {
            if (!Visible || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke(new MethodInvoker(FocusPreparedControl));
        }

        private void FocusPreparedControl()
        {
            Control focusControl = pendingFocusControl;
            if (focusControl == null || !focusControl.CanFocus)
            {
                return;
            }

            focusControl.Focus();
            TextBox textBox = focusControl as TextBox;
            if (textBox != null)
            {
                textBox.SelectAll();
                return;
            }

            ComboBox comboBox = focusControl as ComboBox;
            if (comboBox != null)
            {
                comboBox.SelectAll();
            }
        }

        private static Panel CreatePage()
        {
            Panel page = new Panel();
            page.BackColor = UiTheme.PanelBackground;
            return page;
        }

        private static TableLayoutPanel CreatePageLayout(int rowCount)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = rowCount;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            for (int index = 2; index < rowCount - 1; index++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            }

            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            return layout;
        }

        private void AddPageHeader(
            TableLayoutPanel layout,
            string title,
            string subtitle,
            int row)
        {
            Label titleLabel = new Label();
            titleLabel.Dock = DockStyle.Fill;
            titleLabel.Text = title;
            titleLabel.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
            titleLabel.ForeColor = UiTheme.TextPrimary;
            titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(titleLabel, 0, row);
            layout.SetColumnSpan(titleLabel, 2);

            Label subtitleLabel = new Label();
            subtitleLabel.Dock = DockStyle.Fill;
            subtitleLabel.Text = subtitle;
            subtitleLabel.ForeColor = UiTheme.TextSecondary;
            subtitleLabel.TextAlign = ContentAlignment.TopLeft;
            subtitleLabel.AutoEllipsis = true;
            layout.Controls.Add(subtitleLabel, 0, row + 1);
            layout.SetColumnSpan(subtitleLabel, 2);
        }

        private static void AddField(
            TableLayoutPanel layout,
            int labelRow,
            string labelText,
            Control control)
        {
            Label label = CreateFieldLabel(labelText);
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.BottomLeft;
            layout.Controls.Add(label, 0, labelRow);
            layout.SetColumnSpan(label, 2);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 3, 0, 3);
            layout.Controls.Add(control, 0, labelRow + 1);
            layout.SetColumnSpan(control, 2);
        }

        private static Label CreateFieldLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.ForeColor = UiTheme.TextSecondary;
            return label;
        }

        private static Label CreateSummaryLabel(
            string text,
            string accessibleName)
        {
            Label label = new Label();
            label.Dock = DockStyle.Fill;
            label.Text = text;
            label.TextAlign = ContentAlignment.TopLeft;
            label.ForeColor = UiTheme.TextPrimary;
            label.BackColor = UiTheme.PanelAltBackground;
            label.BorderStyle = BorderStyle.FixedSingle;
            label.Padding = new Padding(10, 8, 10, 8);
            label.AutoEllipsis = true;
            label.AccessibleName = accessibleName;
            label.AccessibleDescription = text;
            return label;
        }

        private static TextBox CreateTextBox(string accessibleName)
        {
            TextBox textBox = new TextBox();
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.BackColor = UiTheme.InputBackground;
            textBox.ForeColor = UiTheme.TextPrimary;
            textBox.AccessibleName = accessibleName;
            return textBox;
        }

        private static FlowLayoutPanel CreateLeftActions()
        {
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = false;
            actions.Padding = new Padding(0, 4, 0, 0);
            actions.Margin = new Padding(0);
            return actions;
        }

        private static Button CreateButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 30;
            button.Margin = new Padding(0, 0, 8, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = UiTheme.BorderColor;
            button.FlatAppearance.MouseOverBackColor = UiTheme.ButtonHover;
            button.FlatAppearance.MouseDownBackColor = UiTheme.ButtonPressed;
            button.BackColor = UiTheme.ButtonBackground;
            button.ForeColor = UiTheme.ButtonText;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private static Button CreateNavigationButton(string text)
        {
            Button button = new Button();
            button.Dock = DockStyle.Fill;
            button.Text = text;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(12, 0, 4, 0);
            button.Margin = new Padding(0, 2, 0, 2);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = UiTheme.ButtonHover;
            button.FlatAppearance.MouseDownBackColor = UiTheme.ButtonPressed;
            button.BackColor = UiTheme.SidebarBackground;
            button.ForeColor = UiTheme.ButtonText;
            button.UseVisualStyleBackColor = false;
            return button;
        }
    }
}
