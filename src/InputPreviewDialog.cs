using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FilePromptWin7
{
    internal sealed class InputPreviewDialog : Form
    {
        private const int MaxPreviewCharacters = 300000;
        private Image previewImage;

        private InputPreviewDialog(InputItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException("item");
            }

            Text = "资料预览 · " + (item.Name ?? "未命名内容");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(620, 400);
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(
                Math.Max(MinimumSize.Width, Math.Min(820, workingArea.Width - 64)),
                Math.Max(MinimumSize.Height, Math.Min(580, workingArea.Height - 64)));
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular);
            BackColor = Color.FromArgb(31, 35, 42);
            AutoScaleMode = AutoScaleMode.None;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Escape)
                {
                    Close();
                    args.SuppressKeyPress = true;
                }
            };

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(12);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            Controls.Add(root);

            root.Controls.Add(CreateHeader(item), 0, 0);
            root.Controls.Add(CreateContent(item), 0, 1);
            root.Controls.Add(CreateFooter(), 0, 2);
        }

        public static void ShowPreview(IWin32Window owner, InputItem item)
        {
            using (InputPreviewDialog dialog = new InputPreviewDialog(item))
            {
                dialog.ShowDialog(owner);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && previewImage != null)
            {
                previewImage.Dispose();
                previewImage = null;
            }

            base.Dispose(disposing);
        }

        private Control CreateHeader(InputItem item)
        {
            TableLayoutPanel panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.ColumnCount = 1;
            panel.RowCount = 2;
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));

            Label title = new Label();
            title.Text = item.Name ?? "未命名内容";
            title.ForeColor = Color.FromArgb(242, 245, 249);
            title.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            title.AutoEllipsis = true;
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;

            Label detail = new Label();
            detail.Text = item.GetKindText() + "  ·  " + item.GetSizeText() +
                (string.IsNullOrWhiteSpace(item.Note)
                    ? string.Empty
                    : "  ·  " + item.Note);
            detail.ForeColor = Color.FromArgb(160, 173, 190);
            detail.AutoEllipsis = true;
            detail.Dock = DockStyle.Fill;
            detail.TextAlign = ContentAlignment.MiddleLeft;

            panel.Controls.Add(title, 0, 0);
            panel.Controls.Add(detail, 0, 1);
            return panel;
        }

        private Control CreateContent(InputItem item)
        {
            if (item.Kind == InputKind.Image &&
                item.BinaryData != null &&
                item.BinaryData.Length > 0)
            {
                try
                {
                    using (MemoryStream stream = new MemoryStream(
                        item.BinaryData,
                        false))
                    using (Image source = Image.FromStream(stream, true, true))
                    {
                        previewImage = new Bitmap(source);
                    }

                    PictureBox picture = new PictureBox();
                    picture.Dock = DockStyle.Fill;
                    picture.BackColor = Color.FromArgb(245, 247, 250);
                    picture.BorderStyle = BorderStyle.FixedSingle;
                    picture.SizeMode = PictureBoxSizeMode.Zoom;
                    picture.Image = previewImage;
                    picture.AccessibleName = "已添加图片预览";
                    return picture;
                }
                catch (Exception exception)
                {
                    return CreateMessage(
                        "图片无法预览，但仍可从资料列表移除或提交。\r\n\r\n" +
                        exception.Message);
                }
            }

            if (!string.IsNullOrEmpty(item.TextContent))
            {
                RichTextBox text = new RichTextBox();
                text.Dock = DockStyle.Fill;
                text.ReadOnly = true;
                text.BackColor = Color.White;
                text.ForeColor = Color.FromArgb(32, 37, 43);
                text.BorderStyle = BorderStyle.FixedSingle;
                text.DetectUrls = false;
                text.WordWrap = true;
                text.Font = new Font("Microsoft YaHei", 9F);
                text.AccessibleName = "已提取文字预览";
                string content = item.TextContent;
                if (content.Length > MaxPreviewCharacters)
                {
                    content = content.Substring(0, MaxPreviewCharacters) +
                        "\r\n\r\n[预览仅显示前 300,000 个字符，提交内容不受影响]";
                }

                text.Text = content;
                text.SelectionStart = 0;
                return text;
            }

            return CreateMessage(
                "此资料没有可显示的文字预览。程序只保存用户主动添加后的内容，" +
                "不会在预览时重新读取本地文件。");
        }

        private Control CreateMessage(string message)
        {
            Label label = new Label();
            label.Text = message;
            label.Dock = DockStyle.Fill;
            label.Padding = new Padding(24);
            label.BackColor = Color.White;
            label.ForeColor = Color.FromArgb(62, 70, 82);
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.BorderStyle = BorderStyle.FixedSingle;
            return label;
        }

        private Control CreateFooter()
        {
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.RightToLeft;
            panel.Padding = new Padding(0, 8, 0, 0);

            Button close = new Button();
            close.Text = "关闭";
            close.Width = 82;
            close.Height = 28;
            close.DialogResult = DialogResult.OK;
            close.FlatStyle = FlatStyle.Flat;
            close.BackColor = Color.FromArgb(47, 53, 63);
            close.ForeColor = Color.FromArgb(235, 239, 245);
            close.FlatAppearance.BorderColor = Color.FromArgb(91, 101, 116);
            panel.Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
            return panel;
        }
    }
}
