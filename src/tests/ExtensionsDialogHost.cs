using System;
using System.Collections;
using System.Collections.Generic;
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
        if (string.Equals(
            args[1],
            "settings",
            StringComparison.OrdinalIgnoreCase))
        {
            return RunSettingsDialog(application);
        }

        Type settingsType = application.GetType(
            "FilePromptAIWin7.ExtensionSettings",
            true);
        object settings = Activator.CreateInstance(settingsType, true);
        AddSampleSkill(application, settingsType, settings);
        AddSampleServer(application, settingsType, settings);

        Type dialogType = application.GetType(
            "FilePromptAIWin7.ExtensionsDialog",
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

    private static int RunSettingsDialog(Assembly application)
    {
        Type dialogType = application.GetType(
            "FilePromptAIWin7.SettingsDialog",
            true);
        using (Form dialog = Activator.CreateInstance(
            dialogType,
            true) as Form)
        {
            if (dialog == null)
            {
                return 3;
            }

            TextBox endpoint = dialogType.GetProperty(
                "EndpointTextBox").GetValue(dialog, null) as TextBox;
            TextBox apiKey = dialogType.GetProperty(
                "ApiKeyTextBox").GetValue(dialog, null) as TextBox;
            ComboBox model = dialogType.GetProperty(
                "ModelTextBox").GetValue(dialog, null) as ComboBox;
            if (endpoint != null)
            {
                endpoint.Text =
                    "https://api.example.com/v1/chat/completions";
            }
            if (apiKey != null)
            {
                apiKey.Text = "ui-smoke-key";
            }
            dialogType.GetMethod("SetAvailableModels").Invoke(
                dialog,
                new object[]
                {
                    new List<string>
                    {
                        "gpt-4.1-mini",
                        "gpt-4.1",
                        "o4-mini"
                    }
                });
            if (model != null)
            {
                model.Text = "gpt-4.1-mini";
            }

            dialog.ShowInTaskbar = true;
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
            "FilePromptAIWin7.SkillDefinition",
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
            "FilePromptAIWin7.McpServerDefinition",
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
