using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

using FilePromptAIWin7;

internal static class MarkdownRendererSmokeTest
{
    [STAThread]
    private static int Main()
    {
        try
        {
            AssertEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState(), "STA thread");
            Application.SetCompatibleTextRenderingDefault(false);
            using (Form host = new Form())
            using (RichTextBox output = new RichTextBox())
            {
                host.ShowInTaskbar = false;
                host.Size = new Size(900, 700);
                output.Dock = DockStyle.Fill;
                output.ReadOnly = true;
                output.Font = new Font("Microsoft YaHei", 9.0F, FontStyle.Regular);
                host.Controls.Add(output);
                host.CreateControl();
                output.CreateControl();

                MarkdownRichTextRenderer.Render(output, null);
                AssertEqual(string.Empty, output.Text, "empty markdown");

                string markdown =
                    "# \u4E2D\u6587\u6807\u9898\n\n" +
                    "\u666E\u901A\u6BB5\u843D\u5305\u542B **\u91CD\u70B9\u6587\u5B57** \u548C `inline_code()`\u3002\n\n" +
                    "- \u65E0\u5E8F\u9879\u4E00\n" +
                    "- \u65E0\u5E8F\u9879\u4E8C\n\n" +
                    "3. \u6709\u5E8F\u6B65\u9AA4\u7532\n" +
                    "4. \u6709\u5E8F\u6B65\u9AA4\u4E59\n\n" +
                    "> \u8FD9\u662F\u4E00\u6BB5\u4E2D\u6587\u5F15\u7528\n\n" +
                    "```csharp\n" +
                    "string value = \"\u4E2D\u6587\";\n" +
                    "Console.WriteLine(value);\n" +
                    "```\n\n" +
                    "| \u540D\u79F0 | \u5185\u5BB9 |\n" +
                    "| --- | --- |\n" +
                    "| \u8868\u683C\u4E2D\u6587 | **\u8868\u683C\u503C** |";

                MarkdownRichTextRenderer.Render(output, markdown);
                string rendered = output.Text;
                AssertContains(rendered, "\u4E2D\u6587\u6807\u9898", "Chinese heading");
                AssertContains(rendered, "\u91CD\u70B9\u6587\u5B57", "bold text");
                AssertContains(rendered, "inline_code()", "inline code");
                AssertContains(rendered, "string value = \"\u4E2D\u6587\";", "code block");
                AssertContains(rendered, "Console.WriteLine(value);", "second code line");
                AssertContains(rendered, "\u8FD9\u662F\u4E00\u6BB5\u4E2D\u6587\u5F15\u7528", "quote");
                AssertContains(rendered, "\u8868\u683C\u4E2D\u6587", "table text");
                AssertContains(rendered, "\u8868\u683C\u503C", "formatted table text");
                AssertContains(rendered, "\t", "table column alignment");
                AssertNotContains(rendered, "# \u4E2D\u6587", "heading marker");
                AssertNotContains(rendered, "**", "bold marker");
                AssertNotContains(rendered, "`", "inline and fenced code markers");
                AssertNotContains(rendered, "| ---", "table delimiter row");
                AssertNotContains(rendered, "> \u8FD9\u662F", "quote marker");
                AssertNotContains(rendered, "- \u65E0\u5E8F", "unordered list marker");

                AssertBold(output, "\u4E2D\u6587\u6807\u9898", "heading format");
                AssertLargerThanBody(output, "\u4E2D\u6587\u6807\u9898", "heading size");
                AssertBold(output, "\u91CD\u70B9\u6587\u5B57", "inline bold format");
                AssertMonospace(output, "inline_code()", "inline code format");
                AssertMonospace(output, "Console.WriteLine(value);", "code block format");
                AssertBold(output, "\u540D\u79F0", "table header format");
                AssertBullet(output, "\u65E0\u5E8F\u9879\u4E00", "unordered list format");
                AssertIndent(output, "Console.WriteLine(value);", 12, "code block indent");
                AssertIndent(output, "\u8FD9\u662F\u4E00\u6BB5\u4E2D\u6587\u5F15\u7528", 18, "quote indent");

                int oldLength = output.TextLength;
                MarkdownRichTextRenderer.Append(output, "\n\n## \u8FFD\u52A0\u6807\u9898");
                AssertTrue(output.TextLength > oldLength, "append adds content");
                AssertContains(output.Text, "\u8FFD\u52A0\u6807\u9898", "append heading text");
                AssertNotContains(output.Text, "## \u8FFD\u52A0", "append heading marker");
            }

            Console.WriteLine("PASS | markdown RichTextBox renderer");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL | markdown RichTextBox renderer");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void AssertBold(RichTextBox output, string value, string name)
    {
        Font font = SelectFont(output, value, name);
        AssertTrue(font.Bold, name + " is bold");
    }

    private static void AssertLargerThanBody(RichTextBox output, string value, string name)
    {
        Font font = SelectFont(output, value, name);
        AssertTrue(font.SizeInPoints > output.Font.SizeInPoints, name + " is larger than body");
    }

    private static void AssertMonospace(RichTextBox output, string value, string name)
    {
        Font font = SelectFont(output, value, name);
        AssertTrue(
            string.Equals(font.Name, "Consolas", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(font.Name, "Courier New", StringComparison.OrdinalIgnoreCase),
            name + " uses a monospace font; actual font was " + font.Name);
    }

    private static void AssertBullet(RichTextBox output, string value, string name)
    {
        SelectText(output, value, name);
        AssertTrue(output.SelectionBullet, name + " uses native bullet formatting");
    }

    private static void AssertIndent(
        RichTextBox output,
        string value,
        int expectedMinimum,
        string name)
    {
        SelectText(output, value, name);
        AssertTrue(
            output.SelectionIndent >= expectedMinimum,
            name + " has readable paragraph indentation");
    }

    private static Font SelectFont(RichTextBox output, string value, string name)
    {
        SelectText(output, value, name);
        Font font = output.SelectionFont;
        AssertTrue(font != null, name + " has one consistent font");
        return font;
    }

    private static void SelectText(RichTextBox output, string value, string name)
    {
        int index = output.Text.IndexOf(value, StringComparison.Ordinal);
        AssertTrue(index >= 0, name + " text exists");
        output.Select(index, value.Length);
    }

    private static void AssertContains(string actual, string expected, string name)
    {
        AssertTrue(actual.IndexOf(expected, StringComparison.Ordinal) >= 0, name + " is retained");
    }

    private static void AssertNotContains(string actual, string unexpected, string name)
    {
        AssertTrue(actual.IndexOf(unexpected, StringComparison.Ordinal) < 0, name + " is removed");
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!object.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                name + " mismatch. Expected [" + expected + "] but got [" + actual + "].");
        }
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " failed.");
        }
    }
}
