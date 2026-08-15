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
            ClientSize = new Size(700, 430);
            MinimumSize = new Size(640, 390);
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
            editor.RowCount = 9;
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

            nameBox = CreateTextBox(false);
            endpointBox = CreateTextBox(false);
            apiKeyBox = CreateTextBox(false);
            apiKeyBox.UseSystemPasswordChar = true;
            modelBox = CreateTextBox(false);
            nameBox.AccessibleName = "模型配置名称";
            endpointBox.AccessibleName = "完整请求 URL";
            apiKeyBox.AccessibleName = "API Key";
            modelBox.AccessibleName = "模型名称";
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

            body.Controls.Add(left, 0, 0);
            body.Controls.Add(editor, 1, 0);

            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.ForeColor = UiTheme.TextMuted;
            statusLabel.AutoEllipsis = true;
            statusLabel.Text = "可保存多个内网模型配置，API Key 只保存在当前 Windows 用户下。";

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
            SetStatus("填写名称、完整 URL、API Key 和模型名称后点击“保存配置”。", false);
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
                ModelName = modelBox.Text
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
