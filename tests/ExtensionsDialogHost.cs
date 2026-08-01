using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class ExtensionsDialogHost
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            return 2;
        }

        string applicationPath = Path.GetFullPath(args[0]);
        string applicationDirectory = Path.GetDirectoryName(applicationPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(
            object sender,
            ResolveEventArgs eventArgs)
        {
            string candidate = Path.Combine(
                applicationDirectory,
                new AssemblyName(eventArgs.Name).Name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Assembly application = Assembly.LoadFrom(applicationPath);
        Type settingsType = application.GetType(
            "FilePromptWin7.ExtensionSettings",
            true);
        object settings = Activator.CreateInstance(settingsType, true);
        AddSampleSkill(application, settingsType, settings);
        AddSampleServer(application, settingsType, settings);

        Type dialogType = application.GetType(
            "FilePromptWin7.ExtensionsDialog",
            true);
        using (Form dialog = Activator.CreateInstance(
            dialogType,
            new[] { settings }) as Form)
        {
            if (dialog == null)
            {
                return 3;
            }

            dialog.ShowInTaskbar = true;

            TabControl tabs = FindTabControl(dialog);
            if (tabs != null && string.Equals(
                args[1],
                "mcp",
                StringComparison.OrdinalIgnoreCase))
            {
                tabs.SelectedIndex = 1;
            }

            Application.Run(dialog);
        }

        return 0;
    }

    private static void AddSampleSkill(
        Assembly application,
        Type settingsType,
        object settings)
    {
        Type skillType = application.GetType(
            "FilePromptWin7.SkillDefinition",
            true);
        object skill = Activator.CreateInstance(skillType, true);
        skillType.GetProperty("Name").SetValue(skill, "合同审阅", null);
        skillType.GetProperty("Description").SetValue(
            skill,
            "单位内部审阅规则",
            null);
        skillType.GetProperty("Instructions").SetValue(
            skill,
            "先列风险，再逐条引用依据。",
            null);
        ((IList)settingsType.GetProperty("Skills").GetValue(
            settings,
            null)).Add(skill);
    }

    private static void AddSampleServer(
        Assembly application,
        Type settingsType,
        object settings)
    {
        Type serverType = application.GetType(
            "FilePromptWin7.McpServerDefinition",
            true);
        object server = Activator.CreateInstance(serverType, true);
        serverType.GetProperty("Name").SetValue(
            server,
            "内网工具",
            null);
        serverType.GetProperty("Transport").SetValue(
            server,
            "stdio",
            null);
        serverType.GetProperty("Command").SetValue(
            server,
            @"D:\MCP\server.exe",
            null);
        ((IList)settingsType.GetProperty("McpServers").GetValue(
            settings,
            null)).Add(server);
    }

    private static TabControl FindTabControl(Control root)
    {
        foreach (Control child in root.Controls)
        {
            TabControl match = child as TabControl;
            if (match != null)
            {
                return match;
            }

            match = FindTabControl(child);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }
}
