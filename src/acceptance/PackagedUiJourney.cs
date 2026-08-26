using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class PackagedUiJourney
{
    private delegate bool Condition();
    private delegate void FormAction(Form form);
    private delegate T FormReader<T>(Form form);
    private delegate T ValueReader<T>();

    private const string EndpointValue = "http://127.0.0.1:{0}/v1/chat/completions";
    private const string ApiKeyValue = "installed-journey-secret";
    private const string ModelValue = "installed-journey-model";
    private const string FirstPrompt = "第一轮：请回复安装旅程正常";
    private const string SecondPrompt = "第二轮：请结合上一轮回复";
    private const string FirstExportMarker = "FIRST_EXPORT_MARKER";
    private const string SecondExportMarker = "SECOND_EXPORT_MARKER";
    private const string ImageFixtureName = "dragged-image-fixture.png";
    private const string FirstReply =
        "# First export\n\n第一轮安装旅程正常\n\n" +
        FirstExportMarker;
    private const string SecondReply =
        "# Second export\n\n第二轮保留上下文正常\n\n" +
        SecondExportMarker +
        "\n\n| name | value |\n| --- | --- |\n| alpha | 42 |";
    private const string FixtureText = "INSTALLED_PATH_FIXTURE_7A31";
    private static int failures;
    private static string firstFailure;

    public static string Run(string applicationPath, string dataRoot)
    {
        if (IsBlank(applicationPath))
        {
            throw new ArgumentException(
                "A packaged application path is required.",
                "applicationPath");
        }
        if (IsBlank(dataRoot))
        {
            throw new ArgumentException(
                "An isolated data root is required.",
                "dataRoot");
        }

        applicationPath = Path.GetFullPath(applicationPath);
        dataRoot = Path.GetFullPath(dataRoot);
        if (!File.Exists(applicationPath))
        {
            throw new FileNotFoundException(
                "Packaged application was not found.",
                applicationPath);
        }

        failures = 0;
        firstFailure = null;
        Directory.CreateDirectory(dataRoot);
        string previousDataRoot = Environment.GetEnvironmentVariable(
            "FILEPROMPTAI_DATA_ROOT");
        Environment.SetEnvironmentVariable("FILEPROMPTAI_DATA_ROOT", dataRoot);
        ResolveEventHandler resolver = delegate(
            object sender,
            ResolveEventArgs eventArgs)
        {
            string dependency = new AssemblyName(eventArgs.Name).Name + ".dll";
            string candidate = Path.Combine(Path.GetDirectoryName(applicationPath), dependency);
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(
                UnhandledExceptionMode.CatchException);
            try
            {
                RunJourney(applicationPath, dataRoot);
            }
            catch (Exception exception)
            {
                Fail("journey.unhandled", Unwrap(exception).ToString());
            }

            if (failures != 0)
            {
                throw new InvalidOperationException(
                    "Packaged UI journey failed; failures=" + failures +
                    "; first=" + (firstFailure ?? "unknown") + ".");
            }

            return "packaged UI assembly journey, five settings pages, " +
                "loopback API, two-turn context, path text and dragged " +
                "image inputs, 13 export handlers, persistence";
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            Environment.SetEnvironmentVariable(
                "FILEPROMPTAI_DATA_ROOT",
                previousDataRoot);
        }
    }

    private static void RunJourney(string applicationPath, string dataRoot)
    {
        string fixturePath = Path.Combine(dataRoot, "explicit-path-fixture.txt");
        string imageFixturePath = Path.Combine(dataRoot, ImageFixtureName);
        string exportRoot = Path.Combine(dataRoot, "exports");
        string packageRoot = Directory.GetParent(
            Path.GetDirectoryName(applicationPath)).FullName;
        Dictionary<string, string> packageBefore =
            GetPackageSnapshot(packageRoot);
        File.WriteAllText(fixturePath, FixtureText, new UTF8Encoding(false));
        WriteImageFixture(imageFixturePath);
        Directory.CreateDirectory(exportRoot);

        Process realApplication = StartRealPackagedApplication(
            applicationPath,
            Path.Combine(dataRoot, "real-app-check"));
        try
        {
            using (LoopbackServer server = new LoopbackServer())
            {
                Assembly application = Assembly.LoadFrom(applicationPath);
                Type mainFormType = application.GetType(
                    "FilePromptAIWin7.MainForm",
                    true);
                using (UiFormHost first = new UiFormHost(mainFormType))
                {
                Assert(
                    first.Read(delegate(Form form)
                    {
                        return form.Visible && form.IsHandleCreated;
                    }),
                    "first-launch",
                    "packaged MainForm is visible and responsive in Application.Run");
                first.Write(delegate(Form form)
                {
                    VerifyMainWindowLayout(form);
                });

                first.Write(delegate(Form form)
                {
                    ConfigureViaSettingsDialog(form, server.Port);
                });
                Assert(
                    File.Exists(Path.Combine(dataRoot, "settings.xml")),
                    "configuration.saved",
                    "first-run connection values were saved through the settings dialog");

                KeyEventArgs enter = new KeyEventArgs(Keys.Enter);
                first.Write(delegate(Form form)
                {
                    TextBoxBase prompt = GetField<TextBoxBase>(
                        form,
                        "promptTextBox");
                    prompt.Text = FirstPrompt;
                    InvokePromptKeyDown(prompt, enter);
                });
                Assert(enter.SuppressKeyPress, "send.enter-event", "Enter was consumed by the real prompt KeyDown handler");
                WaitUntil(delegate
                {
                    return server.Requests.Count >= 1 &&
                        first.Read(delegate(Form form)
                        {
                            return !IsBusy(form);
                        });
                }, 30000, "first Enter send");
                AssertContains(
                    first.Read(delegate(Form form)
                    {
                        return GetText(form, "outputTextBox");
                    }),
                    FirstExportMarker,
                    "send.enter-result");

                first.Write(delegate(Form form)
                {
                    AddViaPathDialog(form, fixturePath);
                });
                WaitUntil(delegate
                {
                    return first.Read(delegate(Form form)
                    {
                        return !IsBusy(form) && GetInputCount(form) == 1;
                    });
                }, 30000, "explicit path add");
                WaitUntil(delegate
                {
                    return first.Read(delegate(Form form)
                    {
                        return string.IsNullOrEmpty(
                            GetField<TextBox>(form, "pathTextBox").Text);
                    });
                }, 5000, "explicit path dialog completion");
                Assert(
                    first.Read(delegate(Form form)
                    {
                        return GetInputCount(form);
                    }) == 1,
                    "path.item-count",
                    "explicit path added one file through the real handler");
                AssertContains(
                    first.Read(delegate(Form form)
                    {
                        return GetFirstInputText(form);
                    }),
                    FixtureText,
                    "path.extracted-content");
                Assert(
                    first.Read(delegate(Form form)
                    {
                        return string.IsNullOrEmpty(
                            GetField<TextBox>(form, "pathTextBox").Text);
                    }),
                    "path.cleared",
                    "successful explicit path input was cleared");

                first.Write(delegate(Form form)
                {
                    AddViaFileDrop(form, imageFixturePath);
                });
                WaitUntil(delegate
                {
                    return first.Read(delegate(Form form)
                    {
                        return !IsBusy(form) && GetInputCount(form) == 2;
                    });
                }, 30000, "dragged image add");
                Assert(
                    first.Read(delegate(Form form)
                    {
                        return GetInputBinaryLength(form, 1) > 0;
                    }),
                    "drag.image-bytes",
                    "dragged PNG was decoded into non-empty binary attachment bytes");
                AssertEqual(
                    "image/png",
                    first.Read(delegate(Form form)
                    {
                        return GetInputMimeType(form, 1);
                    }),
                    "drag.image-mime");
                string expectedImageData = first.Read(delegate(Form form)
                {
                    return GetInputBinaryBase64(form, 1);
                });

                first.Write(delegate(Form form)
                {
                    GetField<TextBoxBase>(form, "promptTextBox").Text =
                        SecondPrompt;
                    GetField<Button>(form, "generateButton").PerformClick();
                });
                WaitUntil(delegate
                {
                    return server.Requests.Count >= 2 &&
                        first.Read(delegate(Form form)
                        {
                            return !IsBusy(form);
                        });
                }, 30000, "second button send");
                AssertContains(
                    first.Read(delegate(Form form)
                    {
                        return GetText(form, "outputTextBox");
                    }),
                    SecondExportMarker,
                    "send.button-result");

                first.Write(delegate(Form form)
                {
                    ExportConversationFiles(form, exportRoot);
                });
                ValidateExportFiles(exportRoot);

                IList<RequestRecord> requests = server.Requests;
                Assert(requests.Count == 2, "api.request-count", "exactly two real chat requests reached loopback HTTP");
                if (requests.Count >= 2)
                {
                    AssertContains(requests[0].Raw, "Authorization: Bearer " + ApiKeyValue, "api.authorization");
                    AssertContains(requests[0].Body, "\"model\":\"" + ModelValue + "\"", "api.model");
                    AssertContains(requests[0].Body, FirstPrompt, "api.first-prompt");
                    AssertContains(requests[1].Body, FirstPrompt, "context.first-user");
                    AssertContains(
                        requests[1].Body,
                        FirstExportMarker,
                        "context.first-assistant");
                    AssertContains(requests[1].Body, SecondPrompt, "context.second-user");
                    AssertContains(
                        requests[1].Body,
                        FixtureText,
                        "path.api-content");
                    AssertContains(
                        requests[1].Body,
                        "\"type\":\"image_url\"",
                        "drag.api-image-type");
                    AssertContains(
                        requests[1].Body,
                        "\"url\":\"data:image/png;base64," +
                            expectedImageData + "\"",
                        "drag.api-image-data");
                }

                first.Write(delegate(Form form)
                {
                    ClearInputsViaMenu(form);
                });
                Assert(
                    first.Read(delegate(Form form)
                    {
                        return GetInputCount(form) == 0;
                    }),
                    "path.cleared-via-menu",
                    "the real clear-all command removed sent path input before normal close");

                Console.WriteLine("CHECK | close.first.begin");
            }
            Console.WriteLine("CHECK | close.first.end");
            Assert(
                File.Exists(Path.Combine(dataRoot, "settings.xml")),
                "persistence.settings-file",
                "normal UI save and close wrote settings.xml to the isolated data root");
            Assert(
                File.Exists(Path.Combine(dataRoot, "conversations.xml")),
                "persistence.conversation-file",
                "normal chat and close wrote conversations.xml to the isolated data root");

            Console.WriteLine("CHECK | relaunch.construct.begin");
            using (UiFormHost second = new UiFormHost(mainFormType))
            {
                Console.WriteLine("CHECK | relaunch.construct.end");
                Console.WriteLine("CHECK | relaunch.show.begin");
                Console.WriteLine("CHECK | relaunch.show.end");
                AssertEqual(
                    string.Format(EndpointValue, server.Port),
                    second.Read(delegate(Form form)
                    {
                        return GetText(form, "endpointTextBox");
                    }),
                    "relaunch.endpoint");
                AssertEqual(
                    ApiKeyValue,
                    second.Read(delegate(Form form)
                    {
                        return GetText(form, "apiKeyTextBox");
                    }),
                    "relaunch.api-key");
                AssertEqual(
                    ModelValue,
                    second.Read(delegate(Form form)
                    {
                        return GetText(form, "modelTextBox");
                    }),
                    "relaunch.model");
                string transcript = second.Read(delegate(Form form)
                {
                    return GetText(form, "outputTextBox");
                });
                AssertContains(transcript, FirstPrompt, "relaunch.first-prompt");
                AssertContains(
                    transcript,
                    FirstExportMarker,
                    "relaunch.first-reply");
                AssertContains(transcript, SecondPrompt, "relaunch.second-prompt");
                AssertContains(
                    transcript,
                    SecondExportMarker,
                    "relaunch.second-reply");
                Assert(
                    second.Read(delegate(Form form)
                    {
                        return GetMessageCount(form);
                    }) == 4,
                    "relaunch.message-count",
                    "reloaded conversation contains two complete turns");

                second.Write(delegate(Form form)
                {
                    SetField(
                        form,
                        "uninstallCheckOnlyForTests",
                        true);
                    SetField(
                        form,
                        "uninstallSourceProcessIdForTests",
                        realApplication.Id);
                    TriggerUninstallFromSettings(form);
                });
                Assert(
                    second.WaitForStop(15000),
                    "uninstall.window-closed",
                    "the application closed after the real uninstaller completed its read-only app-origin check");
            }

            AssertPackageSnapshotEqual(
                packageBefore,
                GetPackageSnapshot(packageRoot));

            server.ThrowIfFaulted();
        }
        }
        finally
        {
            StopRealPackagedApplication(realApplication);
        }
    }

    private static Process StartRealPackagedApplication(
        string applicationPath,
        string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = applicationPath;
        startInfo.WorkingDirectory = Path.GetDirectoryName(applicationPath);
        startInfo.UseShellExecute = false;
        startInfo.EnvironmentVariables["FILEPROMPTAI_DATA_ROOT"] = dataRoot;
        Process process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException(
                "Windows did not return the real packaged process.");
        }
        try
        {
            WaitUntil(delegate
            {
                process.Refresh();
                return !process.HasExited &&
                    process.MainWindowHandle != IntPtr.Zero;
            }, 30000, "real packaged application window");
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }
            process.Close();
            throw;
        }
        Assert(
            !process.HasExited,
            "real-app.running",
            "a real app\\FilePromptAI.exe process is running for app-origin validation");
        return process;
    }

    private static bool IsBlank(string value)
    {
        return string.IsNullOrEmpty(value) || value.Trim().Length == 0;
    }

    private static void StopRealPackagedApplication(Process process)
    {
        if (process == null)
        {
            return;
        }
        using (process)
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(15000))
                {
                    process.Kill();
                    process.WaitForExit();
                }
            }
            Assert(
                process.HasExited,
                "real-app.stopped",
                "the real packaged validation process exited after the journey");
        }
    }

    private static void TriggerUninstallFromSettings(Form form)
    {
        bool clicked = false;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        timer.Interval = 25;
        timer.Tick += delegate
        {
            Form dialog = GetField<Form>(form, "settingsDialog");
            if (!dialog.Visible)
            {
                return;
            }

            timer.Stop();
            Button[] navigation = (Button[])FindField(
                dialog.GetType(),
                "navigationButtons").GetValue(dialog);
            navigation[4].PerformClick();
            Button uninstall = (Button)dialog.GetType()
                .GetProperty("UninstallButton")
                .GetValue(dialog, null);
            clicked = true;
            uninstall.PerformClick();
        };
        timer.Start();
        try
        {
            GetField<Button>(form, "settingsButton").PerformClick();
        }
        finally
        {
            timer.Stop();
            timer.Dispose();
        }

        Assert(
            clicked,
            "uninstall.settings-command",
            "the real Settings maintenance command launched uninstall");
    }

    private static Dictionary<string, string> GetPackageSnapshot(string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        Dictionary<string, string> snapshot =
            new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(
            fullRoot,
            "*",
            SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(path);
            string relativePath = fullPath.Substring(fullRoot.Length)
                .TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            long length;
            string hash;
            using (FileStream stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 algorithm = SHA256.Create())
            {
                length = stream.Length;
                hash = BitConverter.ToString(
                    algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }

            snapshot.Add(
                relativePath,
                length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) +
                ":" + hash);
        }

        return snapshot;
    }

    private static void AssertPackageSnapshotEqual(
        Dictionary<string, string> before,
        Dictionary<string, string> after)
    {
        Assert(
            before.Count == after.Count,
            "uninstall.package-file-count",
            "the real app-origin uninstaller check did not add or remove package files");
        foreach (KeyValuePair<string, string> expected in before)
        {
            string actual;
            Assert(
                after.TryGetValue(expected.Key, out actual),
                "uninstall.package-file-present",
                "the read-only check preserved package file " + expected.Key);
            if (actual != null)
            {
                AssertEqual(
                    expected.Value,
                    actual,
                    "uninstall.package-file-bytes." + expected.Key);
            }
        }
    }

    private static void ConfigureViaSettingsDialog(Form form, int port)
    {
        bool populated = false;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        timer.Interval = 25;
        timer.Tick += delegate
        {
            Form dialog = GetField<Form>(form, "settingsDialog");
            if (!dialog.Visible)
            {
                return;
            }

            timer.Stop();
            Button[] navigation = GetField<Button[]>(
                dialog,
                "navigationButtons");
            Assert(
                navigation.Length == 5,
                "settings.page-count",
                "settings exposes exactly five navigation pages");
            for (int index = 0; index < navigation.Length; index++)
            {
                navigation[index].PerformClick();
                Application.DoEvents();
                VerifyVisibleControlBounds(
                    dialog,
                    "settings.page-" + index.ToString());
            }
            SetText(form, "endpointTextBox", string.Format(EndpointValue, port));
            SetText(form, "apiKeyTextBox", ApiKeyValue);
            SetText(form, "modelTextBox", ModelValue);
            SetField(form, "sendShortcutMode", "Both");
            object settingsDialog = GetField<object>(form, "settingsDialog");
            PropertyInfo shortcut = settingsDialog.GetType().GetProperty("SendShortcutMode");
            shortcut.SetValue(settingsDialog, "Both", null);
            Button save = (Button)settingsDialog.GetType()
                .GetProperty("SaveButton")
                .GetValue(settingsDialog, null);
            populated = true;
            save.PerformClick();
        };
        timer.Start();
        try
        {
            GetField<Button>(form, "settingsButton").PerformClick();
        }
        finally
        {
            timer.Stop();
            timer.Dispose();
        }
        Assert(populated, "configuration.dialog", "connection values were entered and saved through real settings controls");
        AssertEqual(string.Format(EndpointValue, port), GetText(form, "endpointTextBox"), "configuration.endpoint");
        AssertEqual(ApiKeyValue, GetText(form, "apiKeyTextBox"), "configuration.api-key");
        AssertEqual(ModelValue, GetText(form, "modelTextBox"), "configuration.model");
    }

    private static void VerifyMainWindowLayout(Form form)
    {
        Control root = GetField<Control>(form, "rootLayout");
        Control workspace = GetField<Control>(form, "workspaceLayout");
        Control conversation = GetField<Control>(form, "conversationArea");
        Control sessions = GetField<Control>(form, "sessionListBox");
        Control transcript = GetField<Control>(form, "outputTextBox");
        Control composer = GetField<Control>(form, "composerPanel");
        Control prompt = GetField<Control>(form, "promptTextBox");

        AssertVisibleInside(root, form, "layout.root");
        AssertVisibleInside(workspace, form, "layout.workspace");
        AssertVisibleInside(conversation, form, "layout.conversation");
        AssertVisibleInside(sessions, form, "layout.sessions");
        AssertVisibleInside(transcript, form, "layout.transcript");
        AssertVisibleInside(composer, form, "layout.composer");
        AssertVisibleInside(prompt, form, "layout.prompt");
        AssertNoOverlap(sessions, workspace, "layout.sidebar-workspace");
        AssertNoOverlap(transcript, composer, "layout.transcript-composer");
        Rectangle transcriptBounds = BoundsInForm(transcript, form);
        Rectangle composerBounds = BoundsInForm(composer, form);
        Assert(
            transcriptBounds.Bottom <= composerBounds.Top,
            "layout.transcript-above-composer",
            "the transcript is above the composer");
    }

    private static void VerifyVisibleControlBounds(
        Control root,
        string id)
    {
        List<Control> controls = new List<Control>();
        CollectVisibleControls(root, controls);
        Rectangle client = root.ClientRectangle;
        for (int index = 0; index < controls.Count; index++)
        {
            Control control = controls[index];
            Rectangle bounds = root.RectangleToClient(
                control.Parent.RectangleToScreen(control.Bounds));
            Assert(
                bounds.Width >= 0 && bounds.Height >= 0 &&
                    client.Contains(bounds),
                id + ".bounds." + index.ToString(),
                "visible settings control remains inside the dialog");
        }

        for (int left = 0; left < controls.Count; left++)
        {
            if (!IsInputOrAction(controls[left]))
            {
                continue;
            }
            for (int right = left + 1; right < controls.Count; right++)
            {
                if (!IsInputOrAction(controls[right]) ||
                    controls[left].Parent != controls[right].Parent)
                {
                    continue;
                }
                Rectangle first = controls[left].Bounds;
                Rectangle second = controls[right].Bounds;
                Assert(
                    !first.IntersectsWith(second),
                    id + ".siblings." + left.ToString() + "." +
                        right.ToString(),
                    "sibling settings inputs and actions do not overlap");
            }
        }
    }

    private static void CollectVisibleControls(
        Control root,
        IList<Control> controls)
    {
        foreach (Control control in root.Controls)
        {
            if (!control.Visible)
            {
                continue;
            }
            controls.Add(control);
            CollectVisibleControls(control, controls);
        }
    }

    private static bool IsInputOrAction(Control control)
    {
        return control is ButtonBase || control is TextBoxBase ||
            control is ComboBox || control is NumericUpDown ||
            control is ListControl;
    }

    private static void AssertVisibleInside(
        Control control,
        Form form,
        string id)
    {
        Rectangle bounds = BoundsInForm(control, form);
        Assert(
            control.Visible && bounds.Width > 0 && bounds.Height > 0 &&
                form.ClientRectangle.Contains(bounds),
            id,
            "control is visible and inside the main client area");
    }

    private static void AssertNoOverlap(
        Control first,
        Control second,
        string id)
    {
        Form form = first.FindForm();
        Rectangle firstBounds = BoundsInForm(first, form);
        Rectangle secondBounds = BoundsInForm(second, form);
        Assert(
            !firstBounds.IntersectsWith(secondBounds),
            id,
            "major layout regions do not overlap");
    }

    private static Rectangle BoundsInForm(Control control, Form form)
    {
        return form.RectangleToClient(
            control.Parent.RectangleToScreen(control.Bounds));
    }

    private static void AddViaPathDialog(Form form, string path)
    {
        bool populated = false;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        timer.Interval = 25;
        timer.Tick += delegate
        {
            Form dialog = GetField<Form>(form, "pathInputDialog");
            if (!dialog.Visible)
            {
                return;
            }

            timer.Stop();
            GetField<TextBox>(form, "pathTextBox").Text = path;
            populated = true;
            GetField<Button>(form, "readPathButton").PerformClick();
        };
        timer.Start();
        Button add = GetField<Button>(form, "addFileButton");
        try
        {
            add.PerformClick();
            ToolStripItem pathItem = add.ContextMenuStrip.Items[2];
            pathItem.PerformClick();
        }
        finally
        {
            timer.Stop();
            timer.Dispose();
            if (add.ContextMenuStrip.Visible)
            {
                add.ContextMenuStrip.Close();
            }
        }
        Assert(populated, "path.dialog", "explicit path was entered through the real path dialog");
    }

    private static void AddViaFileDrop(Form form, string path)
    {
        DataObject data = new DataObject();
        data.SetData(DataFormats.FileDrop, new string[] { path });
        DragEventArgs enter = new DragEventArgs(
            data,
            0,
            0,
            0,
            DragDropEffects.Copy,
            DragDropEffects.None);
        MethodInfo dragEnter = form.GetType().GetMethod(
            "OnDragEnter",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new Type[] { typeof(object), typeof(DragEventArgs) },
            null);
        MethodInfo dragDrop = form.GetType().GetMethod(
            "OnDragDrop",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new Type[] { typeof(object), typeof(DragEventArgs) },
            null);
        if (dragEnter == null || dragDrop == null)
        {
            throw new MissingMethodException(
                form.GetType().FullName,
                "OnDragEnter/OnDragDrop");
        }

        Control target = GetField<Control>(form, "fileDropTargetPanel");
        Assert(
            target.AllowDrop,
            "drag.target-registered",
            "the packaged drop target registered with WinForms/OLE");
        dragEnter.Invoke(form, new object[] { target, enter });
        Assert(
            enter.Effect == DragDropEffects.Copy,
            "drag.enter-effect",
            "the packaged drop target accepts an Explorer-style file drop");
        DragEventArgs drop = new DragEventArgs(
            data,
            0,
            0,
            0,
            DragDropEffects.Copy,
            DragDropEffects.Copy);
        dragDrop.Invoke(form, new object[] { target, drop });
    }

    private static void WriteImageFixture(string path)
    {
        using (Bitmap image = new Bitmap(4, 3))
        {
            image.SetPixel(0, 0, Color.FromArgb(0, 120, 212));
            image.SetPixel(1, 0, Color.FromArgb(16, 124, 16));
            image.SetPixel(2, 0, Color.White);
            image.SetPixel(3, 0, Color.Black);
            image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static void ExportConversationFiles(Form form, string exportRoot)
    {
        Button exportButton = GetField<Button>(form, "saveOutputButton");
        ContextMenuStrip exportMenu =
            GetField<ContextMenuStrip>(form, "exportMenu");
        Assert(
            object.ReferenceEquals(exportButton.ContextMenuStrip, exportMenu),
            "export.menu-attached",
            "the packaged export button is attached to the real export menu");

        Queue<string> paths = GetField<Queue<string>>(
            form,
            "exportPathsForTests");
        string[,] exports = new string[,]
        {
            { "最新回复 · Markdown", "latest.md" },
            { "最新回复 · 文本", "latest.txt" },
            { "最新回复 · Word", "latest.docx" },
            { "最新回复 · PDF", "latest.pdf" },
            { "最新回复 · PowerPoint", "latest.pptx" },
            { "最新回复 · XMind 思维导图", "latest.xmind" },
            { "整个会话 · Markdown", "conversation.md" },
            { "整个会话 · Word", "conversation.docx" },
            { "整个会话 · PDF", "conversation.pdf" },
            { "整个会话 · PowerPoint", "conversation.pptx" },
            { "整个会话 · XMind 思维导图", "conversation.xmind" },
            { "表格 · Excel 工作簿", "table.xlsx" },
            { "表格 · CSV", "table.csv" }
        };
        for (int index = 0; index < exports.GetLength(0); index++)
        {
            ToolStripItem item = FindMenuItem(exportMenu, exports[index, 0]);
            Assert(
                item != null && item.Enabled,
                "export.command." + index.ToString(),
                "real export command is present and enabled: " +
                    exports[index, 0]);
            if (item == null || !item.Enabled)
            {
                continue;
            }
            string path = Path.Combine(exportRoot, exports[index, 1]);
            paths.Enqueue(path);
            item.PerformClick();
            Assert(
                paths.Count == 0,
                "export.queue." + index.ToString(),
                "real export handler consumed its isolated test path");
        }
        exportMenu.Close();
    }

    private static ToolStripItem FindMenuItem(
        ContextMenuStrip menu,
        string text)
    {
        foreach (ToolStripItem item in menu.Items)
        {
            if (string.Equals(item.Text, text, StringComparison.Ordinal))
            {
                return item;
            }
        }
        return null;
    }

    private static void ValidateExportFiles(string exportRoot)
    {
        string[] names = new string[]
        {
            "latest.md", "latest.txt", "latest.docx", "latest.pdf",
            "latest.pptx", "latest.xmind", "conversation.md",
            "conversation.docx", "conversation.pdf", "conversation.pptx",
            "conversation.xmind", "table.xlsx", "table.csv"
        };
        for (int index = 0; index < names.Length; index++)
        {
            string path = Path.Combine(exportRoot, names[index]);
            Assert(
                File.Exists(path) && new FileInfo(path).Length > 0,
                "export.file." + index.ToString(),
                "real export exists and is non-empty: " + names[index]);
        }

        AssertTextFile(
            Path.Combine(exportRoot, "latest.md"),
            new string[] { SecondExportMarker, "alpha", "42" },
            "export.latest-md");
        AssertTextFile(
            Path.Combine(exportRoot, "latest.txt"),
            new string[] { SecondExportMarker, "alpha", "42" },
            "export.latest-txt");
        AssertTextFile(
            Path.Combine(exportRoot, "conversation.md"),
            new string[]
            {
                FirstExportMarker,
                SecondExportMarker,
                "alpha",
                "42"
            },
            "export.conversation-md");
        AssertTextFile(
            Path.Combine(exportRoot, "table.csv"),
            new string[] { "alpha", "42" },
            "export.csv");

        AssertZipText(
            Path.Combine(exportRoot, "latest.docx"),
            new string[] { SecondExportMarker, "alpha", "42" },
            "export.latest-docx");
        AssertZipText(
            Path.Combine(exportRoot, "latest.pptx"),
            new string[] { SecondExportMarker, "alpha", "42" },
            "export.latest-pptx");
        AssertZipText(
            Path.Combine(exportRoot, "latest.xmind"),
            new string[] { SecondExportMarker, "alpha", "42" },
            "export.latest-xmind");
        AssertZipText(
            Path.Combine(exportRoot, "conversation.docx"),
            new string[] { FirstExportMarker, SecondExportMarker },
            "export.conversation-docx");
        AssertZipText(
            Path.Combine(exportRoot, "conversation.pptx"),
            new string[] { FirstExportMarker, SecondExportMarker },
            "export.conversation-pptx");
        AssertZipText(
            Path.Combine(exportRoot, "conversation.xmind"),
            new string[] { FirstExportMarker, SecondExportMarker },
            "export.conversation-xmind");
        AssertZipText(
            Path.Combine(exportRoot, "table.xlsx"),
            new string[] { "alpha", "42" },
            "export.xlsx");
        AssertPdf(
            Path.Combine(exportRoot, "latest.pdf"),
            new string[] { SecondExportMarker, "alpha", "42" },
            "export.latest-pdf");
        AssertPdf(
            Path.Combine(exportRoot, "conversation.pdf"),
            new string[] { FirstExportMarker, SecondExportMarker, "alpha", "42" },
            "export.conversation-pdf");
    }

    private static void AssertTextFile(
        string path,
        string[] expected,
        string id)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        for (int index = 0; index < expected.Length; index++)
        {
            AssertContains(text, expected[index], id);
        }
    }

    private static void AssertPdf(string path, string[] expected, string id)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool validSignature =
            bytes.Length > 512 && bytes[0] == 0x25 && bytes[1] == 0x50 &&
                bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D;
        Assert(
            validSignature,
            id,
            "PDF has a %PDF- signature and non-trivial content");
        if (!validSignature)
        {
            return;
        }

        Assembly pdfAssembly = Assembly.Load(
            new AssemblyName("UglyToad.PdfPig"));
        Type documentType = pdfAssembly.GetType(
            "UglyToad.PdfPig.PdfDocument",
            true);
        Type optionsType = pdfAssembly.GetType(
            "UglyToad.PdfPig.ParsingOptions",
            true);
        MethodInfo openMethod = documentType.GetMethod(
            "Open",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new Type[] { typeof(string), optionsType },
            null);
        if (openMethod == null)
        {
            throw new MissingMethodException(
                documentType.FullName,
                "Open(string, ParsingOptions)");
        }

        object document = openMethod.Invoke(
            null,
            new object[] { path, Activator.CreateInstance(optionsType) });
        try
        {
            MethodInfo pagesMethod = null;
            MethodInfo[] methods = documentType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance);
            for (int index = 0; index < methods.Length; index++)
            {
                if (methods[index].Name == "GetPages" &&
                    !methods[index].IsGenericMethodDefinition &&
                    methods[index].GetParameters().Length == 0)
                {
                    pagesMethod = methods[index];
                    break;
                }
            }
            if (pagesMethod == null)
            {
                throw new MissingMethodException(documentType.FullName, "GetPages");
            }

            IEnumerable pages = (IEnumerable)pagesMethod.Invoke(
                document,
                new object[0]);
            StringBuilder text = new StringBuilder();
            int pageCount = 0;
            foreach (object page in pages)
            {
                pageCount++;
                PropertyInfo textProperty = page.GetType().GetProperty(
                    "Text",
                    BindingFlags.Public | BindingFlags.Instance);
                if (textProperty == null)
                {
                    throw new MissingMemberException(
                        page.GetType().FullName,
                        "Text");
                }
                text.Append((string)textProperty.GetValue(page, null));
                text.Append('\n');
            }
            Assert(pageCount > 0, id + ".pages", "PDF contains at least one page");
            string combined = text.ToString();
            for (int index = 0; index < expected.Length; index++)
            {
                AssertContains(combined, expected[index], id + ".content");
            }
        }
        finally
        {
            IDisposable disposable = document as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
        }
    }

    private static void AssertZipText(
        string path,
        string[] expected,
        string id)
    {
        byte[] package = File.ReadAllBytes(path);
        Assert(
            package.Length > 4 && ReadUInt32(package, 0) == 0x04034B50U,
            id + ".signature",
            "package has a ZIP local-header signature");
        if (package.Length <= 4)
        {
            return;
        }

        int end = FindZipEndRecord(package);
        Assert(end >= 0, id + ".end-record", "ZIP end record is present");
        if (end < 0)
        {
            return;
        }
        int entryCount = ReadUInt16(package, end + 10);
        int cursor = (int)ReadUInt32(package, end + 16);
        StringBuilder text = new StringBuilder();
        for (int index = 0; index < entryCount; index++)
        {
            if (cursor < 0 || cursor > package.Length - 46 ||
                ReadUInt32(package, cursor) != 0x02014B50U)
            {
                Assert(false, id + ".central", "ZIP central directory is valid");
                return;
            }
            int flags = ReadUInt16(package, cursor + 8);
            int method = ReadUInt16(package, cursor + 10);
            int compressedSize = (int)ReadUInt32(package, cursor + 20);
            int uncompressedSize = (int)ReadUInt32(package, cursor + 24);
            int nameLength = ReadUInt16(package, cursor + 28);
            int extraLength = ReadUInt16(package, cursor + 30);
            int commentLength = ReadUInt16(package, cursor + 32);
            int localOffset = (int)ReadUInt32(package, cursor + 42);
            int recordLength = 46 + nameLength + extraLength + commentLength;
            if ((flags & 1) != 0 || (method != 0 && method != 8) ||
                compressedSize < 0 || uncompressedSize < 0 ||
                recordLength < 46 || cursor > package.Length - recordLength ||
                localOffset < 0 || localOffset > package.Length - 30 ||
                ReadUInt32(package, localOffset) != 0x04034B50U)
            {
                Assert(false, id + ".entry", "ZIP entry is supported and bounded");
                return;
            }

            string name = Encoding.UTF8.GetString(
                package,
                cursor + 46,
                nameLength);
            int localNameLength = ReadUInt16(package, localOffset + 26);
            int localExtraLength = ReadUInt16(package, localOffset + 28);
            int dataOffset = localOffset + 30 + localNameLength +
                localExtraLength;
            if (dataOffset < 0 || compressedSize > package.Length - dataOffset)
            {
                Assert(false, id + ".data", "ZIP entry data is bounded");
                return;
            }

            if (IsTextArchiveEntry(name))
            {
                byte[] content = InflateZipEntry(
                    package,
                    dataOffset,
                    compressedSize,
                    uncompressedSize,
                    method);
                text.Append(Encoding.UTF8.GetString(content));
                text.Append('\n');
            }
            cursor += recordLength;
        }

        string combined = text.ToString();
        for (int index = 0; index < expected.Length; index++)
        {
            AssertContains(combined, expected[index], id + ".content");
        }
    }

    private static bool IsTextArchiveEntry(string name)
    {
        return name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] InflateZipEntry(
        byte[] package,
        int offset,
        int compressedSize,
        int expectedSize,
        int method)
    {
        byte[] compressed = new byte[compressedSize];
        Buffer.BlockCopy(package, offset, compressed, 0, compressedSize);
        if (method == 0)
        {
            return compressed;
        }
        using (MemoryStream input = new MemoryStream(compressed, false))
        using (DeflateStream inflater = new DeflateStream(
            input,
            CompressionMode.Decompress))
        using (MemoryStream output = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, count);
                if (output.Length > 16 * 1024 * 1024)
                {
                    throw new InvalidDataException(
                        "Export archive text entry exceeds its test limit.");
                }
            }
            byte[] content = output.ToArray();
            if (expectedSize >= 0 && content.Length != expectedSize)
            {
                throw new InvalidDataException(
                    "Export archive entry size does not match its directory record.");
            }
            return content;
        }
    }

    private static int FindZipEndRecord(byte[] bytes)
    {
        int minimum = Math.Max(0, bytes.Length - 65557);
        for (int index = bytes.Length - 22; index >= minimum; index--)
        {
            if (ReadUInt32(bytes, index) == 0x06054B50U &&
                index + 22 + ReadUInt16(bytes, index + 20) == bytes.Length)
            {
                return index;
            }
        }
        return -1;
    }

    private static int ReadUInt16(byte[] bytes, int offset)
    {
        return bytes[offset] | (bytes[offset + 1] << 8);
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return (uint)(bytes[offset] | (bytes[offset + 1] << 8) |
            (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }

    private static void ClearInputsViaMenu(Form form)
    {
        ListView inputList = GetField<ListView>(form, "inputListView");
        ContextMenuStrip menu = inputList.ContextMenuStrip;
        ToolStripItem clearItem = null;
        foreach (ToolStripItem item in menu.Items)
        {
            if (string.Equals(
                item.Text,
                "清空全部资料",
                StringComparison.Ordinal))
            {
                clearItem = item;
                break;
            }
        }

        if (clearItem == null)
        {
            throw new InvalidOperationException(
                "The packaged input clear command was not found.");
        }
        clearItem.PerformClick();
    }

    private static void InvokePromptKeyDown(Control prompt, KeyEventArgs args)
    {
        MethodInfo method = typeof(Control).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(prompt, new object[] { args });
    }

    private static bool IsBusy(object form)
    {
        return (bool)GetProperty(form, "IsBusy");
    }

    private static int GetInputCount(object form)
    {
        ICollection items = (ICollection)GetField<object>(form, "inputItems");
        return items.Count;
    }

    private static string GetFirstInputText(object form)
    {
        IList items = (IList)GetField<object>(form, "inputItems");
        return items.Count == 0 ? string.Empty : (string)GetProperty(items[0], "TextContent");
    }

    private static int GetInputBinaryLength(object form, int index)
    {
        IList items = (IList)GetField<object>(form, "inputItems");
        if (index < 0 || index >= items.Count)
        {
            return 0;
        }
        byte[] bytes = (byte[])GetProperty(items[index], "BinaryData");
        return bytes == null ? 0 : bytes.Length;
    }

    private static string GetInputMimeType(object form, int index)
    {
        IList items = (IList)GetField<object>(form, "inputItems");
        return index < 0 || index >= items.Count
            ? string.Empty
            : (string)GetProperty(items[index], "MimeType");
    }

    private static string GetInputBinaryBase64(object form, int index)
    {
        IList items = (IList)GetField<object>(form, "inputItems");
        if (index < 0 || index >= items.Count)
        {
            return string.Empty;
        }
        byte[] bytes = (byte[])GetProperty(items[index], "BinaryData");
        return bytes == null ? string.Empty : Convert.ToBase64String(bytes);
    }

    private static int GetMessageCount(object form)
    {
        object store = GetField<object>(form, "conversationStore");
        object session = GetProperty(store, "CurrentSession");
        ICollection messages = (ICollection)GetProperty(session, "Messages");
        return messages.Count;
    }

    private static string GetText(object form, string fieldName)
    {
        return GetField<Control>(form, fieldName).Text;
    }

    private static void SetText(object form, string fieldName, string value)
    {
        GetField<Control>(form, fieldName).Text = value;
    }

    private static T GetField<T>(object target, string name)
    {
        FieldInfo field = FindField(target.GetType(), name);
        if (field == null)
        {
            throw new MissingFieldException(target.GetType().FullName, name);
        }

        return (T)field.GetValue(target);
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = FindField(target.GetType(), name);
        if (field == null)
        {
            throw new MissingFieldException(target.GetType().FullName, name);
        }

        field.SetValue(target, value);
    }

    private static FieldInfo FindField(Type type, string name)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
            {
                return field;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static object GetProperty(object target, string name)
    {
        PropertyInfo property = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (property == null)
        {
            throw new MissingMemberException(target.GetType().FullName, name);
        }

        return property.GetValue(target, null);
    }

    private static void WaitUntil(
        Condition condition,
        int timeoutMilliseconds,
        string operation)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(15);
        }

        throw new TimeoutException(operation + " timed out after " + timeoutMilliseconds + " ms.");
    }

    private static void AssertContains(string actual, string expected, string id)
    {
        Assert(actual != null && actual.IndexOf(expected, StringComparison.Ordinal) >= 0, id, "contains expected text: " + expected);
    }

    private static void AssertEqual(string expected, string actual, string id)
    {
        Assert(string.Equals(expected, actual, StringComparison.Ordinal), id, "expected=" + expected + "; actual=" + actual);
    }

    private static void Assert(bool passed, string id, string detail)
    {
        if (passed)
        {
            Console.WriteLine("PASS | " + id + " | " + detail);
            return;
        }

        failures++;
        if (firstFailure == null)
        {
            firstFailure = id + ": " + detail;
        }
        Console.Error.WriteLine("FAIL | " + id + " | " + detail);
    }

    private static void Fail(string id, string detail)
    {
        failures++;
        if (firstFailure == null)
        {
            firstFailure = id + ": " + detail;
        }
        Console.Error.WriteLine("FAIL | " + id + " | " + detail);
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException && exception.InnerException != null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }

    private sealed class RequestRecord
    {
        public string Raw { get; set; }
        public string Body { get; set; }
    }

    private sealed class UiFormHost : IDisposable
    {
        private readonly Type formType;
        private readonly ManualResetEvent ready;
        private readonly ManualResetEvent stopped;
        private readonly Thread thread;
        private Form form;
        private Exception fault;
        private bool disposed;

        public UiFormHost(Type type)
        {
            formType = type;
            ready = new ManualResetEvent(false);
            stopped = new ManualResetEvent(false);
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "InstalledUserJourneyUI";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.WaitOne(10000))
            {
                throw new TimeoutException(
                    "Packaged MainForm did not start its UI loop within 10 seconds.");
            }

            ThrowIfFaulted();
        }

        private void Run()
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new WindowsFormsSynchronizationContext());
                form = (Form)Activator.CreateInstance(formType, true);
                form.Shown += delegate { ready.Set(); };
                Application.Run(form);
            }
            catch (Exception exception)
            {
                fault = Unwrap(exception);
                ready.Set();
            }
            finally
            {
                stopped.Set();
            }
        }

        public void Write(FormAction action)
        {
            Read<object>(delegate(Form current)
            {
                action(current);
                return null;
            });
        }

        public T Read<T>(FormReader<T> action)
        {
            ThrowIfFaulted();
            Form current = form;
            if (current == null || current.IsDisposed)
            {
                throw new InvalidOperationException(
                    "The packaged MainForm is not available.");
            }

            object result = current.Invoke(new ValueReader<T>(delegate
            {
                return action(current);
            }));
            ThrowIfFaulted();
            return (T)result;
        }

        public bool WaitForStop(int timeoutMilliseconds)
        {
            return stopped.WaitOne(timeoutMilliseconds);
        }

        private void ThrowIfFaulted()
        {
            if (fault != null)
            {
                throw new InvalidOperationException(
                    "The packaged UI thread failed.",
                    fault);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Form current = form;
            if (current != null && !current.IsDisposed)
            {
                current.BeginInvoke(new MethodInvoker(delegate
                {
                    FieldInfo confirmation = FindField(
                        current.GetType(),
                        "exitConfirmationGranted");
                    if (confirmation != null)
                    {
                        confirmation.SetValue(current, true);
                    }
                    current.Close();
                }));
            }

            if (!stopped.WaitOne(15000))
            {
                Fail(
                    "journey.cleanup-close",
                    "Packaged MainForm did not close within 15 seconds.");
                return;
            }

            if (fault != null)
            {
                Fail(
                    "journey.cleanup-thread",
                    "The packaged UI thread failed during cleanup: " +
                        fault);
            }
            ready.Close();
            stopped.Close();
        }
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread worker;
        private readonly List<RequestRecord> requests;
        private readonly object sync;
        private Exception fault;
        private bool disposed;

        public LoopbackServer()
        {
            sync = new object();
            requests = new List<RequestRecord>();
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "InstalledUserJourneyLoopback";
            worker.Start();
        }

        public int Port
        {
            get { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        }

        public IList<RequestRecord> Requests
        {
            get
            {
                lock (sync)
                {
                    return new List<RequestRecord>(requests);
                }
            }
        }

        private void Run()
        {
            try
            {
                for (int index = 0; index < 2; index++)
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        stream.ReadTimeout = 15000;
                        stream.WriteTimeout = 15000;
                        RequestRecord request = ReadRequest(stream);
                        lock (sync)
                        {
                            requests.Add(request);
                        }

                        string reply = index == 0 ? FirstReply : SecondReply;
                        string body =
                            "data: {\"choices\":[{\"delta\":{\"content\":\"" + JsonEscape(reply) + "\"}}]}\r\n\r\n" +
                            "data: [DONE]\r\n\r\n";
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        string headers =
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: text/event-stream; charset=utf-8\r\n" +
                            "Content-Length: " + bytes.Length + "\r\n" +
                            "Connection: close\r\n\r\n";
                        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                        stream.Write(headerBytes, 0, headerBytes.Length);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                }
            }
            catch (Exception exception)
            {
                if (!disposed)
                {
                    fault = exception;
                }
            }
        }

        private static RequestRecord ReadRequest(NetworkStream stream)
        {
            MemoryStream bytes = new MemoryStream();
            byte[] buffer = new byte[4096];
            int headerEnd = -1;
            int contentLength = 0;
            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    throw new EndOfStreamException("HTTP request ended before headers completed.");
                }

                bytes.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(bytes.GetBuffer(), (int)bytes.Length);
            }

            string headers = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, headerEnd);
            foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring(15).Trim(), out contentLength);
                }
            }

            int bodyStart = headerEnd + 4;
            while (bytes.Length - bodyStart < contentLength)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                bytes.Write(buffer, 0, read);
            }

            string raw = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            string body = Encoding.UTF8.GetString(bytes.GetBuffer(), bodyStart, Math.Min(contentLength, (int)bytes.Length - bodyStart));
            return new RequestRecord { Raw = raw, Body = body };
        }

        private static int FindHeaderEnd(byte[] bytes, int length)
        {
            for (int index = 0; index <= length - 4; index++)
            {
                if (bytes[index] == 13 && bytes[index + 1] == 10 && bytes[index + 2] == 13 && bytes[index + 3] == 10)
                {
                    return index;
                }
            }

            return -1;
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        public void ThrowIfFaulted()
        {
            if (!worker.Join(5000))
            {
                throw new TimeoutException("Loopback server did not finish two requests.");
            }

            if (fault != null)
            {
                throw new InvalidOperationException("Loopback server failed.", fault);
            }
        }

        public void Dispose()
        {
            disposed = true;
            listener.Stop();
            worker.Join(1000);
        }
    }
}
