using System.Drawing;
using System.Windows.Forms;

namespace FilePromptAIWin7
{
    internal sealed class PathInputDialog : Form
    {
        public TextBox PathsTextBox { get; private set; }

        public Button ReadPathButton { get; private set; }

        public Button AddButton
        {
            get { return ReadPathButton; }
        }

        public PathInputDialog()
        {
            Text = "按路径添加文件";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 330);
            MinimumSize = new Size(520, 330);
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular);
            BackColor = UiTheme.WindowBackground;
            ForeColor = UiTheme.TextPrimary;
            AutoScaleMode = AutoScaleMode.None;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

            Label heading = new Label();
            heading.Dock = DockStyle.Fill;
            heading.Text = "文件路径";
            heading.Font = new Font(Font, FontStyle.Bold);
            heading.TextAlign = ContentAlignment.MiddleLeft;
            heading.ForeColor = UiTheme.TextPrimary;

            Label guidance = new Label();
            guidance.Dock = DockStyle.Fill;
            guidance.Text = "每行一个文件路径；只读取列出的文件，不扫描目录。";
            guidance.TextAlign = ContentAlignment.TopLeft;
            guidance.ForeColor = UiTheme.TextSecondary;
            guidance.AutoEllipsis = true;

            PathsTextBox = new TextBox();
            PathsTextBox.Dock = DockStyle.Fill;
            PathsTextBox.Multiline = true;
            PathsTextBox.AcceptsReturn = true;
            PathsTextBox.AcceptsTab = false;
            PathsTextBox.ScrollBars = ScrollBars.Vertical;
            PathsTextBox.WordWrap = false;
            PathsTextBox.BorderStyle = BorderStyle.FixedSingle;
            PathsTextBox.BackColor = UiTheme.InputBackground;
            PathsTextBox.ForeColor = UiTheme.TextPrimary;
            PathsTextBox.AccessibleName = "每行一个文件路径";

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Padding = new Padding(0, 7, 0, 0);

            ReadPathButton = CreateButton("添加", 86);
            ReadPathButton.DialogResult = DialogResult.OK;
            ReadPathButton.BackColor = UiTheme.Accent;
            ReadPathButton.ForeColor = Color.White;
            ReadPathButton.FlatAppearance.BorderSize = 0;
            Button cancelButton = CreateButton("取消", 82);
            cancelButton.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(ReadPathButton);
            actions.Controls.Add(cancelButton);

            root.Controls.Add(heading, 0, 0);
            root.Controls.Add(guidance, 0, 1);
            root.Controls.Add(PathsTextBox, 0, 2);
            root.Controls.Add(actions, 0, 3);
            Controls.Add(root);

            AcceptButton = ReadPathButton;
            CancelButton = cancelButton;
            Shown += delegate { PathsTextBox.Focus(); };
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
    }
}
