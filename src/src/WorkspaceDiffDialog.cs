using System;
using System.Drawing;
using System.Windows.Forms;

namespace FilePromptAIWin7
{
    internal sealed class WorkspaceDiffDialog : Form
    {
        private WorkspaceDiffDialog(
            IWin32Window owner,
            Font ownerFont,
            string relativePath,
            string expectedSha256,
            string unifiedDiff)
        {
            Text = "确认代码修改";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(680, 460);
            Rectangle workingArea = owner != null && owner.Handle != IntPtr.Zero
                ? Screen.FromHandle(owner.Handle).WorkingArea
                : Screen.PrimaryScreen.WorkingArea;
            Size = new Size(
                Math.Max(MinimumSize.Width, Math.Min(900, workingArea.Width - 64)),
                Math.Max(MinimumSize.Height, Math.Min(680, workingArea.Height - 64)));
            Font = ownerFont ?? new Font("Microsoft YaHei", 9F);
            BackColor = UiTheme.WindowBackground;
            ForeColor = UiTheme.TextPrimary;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            KeyPreview = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            Controls.Add(root);

            Label title = new Label();
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            title.ForeColor = UiTheme.TextPrimary;
            title.AutoEllipsis = true;
            title.Text = string.IsNullOrWhiteSpace(relativePath)
                ? "未命名文件"
                : relativePath;
            title.AccessibleName = "待修改的工作区相对路径";

            Label notice = new Label();
            notice.Dock = DockStyle.Fill;
            notice.TextAlign = ContentAlignment.MiddleLeft;
            notice.ForeColor = UiTheme.TextSecondary;
            notice.AutoEllipsis = true;
            notice.Text =
                "仅会修改所选工作区内的这个文件。应用前程序会再次核对原文件摘要。\r\n" +
                "读取时 SHA-256：" + ShortHash(expectedSha256);

            RichTextBox diff = new RichTextBox();
            diff.Dock = DockStyle.Fill;
            diff.ReadOnly = true;
            diff.WordWrap = false;
            diff.DetectUrls = false;
            diff.BorderStyle = BorderStyle.FixedSingle;
            diff.BackColor = Color.White;
            diff.ForeColor = UiTheme.TextPrimary;
            diff.Font = new Font("Consolas", 9F, FontStyle.Regular);
            diff.Text = string.IsNullOrEmpty(unifiedDiff)
                ? "[没有可显示的差异，修改不会应用]"
                : unifiedDiff;
            diff.SelectionStart = 0;
            diff.HideSelection = false;
            diff.AccessibleName = "代码修改完整差异";

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Padding = new Padding(0, 10, 0, 0);

            Button reject = CreateDialogButton("拒绝", 90);
            reject.DialogResult = DialogResult.No;
            reject.AccessibleName = "拒绝本次代码修改";
            Button approve = CreateDialogButton("应用修改", 104);
            approve.DialogResult = DialogResult.Yes;
            approve.AccessibleName = "确认并应用当前显示的代码修改";
            approve.BackColor = UiTheme.Accent;
            approve.ForeColor = Color.White;
            approve.FlatAppearance.BorderColor = UiTheme.Accent;
            approve.FlatAppearance.MouseOverBackColor = UiTheme.AccentHover;
            approve.FlatAppearance.MouseDownBackColor = UiTheme.AccentPressed;
            approve.Enabled = !string.IsNullOrEmpty(unifiedDiff);

            actions.Controls.Add(reject);
            actions.Controls.Add(approve);
            root.Controls.Add(title, 0, 0);
            root.Controls.Add(notice, 0, 1);
            root.Controls.Add(diff, 0, 2);
            root.Controls.Add(actions, 0, 3);

            CancelButton = reject;
            Shown += delegate { reject.Focus(); };
            KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Escape)
                {
                    DialogResult = DialogResult.No;
                    Close();
                    args.SuppressKeyPress = true;
                }
            };
        }

        public static bool Confirm(
            IWin32Window owner,
            Font ownerFont,
            string relativePath,
            string expectedSha256,
            string unifiedDiff)
        {
            using (WorkspaceDiffDialog dialog = new WorkspaceDiffDialog(
                owner,
                ownerFont,
                relativePath,
                expectedSha256,
                unifiedDiff))
            {
                return dialog.ShowDialog(owner) == DialogResult.Yes;
            }
        }

        private static Button CreateDialogButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 30;
            button.Margin = new Padding(6, 0, 0, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = UiTheme.ButtonBackground;
            button.ForeColor = UiTheme.ButtonText;
            button.FlatAppearance.BorderColor = UiTheme.BorderColor;
            button.FlatAppearance.MouseOverBackColor = UiTheme.ButtonHover;
            button.FlatAppearance.MouseDownBackColor = UiTheme.ButtonPressed;
            return button;
        }

        private static string ShortHash(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "未知";
            }

            return normalized.Length <= 16
                ? normalized
                : normalized.Substring(0, 16) + "...";
        }
    }
}
