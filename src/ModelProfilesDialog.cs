using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FilePromptAIWin7
{
    internal sealed class ModelProfilesDialog : Form
    {
        private readonly List<ModelProfile> working;
        private readonly ListBox profileList;
        private readonly TextBox nameBox;
        private readonly TextBox endpointBox;
        private readonly TextBox apiKeyBox;
        private readonly TextBox modelBox;
        private readonly TextBox systemPromptBox;
        private readonly CheckBox temperatureEnabled;
        private readonly NumericUpDown temperatureBox;
        private readonly CheckBox topPEnabled;
        private readonly NumericUpDown topPBox;
        private readonly CheckBox maxOutputTokensEnabled;
        private readonly NumericUpDown maxOutputTokensBox;
        private readonly Label statusLabel;
        private bool refreshing;
        private int selectedIndex;

        public IList<ModelProfile> Profiles
        {
            get { return working.Select(profile => profile.Clone()).ToList(); }
        }

        public ModelProfile SelectedProfile { get; private set; }

        public bool Changed { get; private set; }

        public ModelProfilesDialog(
            IEnumerable<ModelProfile> profiles,
            ModelProfile current)
        {
            working = new List<ModelProfile>();
            if (profiles != null)
            {
                foreach (ModelProfile profile in profiles)
                {
                    if (profile != null)
                    {
                        working.Add(profile.Clone());
                    }
                }
            }

            selectedIndex = -1;
            SelectedProfile = null;
            Changed = false;

            Text = "模型配置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(820, 610);
            MinimumSize = new Size(760, 570);
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular);
            BackColor = UiTheme.WindowBackground;
            ForeColor = UiTheme.ButtonText;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(12);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 2;
            body.RowCount = 1;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.Padding = new Padding(0, 0, 10, 0);
            left.ColumnCount = 1;
            left.RowCount = 2;
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));

            profileList = new ListBox();
            profileList.Dock = DockStyle.Fill;
            profileList.BackColor = UiTheme.InputBackground;
            profileList.ForeColor = UiTheme.TextPrimary;
            profileList.BorderStyle = BorderStyle.FixedSingle;
            profileList.AccessibleName = "模型配置列表";
            profileList.SelectedIndexChanged += OnProfileSelected;

            FlowLayoutPanel listActions = new FlowLayoutPanel();
            listActions.Dock = DockStyle.Fill;
            listActions.WrapContents = false;
            listActions.FlowDirection = FlowDirection.LeftToRight;
            Button newButton = CreateButton("新建", 64);
            Button deleteButton = CreateButton("删除", 64);
            newButton.Click += OnNewProfile;
            deleteButton.Click += OnDeleteProfile;
            listActions.Controls.Add(newButton);
            listActions.Controls.Add(deleteButton);

            left.Controls.Add(profileList, 0, 0);
            left.Controls.Add(listActions, 0, 1);

            TableLayoutPanel editor = new TableLayoutPanel();
            editor.Dock = DockStyle.Fill;
            editor.Padding = new Padding(6, 0, 0, 0);
            editor.ColumnCount = 2;
            editor.RowCount = 14;
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94F));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));

            nameBox = CreateTextBox(false);
            endpointBox = CreateTextBox(false);
            apiKeyBox = CreateTextBox(false);
            apiKeyBox.UseSystemPasswordChar = true;
            modelBox = CreateTextBox(false);
            systemPromptBox = CreateTextBox(true);
            systemPromptBox.MaxLength = 16000;
            nameBox.AccessibleName = "模型配置名称";
            endpointBox.AccessibleName = "完整请求 URL";
            apiKeyBox.AccessibleName = "API Key";
            modelBox.AccessibleName = "模型名称";
            systemPromptBox.AccessibleName = "系统提示词";
            AddEditorRow(editor, 0, "名称", nameBox);
            AddEditorRow(editor, 2, "完整 URL", endpointBox);
            AddEditorRow(editor, 4, "API Key", apiKeyBox);
            AddEditorRow(editor, 6, "模型名称", modelBox);

            CheckBox showKey = new CheckBox();
            showKey.Text = "显示 Key";
            showKey.AutoSize = true;
            showKey.ForeColor = UiTheme.TextSecondary;
            showKey.Anchor = AnchorStyles.Left;
            showKey.CheckedChanged += delegate
            {
                apiKeyBox.UseSystemPasswordChar = !showKey.Checked;
            };
            editor.Controls.Add(showKey, 1, 8);

            AddEditorRow(editor, 9, "系统提示词", systemPromptBox);

            temperatureEnabled = CreateOptionCheckBox("启用");
            temperatureBox = CreateDecimalBox(0m, 2m, 0.7m, 2);
            temperatureBox.AccessibleName = "Temperature";
            AddOptionalNumberRow(
                editor,
                11,
                "Temperature",
                temperatureEnabled,
                temperatureBox);

            topPEnabled = CreateOptionCheckBox("启用");
            topPBox = CreateDecimalBox(0m, 1m, 1m, 2);
            topPBox.AccessibleName = "Top P";
            AddOptionalNumberRow(
                editor,
                12,
                "Top P",
                topPEnabled,
                topPBox);

            maxOutputTokensEnabled = CreateOptionCheckBox("启用");
            maxOutputTokensBox = CreateDecimalBox(
                1m,
                1048576m,
                4096m,
                0);
            maxOutputTokensBox.Increment = 256m;
            maxOutputTokensBox.AccessibleName = "最大输出 Token";
            AddOptionalNumberRow(
                editor,
                13,
                "最大输出",
                maxOutputTokensEnabled,
                maxOutputTokensBox);

            body.Controls.Add(left, 0, 0);
            body.Controls.Add(editor, 1, 0);

            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.ForeColor = UiTheme.TextMuted;
            statusLabel.AutoEllipsis = true;
            statusLabel.Text = "每个配置可保存连接、系统提示词和可选生成参数；API Key 可留空。";

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            Button applyButton = CreateButton("保存并应用", 96);
            applyButton.BackColor = UiTheme.Accent;
            applyButton.ForeColor = Color.White;
            applyButton.FlatAppearance.BorderSize = 0;
            applyButton.Click += OnApply;
            Button saveButton = CreateButton("保存配置", 90);
            saveButton.Click += OnSaveProfile;
            Button cancelButton = CreateButton("取消", 78);
            cancelButton.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(applyButton);
            actions.Controls.Add(saveButton);
            actions.Controls.Add(cancelButton);

            root.Controls.Add(body, 0, 0);
            root.Controls.Add(statusLabel, 0, 1);
            root.Controls.Add(actions, 0, 2);
            Controls.Add(root);
            AcceptButton = applyButton;
            CancelButton = cancelButton;

            ReloadProfiles(-1);
            LoadCurrentEditor(current);
        }

        private void LoadCurrentEditor(ModelProfile current)
        {
            if (selectedIndex >= 0)
            {
                return;
            }

            ModelProfile source = current == null
                ? new ModelProfile()
                : current;
            nameBox.Text = source.Name ?? string.Empty;
            endpointBox.Text = source.EndpointUrl ?? string.Empty;
            apiKeyBox.Text = source.ApiKey ?? string.Empty;
            modelBox.Text = source.ModelName ?? string.Empty;
            systemPromptBox.Text = source.SystemPrompt ?? string.Empty;
            SetOptionalValue(
                temperatureEnabled,
                temperatureBox,
                source.Temperature);
            SetOptionalValue(topPEnabled, topPBox, source.TopP);
            SetOptionalValue(
                maxOutputTokensEnabled,
                maxOutputTokensBox,
                source.MaxOutputTokens);
        }

        private void ReloadProfiles(int index)
        {
            refreshing = true;
            profileList.Items.Clear();
            foreach (ModelProfile profile in working)
            {
                profileList.Items.Add(profile);
            }

            if (working.Count == 0)
            {
                selectedIndex = -1;
                profileList.SelectedIndex = -1;
            }
            else
            {
                selectedIndex = Math.Max(0, Math.Min(index, working.Count - 1));
                profileList.SelectedIndex = selectedIndex;
            }

            refreshing = false;
            LoadSelectedEditor();
        }

        private void OnProfileSelected(object sender, EventArgs args)
        {
            if (refreshing)
            {
                return;
            }

            selectedIndex = profileList.SelectedIndex;
            LoadSelectedEditor();
        }

        private void LoadSelectedEditor()
        {
            if (selectedIndex < 0 || selectedIndex >= working.Count)
            {
                return;
            }

            ModelProfile profile = working[selectedIndex];
            nameBox.Text = profile.Name ?? string.Empty;
            endpointBox.Text = profile.EndpointUrl ?? string.Empty;
            apiKeyBox.Text = profile.ApiKey ?? string.Empty;
            modelBox.Text = profile.ModelName ?? string.Empty;
            systemPromptBox.Text = profile.SystemPrompt ?? string.Empty;
            SetOptionalValue(
                temperatureEnabled,
                temperatureBox,
                profile.Temperature);
            SetOptionalValue(topPEnabled, topPBox, profile.TopP);
            SetOptionalValue(
                maxOutputTokensEnabled,
                maxOutputTokensBox,
                profile.MaxOutputTokens);
        }

        private void OnNewProfile(object sender, EventArgs args)
        {
            selectedIndex = -1;
            refreshing = true;
            profileList.ClearSelected();
            refreshing = false;
            nameBox.Clear();
            endpointBox.Clear();
            apiKeyBox.Clear();
            modelBox.Clear();
            systemPromptBox.Clear();
            temperatureEnabled.Checked = false;
            topPEnabled.Checked = false;
            maxOutputTokensEnabled.Checked = false;
            SetStatus("填写名称、完整 URL 和模型名称后点击“保存配置”；API Key 可留空。", false);
            nameBox.Focus();
        }

        private void OnSaveProfile(object sender, EventArgs args)
        {
            ModelProfile candidate = ReadEditor();
            try
            {
                ModelProfileStore.Validate(candidate);
                int existing = FindByName(candidate.Name);
                if (existing >= 0 && existing != selectedIndex)
                {
                    throw new InvalidOperationException(
                        "已经存在同名模型配置，请换一个名称。");
                }

                if (selectedIndex >= 0 && selectedIndex < working.Count)
                {
                    working[selectedIndex] = candidate;
                }
                else
                {
                    working.Add(candidate);
                    selectedIndex = working.Count - 1;
                }

                Changed = true;
                ReloadProfiles(selectedIndex);
                SetStatus("配置已加入本次编辑列表，点击“保存并应用”后生效。", false);
            }
            catch (Exception exception)
            {
                SetStatus(exception.Message, true);
                MessageBox.Show(
                    this,
                    exception.Message,
                    "模型配置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void OnDeleteProfile(object sender, EventArgs args)
        {
            if (selectedIndex < 0 || selectedIndex >= working.Count)
            {
                return;
            }

            ModelProfile profile = working[selectedIndex];
            DialogResult confirm = MessageBox.Show(
                this,
                "确定删除模型配置“" + profile.Name + "”吗？",
                "删除模型配置",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            working.RemoveAt(selectedIndex);
            Changed = true;
            ReloadProfiles(Math.Max(0, selectedIndex - 1));
            SetStatus("已从本次编辑列表删除，应用后生效。", false);
        }

        private void OnApply(object sender, EventArgs args)
        {
            if (selectedIndex < 0 || selectedIndex >= working.Count)
            {
                if (Changed && working.Count == 0)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                SetStatus("请先保存并选择一个模型配置。", true);
                return;
            }

            SelectedProfile = working[selectedIndex].Clone();
            DialogResult = DialogResult.OK;
            Close();
        }

        private ModelProfile ReadEditor()
        {
            return new ModelProfile
            {
                Name = nameBox.Text,
                EndpointUrl = endpointBox.Text,
                ApiKey = apiKeyBox.Text,
                ModelName = modelBox.Text,
                SystemPrompt = systemPromptBox.Text,
                Temperature = temperatureEnabled.Checked
                    ? (double?)temperatureBox.Value
                    : null,
                TopP = topPEnabled.Checked
                    ? (double?)topPBox.Value
                    : null,
                MaxOutputTokens = maxOutputTokensEnabled.Checked
                    ? (int?)decimal.ToInt32(maxOutputTokensBox.Value)
                    : null
            };
        }

        private int FindByName(string name)
        {
            for (int index = 0; index < working.Count; index++)
            {
                if (string.Equals(
                    working[index].Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private void SetStatus(string text, bool error)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = error ? UiTheme.Danger : UiTheme.TextMuted;
        }

        private static TextBox CreateTextBox(bool multiline)
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Multiline = multiline;
            box.ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None;
            box.BackColor = UiTheme.InputBackground;
            box.ForeColor = UiTheme.TextPrimary;
            box.BorderStyle = BorderStyle.FixedSingle;
            return box;
        }

        private static CheckBox CreateOptionCheckBox(string text)
        {
            CheckBox checkBox = new CheckBox();
            checkBox.Text = text;
            checkBox.AutoSize = true;
            checkBox.Anchor = AnchorStyles.Left;
            checkBox.ForeColor = UiTheme.TextSecondary;
            return checkBox;
        }

        private static NumericUpDown CreateDecimalBox(
            decimal minimum,
            decimal maximum,
            decimal value,
            int decimalPlaces)
        {
            NumericUpDown box = new NumericUpDown();
            box.Minimum = minimum;
            box.Maximum = maximum;
            box.Value = value;
            box.DecimalPlaces = decimalPlaces;
            box.Width = 132;
            box.TextAlign = HorizontalAlignment.Right;
            box.BackColor = UiTheme.InputBackground;
            box.ForeColor = UiTheme.TextPrimary;
            return box;
        }

        private static void SetOptionalValue(
            CheckBox enabled,
            NumericUpDown box,
            double? value)
        {
            enabled.Checked = value.HasValue;
            if (value.HasValue)
            {
                box.Value = Math.Max(
                    box.Minimum,
                    Math.Min(box.Maximum, (decimal)value.Value));
            }
        }

        private static void SetOptionalValue(
            CheckBox enabled,
            NumericUpDown box,
            int? value)
        {
            enabled.Checked = value.HasValue;
            if (value.HasValue)
            {
                box.Value = Math.Max(
                    box.Minimum,
                    Math.Min(box.Maximum, value.Value));
            }
        }

        private static void AddOptionalNumberRow(
            TableLayoutPanel editor,
            int row,
            string labelText,
            CheckBox enabled,
            NumericUpDown box)
        {
            Label label = new Label();
            label.Text = labelText;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.ForeColor = UiTheme.TextSecondary;

            FlowLayoutPanel controls = new FlowLayoutPanel();
            controls.Dock = DockStyle.Fill;
            controls.FlowDirection = FlowDirection.LeftToRight;
            controls.WrapContents = false;
            controls.Margin = new Padding(0);
            controls.Padding = new Padding(0, 4, 0, 0);
            controls.Controls.Add(enabled);
            controls.Controls.Add(box);
            enabled.CheckedChanged += delegate { box.Enabled = enabled.Checked; };
            box.Enabled = enabled.Checked;

            editor.Controls.Add(label, 0, row);
            editor.Controls.Add(controls, 1, row);
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
            button.FlatAppearance.MouseOverBackColor = UiTheme.ButtonHover;
            button.FlatAppearance.MouseDownBackColor = UiTheme.ButtonPressed;
            button.BackColor = UiTheme.ButtonBackground;
            button.ForeColor = UiTheme.ButtonText;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private static void AddEditorRow(
            TableLayoutPanel editor,
            int labelRow,
            string labelText,
            Control control)
        {
            Label label = new Label();
            label.Text = labelText;
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.ForeColor = UiTheme.TextSecondary;
            editor.Controls.Add(label, 0, labelRow);
            editor.Controls.Add(control, 1, labelRow + 1);
        }
    }
}
