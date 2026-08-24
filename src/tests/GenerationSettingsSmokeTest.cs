using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class GenerationSettingsSmokeTest
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args != null && args.Length == 4 &&
            args[0] == "--hold-exclusive-lock")
        {
            return HoldFileLock(args[1], args[2], args[3], true);
        }

        if (args != null && args.Length == 4 &&
            args[0] == "--hold-readable-lock")
        {
            return HoldFileLock(args[1], args[2], args[3], false);
        }

        string dataRoot = Path.Combine(
            Path.GetTempPath(),
            "FilePromptAIGenerationSettings-" + Guid.NewGuid().ToString("N"));
        string previousRoot = Environment.GetEnvironmentVariable(
            "FILEPROMPTAI_DATA_ROOT");
        object form = null;
        try
        {
            if (args.Length != 1)
            {
                throw new ArgumentException(
                    "Usage: GenerationSettingsSmokeTest <FilePromptAI.exe>");
            }

            string applicationPath = Path.GetFullPath(args[0]);
            ConfigureAssemblyResolution(applicationPath);
            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                dataRoot);
            Directory.CreateDirectory(dataRoot);

            Assembly application = Assembly.LoadFrom(applicationPath);
            TestAppSettings(application, dataRoot);
            TestModelRequestValidation(application);

            DeleteSettingsFile(application);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Type formType = application.GetType(
                "FilePromptAIWin7.MainForm",
                true);
            form = Activator.CreateInstance(formType, true);
            TestCombinedSystemPromptAndBudget(application, formType, form);
            TestGenerationOptionControls(application, formType, form);

            Console.WriteLine("PASS | generation settings smoke test");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | generation settings smoke test");
            Console.Error.WriteLine(Unwrap(exception));
            return 1;
        }
        finally
        {
            IDisposable disposable = form as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }

            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                previousRoot);
            try
            {
                if (Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, true);
                }
            }
            catch
            {
                // The uniquely named test directory can be removed later.
            }
        }
    }

    private static void TestAppSettings(Assembly application, string dataRoot)
    {
        Type settingsType = application.GetType(
            "FilePromptAIWin7.AppSettings",
            true);
        object settings = Activator.CreateInstance(settingsType, true);
        SetProperty(settings, "EndpointUrl", "https://example.invalid/v1/chat/completions");
        SetProperty(settings, "ApiKey", "round-trip-key");
        SetProperty(settings, "ModelName", "round-trip-model");
        SetProperty(settings, "SendShortcut", "CtrlEnter");
        SetProperty(settings, "SystemPrompt", "Round-trip system prompt.");
        SetProperty(settings, "Temperature", (double?)0.35d);
        SetProperty(settings, "TopP", (double?)0.85d);
        SetProperty(settings, "MaxOutputTokens", (int?)4096);
        InvokePublic(settings, "Save");

        object loaded = InvokeStaticPublic(settingsType, "Load");
        Assert(
            (string)GetProperty(loaded, "EndpointUrl") ==
                "https://example.invalid/v1/chat/completions" &&
            (string)GetProperty(loaded, "ApiKey") == "round-trip-key" &&
            (string)GetProperty(loaded, "ModelName") == "round-trip-model" &&
            (string)GetProperty(loaded, "SendShortcut") == "CtrlEnter",
            "AppSettings existing fields round-trip");
        Assert(
            (string)GetProperty(loaded, "SystemPrompt") ==
                "Round-trip system prompt." &&
            (double?)GetProperty(loaded, "Temperature") == 0.35d &&
            (double?)GetProperty(loaded, "TopP") == 0.85d &&
            (int?)GetProperty(loaded, "MaxOutputTokens") == 4096,
            "AppSettings generation fields round-trip");

        string settingsPath = (string)settingsType.GetProperty(
            "SettingsPath",
            BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
        WriteSettingsXml(
            settingsPath,
            "<EndpointUrl>http://legacy.invalid/chat</EndpointUrl>" +
            "<ModelName>legacy-model</ModelName>" +
            "<SendShortcut>Enter</SendShortcut>");
        object legacy = InvokeStaticPublic(settingsType, "Load");
        Assert(
            (string)GetProperty(legacy, "EndpointUrl") ==
                "http://legacy.invalid/chat" &&
            (string)GetProperty(legacy, "ModelName") == "legacy-model" &&
            (string)GetProperty(legacy, "SendShortcut") == "Enter" &&
            (string)GetProperty(legacy, "SystemPrompt") == string.Empty &&
            GetProperty(legacy, "Temperature") == null &&
            GetProperty(legacy, "TopP") == null &&
            GetProperty(legacy, "MaxOutputTokens") == null,
            "AppSettings legacy file defaults missing generation fields");

        AssertInvalidDoubleSettings(
            settingsType,
            settingsPath,
            "Temperature",
            new[]
            {
                "-0.01",
                "2.01",
                "NaN",
                "Infinity",
                "-Infinity",
                "invalid"
            });
        AssertInvalidDoubleSettings(
            settingsType,
            settingsPath,
            "TopP",
            new[]
            {
                "-0.01",
                "1.01",
                "NaN",
                "Infinity",
                "-Infinity",
                "invalid"
            });
        AssertInvalidIntegerSettings(
            settingsType,
            settingsPath,
            new[] { "0", "1048577", "1.5", "invalid" });
        Assert(Directory.Exists(dataRoot), "AppSettings uses isolated data root");
        TestAppSettingsCorruption(settingsType, dataRoot);
        TestAppSettingsExclusiveLock(settingsType, dataRoot);
    }

    private static void TestAppSettingsCorruption(
        Type settingsType,
        string dataRoot)
    {
        string previousRoot = Environment.GetEnvironmentVariable(
            "FILEPROMPTAI_DATA_ROOT");
        string root = Path.Combine(dataRoot, "damaged-settings");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("FILEPROMPTAI_DATA_ROOT", root);
        try
        {
            string path = (string)settingsType.GetProperty(
                "SettingsPath",
                BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
            byte[] damaged = Encoding.UTF8.GetBytes("<FilePromptAISettings");
            File.WriteAllBytes(path, damaged);
            object loaded = InvokeStaticPublic(settingsType, "Load");
            Assert(
                !Convert.ToBoolean(GetProperty(loaded, "IsWriteBlocked")) &&
                !File.Exists(path) &&
                Directory.GetFiles(
                    root,
                    "settings.xml.corrupt-*.xml").Length == 1,
                "damaged AppSettings safely preserved before rebuild");

            string blockedRoot = Path.Combine(dataRoot, "blocked-damage");
            Directory.CreateDirectory(blockedRoot);
            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                blockedRoot);
            path = (string)settingsType.GetProperty(
                "SettingsPath",
                BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
            File.WriteAllBytes(path, damaged);
            string ready = path + ".ready";
            string release = path + ".release";
            Process holder = StartLockHolder(
                "--hold-readable-lock",
                path,
                ready,
                release);
            object protectedSettings;
            try
            {
                WaitForLockHolder(holder, ready);
                protectedSettings = InvokeStaticPublic(settingsType, "Load");
                Assert(
                    Convert.ToBoolean(GetProperty(
                        protectedSettings,
                        "IsWriteBlocked")) &&
                    ((string)GetProperty(
                        protectedSettings,
                        "LoadWarning")).IndexOf(
                            "无法创建安全备份",
                            StringComparison.Ordinal) >= 0,
                    "failed AppSettings damage backup enables protection");
                Assert(
                    damaged.SequenceEqual(File.ReadAllBytes(path)) &&
                    Directory.GetFiles(
                        blockedRoot,
                        "settings.xml.corrupt-*.xml").Length == 0,
                    "failed AppSettings damage backup preserves bytes");
            }
            finally
            {
                ReleaseLockHolder(holder, release);
            }

            Exception saveFailure = CaptureFailure(delegate
            {
                InvokePublic(protectedSettings, "Save");
            });
            Assert(
                saveFailure is InvalidOperationException &&
                damaged.SequenceEqual(File.ReadAllBytes(path)),
                "protected damaged AppSettings rejects later save");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                previousRoot);
        }
    }

    private static void TestAppSettingsExclusiveLock(
        Type settingsType,
        string dataRoot)
    {
        string previousRoot = Environment.GetEnvironmentVariable(
            "FILEPROMPTAI_DATA_ROOT");
        string root = Path.Combine(dataRoot, "locked-settings");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("FILEPROMPTAI_DATA_ROOT", root);
        try
        {
            object expected = Activator.CreateInstance(settingsType, true);
            SetProperty(expected, "EndpointUrl", "http://127.0.0.1/locked");
            SetProperty(expected, "ApiKey", "locked-key");
            SetProperty(expected, "ModelName", "locked-model");
            InvokePublic(expected, "Save");
            string path = (string)settingsType.GetProperty(
                "SettingsPath",
                BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
            byte[] original = File.ReadAllBytes(path);
            string ready = path + ".ready";
            string release = path + ".release";
            Process holder = StartLockHolder(
                "--hold-exclusive-lock",
                path,
                ready,
                release);
            object protectedSettings;
            try
            {
                WaitForLockHolder(holder, ready);
                protectedSettings = InvokeStaticPublic(settingsType, "Load");
                Assert(
                    Convert.ToBoolean(GetProperty(
                        protectedSettings,
                        "IsWriteBlocked")),
                    "exclusive AppSettings lock enables sticky protection");
                Assert(
                    ((string)GetProperty(
                        protectedSettings,
                        "LoadWarning")).IndexOf(
                            "无法安全读取",
                            StringComparison.Ordinal) >= 0,
                    "exclusive AppSettings lock reports access warning");
                Assert(
                    File.Exists(path) &&
                    Directory.GetFiles(
                        root,
                        "settings.xml.corrupt-*.xml").Length == 0,
                    "exclusive AppSettings lock creates no corrupt backup");
            }
            finally
            {
                ReleaseLockHolder(holder, release);
            }

            Assert(
                original.SequenceEqual(File.ReadAllBytes(path)),
                "exclusive AppSettings lock preserves original bytes");
            object loaded = InvokeStaticPublic(settingsType, "Load");
            Assert(
                (string)GetProperty(loaded, "EndpointUrl") ==
                    "http://127.0.0.1/locked" &&
                (string)GetProperty(loaded, "ApiKey") == "locked-key" &&
                (string)GetProperty(loaded, "ModelName") == "locked-model",
                "AppSettings loads after exclusive lock release");
            Exception saveFailure = CaptureFailure(delegate
            {
                InvokePublic(protectedSettings, "Save");
            });
            Assert(
                saveFailure is InvalidOperationException &&
                original.SequenceEqual(File.ReadAllBytes(path)),
                "AppSettings write protection remains sticky after release");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                previousRoot);
        }
    }

    private static void AssertInvalidDoubleSettings(
        Type settingsType,
        string settingsPath,
        string propertyName,
        string[] invalidValues)
    {
        foreach (string invalidValue in invalidValues)
        {
            string temperature = propertyName == "Temperature"
                ? invalidValue
                : "0.5";
            string topP = propertyName == "TopP" ? invalidValue : "0.5";
            WriteSettingsXml(
                settingsPath,
                "<Temperature>" + temperature + "</Temperature>" +
                "<TopP>" + topP + "</TopP>" +
                "<MaxOutputTokens>1024</MaxOutputTokens>");
            object loaded = InvokeStaticPublic(settingsType, "Load");
            Assert(
                GetProperty(loaded, propertyName) == null,
                "AppSettings ignores invalid " + propertyName +
                    " value " + invalidValue);
            Assert(
                (int?)GetProperty(loaded, "MaxOutputTokens") == 1024,
                "AppSettings keeps valid peer fields for " + propertyName);
        }
    }

    private static void AssertInvalidIntegerSettings(
        Type settingsType,
        string settingsPath,
        string[] invalidValues)
    {
        foreach (string invalidValue in invalidValues)
        {
            WriteSettingsXml(
                settingsPath,
                "<Temperature>0.5</Temperature>" +
                "<TopP>0.5</TopP>" +
                "<MaxOutputTokens>" + invalidValue +
                    "</MaxOutputTokens>");
            object loaded = InvokeStaticPublic(settingsType, "Load");
            Assert(
                GetProperty(loaded, "MaxOutputTokens") == null,
                "AppSettings ignores invalid MaxOutputTokens value " +
                    invalidValue);
            Assert(
                (double?)GetProperty(loaded, "Temperature") == 0.5d &&
                (double?)GetProperty(loaded, "TopP") == 0.5d,
                "AppSettings keeps valid peer generation fields");
        }
    }

    private static void WriteSettingsXml(string path, string innerXml)
    {
        string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<FilePromptAISettings version=\"1\">" + innerXml +
            "</FilePromptAISettings>";
        File.WriteAllText(path, xml, new UTF8Encoding(false));
    }

    private static void DeleteSettingsFile(Assembly application)
    {
        Type settingsType = application.GetType(
            "FilePromptAIWin7.AppSettings",
            true);
        string settingsPath = (string)settingsType.GetProperty(
            "SettingsPath",
            BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
        if (File.Exists(settingsPath))
        {
            File.Delete(settingsPath);
        }
    }

    private static void TestCombinedSystemPromptAndBudget(
        Assembly application,
        Type formType,
        object form)
    {
        TextBox systemPrompt = (TextBox)GetField(
            formType,
            form,
            "systemPromptTextBox");
        Type settingsType = application.GetType(
            "FilePromptAIWin7.ExtensionSettings",
            true);
        Type skillType = application.GetType(
            "FilePromptAIWin7.SkillDefinition",
            true);

        object settings = Activator.CreateInstance(settingsType, true);
        IList skills = (IList)GetProperty(settings, "Skills");
        skills.Add(CreateSkill(
            skillType,
            "Enabled skill",
            "ENABLED_SKILL_INSTRUCTION",
            true));
        skills.Add(CreateSkill(
            skillType,
            "Disabled skill",
            "DISABLED_SKILL_INSTRUCTION",
            false));
        SetField(formType, form, "extensionSettings", settings);
        long skillCharacters = Convert.ToInt64(InvokePrivateStatic(
            formType,
            "CalculateExtensionPromptCharacterEstimate",
            settings));
        SetField(
            formType,
            form,
            "extensionPromptCharacterEstimate",
            skillCharacters);
        systemPrompt.Text = "  CUSTOM_SYSTEM_PROMPT  ";

        string combined = (string)InvokePrivate(
            formType,
            form,
            "BuildCombinedSystemPrompt");
        string skillPrompt = (string)InvokePublic(settings, "BuildSystemPrompt");
        Assert(
            combined == "CUSTOM_SYSTEM_PROMPT\r\n\r\n" + skillPrompt,
            "custom system prompt precedes enabled skill instructions");
        Assert(
            combined.IndexOf(
                "ENABLED_SKILL_INSTRUCTION",
                StringComparison.Ordinal) > 0 &&
            combined.IndexOf(
                "DISABLED_SKILL_INSTRUCTION",
                StringComparison.Ordinal) < 0,
            "combined system prompt includes only enabled skills");
        long estimated = Convert.ToInt64(InvokePrivate(
            formType,
            form,
            "GetSystemPromptCharacterEstimate"));
        Assert(
            estimated == "CUSTOM_SYSTEM_PROMPT".Length + 4L +
                skillCharacters,
            "custom and skill system prompts both enter context estimate");

        object largeSettings = Activator.CreateInstance(settingsType, true);
        ((IList)GetProperty(largeSettings, "Skills")).Add(CreateSkill(
            skillType,
            "Large enabled skill",
            new string('S', 47500),
            true));
        SetField(formType, form, "extensionSettings", largeSettings);
        long largeSkillCharacters = Convert.ToInt64(InvokePrivateStatic(
            formType,
            "CalculateExtensionPromptCharacterEstimate",
            largeSettings));
        SetField(
            formType,
            form,
            "extensionPromptCharacterEstimate",
            largeSkillCharacters);
        systemPrompt.Text = new string('C', 600);
        string largeSkillPrompt = (string)InvokePublic(
            largeSettings,
            "BuildSystemPrompt");
        string overBudgetCombined = (string)InvokePrivate(
            formType,
            form,
            "BuildCombinedSystemPrompt");
        Assert(
            largeSkillPrompt.Length < 48000 &&
            overBudgetCombined.Length >= 48000,
            "custom prompt can move combined system prompt over budget");
        Assert(
            TryBuildCombinedPrompt(formType, form, largeSkillPrompt),
            "skill prompt alone remains within context budget");
        Assert(
            !TryBuildCombinedPrompt(formType, form, overBudgetCombined),
            "custom prompt is enforced by context budget");
    }

    private static object CreateSkill(
        Type skillType,
        string name,
        string instructions,
        bool enabled)
    {
        object skill = Activator.CreateInstance(skillType, true);
        SetProperty(skill, "Name", name);
        SetProperty(skill, "Description", string.Empty);
        SetProperty(skill, "Instructions", instructions);
        SetProperty(skill, "Enabled", enabled);
        return skill;
    }

    private static bool TryBuildCombinedPrompt(
        Type formType,
        object form,
        string systemPrompt)
    {
        object[] arguments = new object[]
        {
            "x",
            systemPrompt,
            null,
            null,
            false
        };
        return (bool)formType.GetMethod(
            "TryBuildCombinedPrompt",
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(
                form,
                arguments);
    }

    private static void TestGenerationOptionControls(
        Assembly application,
        Type formType,
        object form)
    {
        CheckBox temperatureEnabled = (CheckBox)GetField(
            formType,
            form,
            "temperatureEnabledCheckBox");
        NumericUpDown temperature = (NumericUpDown)GetField(
            formType,
            form,
            "temperatureNumericUpDown");
        CheckBox topPEnabled = (CheckBox)GetField(
            formType,
            form,
            "topPEnabledCheckBox");
        NumericUpDown topP = (NumericUpDown)GetField(
            formType,
            form,
            "topPNumericUpDown");
        CheckBox maxTokensEnabled = (CheckBox)GetField(
            formType,
            form,
            "maxOutputTokensEnabledCheckBox");
        NumericUpDown maxTokens = (NumericUpDown)GetField(
            formType,
            form,
            "maxOutputTokensNumericUpDown");
        Type requestType = application.GetType(
            "FilePromptAIWin7.ModelRequest",
            true);

        temperature.Value = 1.25m;
        topP.Value = 0.55m;
        maxTokens.Value = 8192m;
        temperatureEnabled.Checked = true;
        topPEnabled.Checked = true;
        maxTokensEnabled.Checked = true;
        object enabledRequest = Activator.CreateInstance(requestType, true);
        InvokePrivate(
            formType,
            form,
            "ApplyGenerationOptions",
            enabledRequest);
        Assert(
            (double?)GetProperty(enabledRequest, "Temperature") == 1.25d &&
            (double?)GetProperty(enabledRequest, "TopP") == 0.55d &&
            (int?)GetProperty(enabledRequest, "MaxOutputTokens") == 8192,
            "enabled generation controls populate request values");

        temperatureEnabled.Checked = false;
        topPEnabled.Checked = false;
        maxTokensEnabled.Checked = false;
        object disabledRequest = Activator.CreateInstance(requestType, true);
        SetProperty(disabledRequest, "Temperature", (double?)1d);
        SetProperty(disabledRequest, "TopP", (double?)0.5d);
        SetProperty(disabledRequest, "MaxOutputTokens", (int?)1024);
        InvokePrivate(
            formType,
            form,
            "ApplyGenerationOptions",
            disabledRequest);
        Assert(
            GetProperty(disabledRequest, "Temperature") == null &&
            GetProperty(disabledRequest, "TopP") == null &&
            GetProperty(disabledRequest, "MaxOutputTokens") == null,
            "disabled generation controls clear request values");
    }

    private static void TestModelRequestValidation(Assembly application)
    {
        Type requestType = application.GetType(
            "FilePromptAIWin7.ModelRequest",
            true);
        Type clientType = application.GetType(
            "FilePromptAIWin7.ModelClient",
            true);
        MethodInfo validate = clientType.GetMethod(
            "ValidateRequest",
            BindingFlags.Static | BindingFlags.NonPublic);

        AssertValidOptions(validate, CreateRequest(requestType, 0d, 0d, 1));
        AssertValidOptions(
            validate,
            CreateRequest(requestType, 2d, 1d, 1048576));

        AssertRejectedOption(
            validate,
            CreateRequest(requestType, -0.0001d, null, null),
            "temperature below minimum");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, 2.0001d, null, null),
            "temperature above maximum");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, double.NaN, null, null),
            "temperature NaN");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, double.PositiveInfinity, null, null),
            "temperature positive infinity");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, double.NegativeInfinity, null, null),
            "temperature negative infinity");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, null, -0.0001d, null),
            "top_p below minimum");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, null, 1.0001d, null),
            "top_p above maximum");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, null, double.NaN, null),
            "top_p NaN");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, null, double.PositiveInfinity, null),
            "top_p positive infinity");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, null, double.NegativeInfinity, null),
            "top_p negative infinity");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, null, null, 0),
            "max_tokens below minimum");
        AssertRejectedOption(
            validate,
            CreateRequest(requestType, null, null, 1048577),
            "max_tokens above maximum");
    }

    private static object CreateRequest(
        Type requestType,
        double? temperature,
        double? topP,
        int? maxOutputTokens)
    {
        object request = Activator.CreateInstance(requestType, true);
        SetProperty(request, "EndpointUrl", "http://127.0.0.1/chat");
        SetProperty(request, "ModelName", "validation-model");
        SetProperty(request, "Prompt", "validation prompt");
        SetProperty(request, "Temperature", temperature);
        SetProperty(request, "TopP", topP);
        SetProperty(request, "MaxOutputTokens", maxOutputTokens);
        return request;
    }

    private static void AssertValidOptions(MethodInfo validate, object request)
    {
        validate.Invoke(null, new[] { request });
        Assert(true, "ModelClient accepts inclusive generation boundaries");
    }

    private static void AssertRejectedOption(
        MethodInfo validate,
        object request,
        string name)
    {
        Exception failure = null;
        try
        {
            validate.Invoke(null, new[] { request });
        }
        catch (TargetInvocationException exception)
        {
            failure = exception.InnerException;
        }

        Assert(
            failure != null &&
                failure.GetType().FullName ==
                    "FilePromptAIWin7.ModelCallException",
            "ModelClient rejects " + name);
    }

    private static void ConfigureAssemblyResolution(string applicationPath)
    {
        string applicationDirectory = Path.GetDirectoryName(applicationPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(
            object sender,
            ResolveEventArgs eventArgs)
        {
            string fileName = new AssemblyName(eventArgs.Name).Name + ".dll";
            string candidate = Path.Combine(applicationDirectory, fileName);
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
    }

    private static Exception CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return Unwrap(exception);
        }
    }

    private static Process StartLockHolder(
        string mode,
        string path,
        string ready,
        string release)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = Assembly.GetExecutingAssembly().Location;
        startInfo.Arguments = mode + " " + QuoteArgument(path) + " " +
            QuoteArgument(ready) + " " + QuoteArgument(release);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return Process.Start(startInfo);
    }

    private static void WaitForLockHolder(Process holder, string ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(ready))
        {
            if (holder == null || holder.HasExited)
            {
                throw new InvalidOperationException(
                    "AppSettings lock holder exited before acquiring the lock.");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Timed out waiting for AppSettings file lock.");
            }

            Thread.Sleep(25);
        }
    }

    private static void ReleaseLockHolder(Process holder, string release)
    {
        File.WriteAllText(release, "release", Encoding.ASCII);
        if (holder == null)
        {
            return;
        }

        if (!holder.WaitForExit(5000))
        {
            holder.Kill();
            holder.WaitForExit();
        }

        int exitCode = holder.ExitCode;
        holder.Dispose();
        Assert(exitCode == 0, "AppSettings lock holder exits");
    }

    private static int HoldFileLock(
        string path,
        string ready,
        string release,
        bool exclusive)
    {
        try
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                exclusive ? FileShare.None : FileShare.Read))
            {
                File.WriteAllText(ready, "ready", Encoding.ASCII);
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(release))
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        return 2;
                    }

                    Thread.Sleep(25);
                }
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 3;
        }
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static object GetField(Type type, object instance, string name)
    {
        FieldInfo field = type.GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new MissingFieldException(type.FullName, name);
        }

        return field.GetValue(instance);
    }

    private static void SetField(
        Type type,
        object instance,
        string name,
        object value)
    {
        FieldInfo field = type.GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new MissingFieldException(type.FullName, name);
        }

        field.SetValue(instance, value);
    }

    private static object GetProperty(object instance, string name)
    {
        return instance.GetType().GetProperty(name).GetValue(instance, null);
    }

    private static void SetProperty(object instance, string name, object value)
    {
        instance.GetType().GetProperty(name).SetValue(instance, value, null);
    }

    private static object InvokePublic(
        object instance,
        string name,
        params object[] arguments)
    {
        return instance.GetType().InvokeMember(
            name,
            BindingFlags.InvokeMethod | BindingFlags.Instance |
                BindingFlags.Public,
            null,
            instance,
            arguments);
    }

    private static object InvokeStaticPublic(Type type, string name)
    {
        return type.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
    }

    private static object InvokePrivate(
        Type type,
        object instance,
        string name,
        params object[] arguments)
    {
        return type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(
                instance,
                arguments);
    }

    private static object InvokePrivateStatic(
        Type type,
        string name,
        params object[] arguments)
    {
        return type.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic).Invoke(
                null,
                arguments);
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while ((current is TargetInvocationException ||
            current is AggregateException) && current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " failed.");
        }

        Console.WriteLine("PASS | " + name);
    }
}
