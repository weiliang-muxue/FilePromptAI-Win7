using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class InstalledUserJourneySmokeTest
{
    private const string EndpointValue = "http://127.0.0.1:{0}/v1/chat/completions";
    private const string ApiKeyValue = "installed-journey-secret";
    private const string ModelValue = "installed-journey-model";
    private const string FirstPrompt = "第一轮：请回复安装旅程正常";
    private const string SecondPrompt = "第二轮：请结合上一轮回复";
    private const string FirstReply = "第一轮安装旅程正常";
    private const string SecondReply = "第二轮保留上下文正常";
    private const string FixtureText = "INSTALLED_PATH_FIXTURE_7A31";
    private static int failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                "Usage: InstalledUserJourneySmokeTest.exe <packaged-app-exe> <isolated-data-root>");
            return 2;
        }

        string applicationPath = Path.GetFullPath(args[0]);
        string dataRoot = Path.GetFullPath(args[1]);
        if (!File.Exists(applicationPath))
        {
            Console.Error.WriteLine("Packaged application was not found: " + applicationPath);
            return 2;
        }

        Directory.CreateDirectory(dataRoot);
        Environment.SetEnvironmentVariable("FILEPROMPTAI_DATA_ROOT", dataRoot);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            string dependency = new AssemblyName(eventArgs.Name).Name + ".dll";
            string candidate = Path.Combine(Path.GetDirectoryName(applicationPath), dependency);
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

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
            Console.Error.WriteLine("FAIL | installed user journey | failures=" + failures);
            return 1;
        }

        Console.WriteLine("PASS | installed user journey | real packaged UI, loopback API, context, path, export menu, persistence");
        return 0;
    }

    private static void RunJourney(string applicationPath, string dataRoot)
    {
        string fixturePath = Path.Combine(dataRoot, "explicit-path-fixture.txt");
        string packageRoot = Directory.GetParent(
            Path.GetDirectoryName(applicationPath)).FullName;
        Dictionary<string, string> packageBefore =
            GetPackageSnapshot(packageRoot);
        File.WriteAllText(fixturePath, FixtureText, new UTF8Encoding(false));

        using (LoopbackServer server = new LoopbackServer())
        {
            Assembly application = Assembly.LoadFrom(applicationPath);
            Type mainFormType = application.GetType("FilePromptAIWin7.MainForm", true);
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
                    FirstReply,
                    "send.enter-result");

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
                    SecondReply,
                    "send.button-result");

                first.Write(delegate(Form form)
                {
                    VerifyExportMenuWiring(form);
                });

                IList<RequestRecord> requests = server.Requests;
                Assert(requests.Count == 2, "api.request-count", "exactly two real chat requests reached loopback HTTP");
                if (requests.Count >= 2)
                {
                    AssertContains(requests[0].Raw, "Authorization: Bearer " + ApiKeyValue, "api.authorization");
                    AssertContains(requests[0].Body, "\"model\":\"" + ModelValue + "\"", "api.model");
                    AssertContains(requests[0].Body, FirstPrompt, "api.first-prompt");
                    AssertContains(requests[1].Body, FirstPrompt, "context.first-user");
                    AssertContains(requests[1].Body, FirstReply, "context.first-assistant");
                    AssertContains(requests[1].Body, SecondPrompt, "context.second-user");
                }

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
                    ClearInputsViaMenu(form);
                });
                Assert(
                    first.Read(delegate(Form form)
                    {
                        return GetInputCount(form) == 0;
                    }),
                    "path.cleared-via-menu",
                    "the real clear-all command removed unsent path input before normal close");

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
                AssertContains(transcript, FirstReply, "relaunch.first-reply");
                AssertContains(transcript, SecondPrompt, "relaunch.second-prompt");
                AssertContains(transcript, SecondReply, "relaunch.second-reply");
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

    private static void VerifyExportMenuWiring(Form form)
    {
        Button exportButton = GetField<Button>(form, "saveOutputButton");
        ContextMenuStrip exportMenu =
            GetField<ContextMenuStrip>(form, "exportMenu");
        Assert(
            object.ReferenceEquals(exportButton.ContextMenuStrip, exportMenu),
            "export.menu-attached",
            "the packaged export button is attached to the real export menu");

        exportButton.PerformClick();
        Assert(
            exportMenu.Visible,
            "export.menu-opened",
            "clicking the packaged export button opens its menu");

        ToolStripItem markdownItem = null;
        foreach (ToolStripItem item in exportMenu.Items)
        {
            if (string.Equals(
                item.Text,
                "最新回复 · Markdown",
                StringComparison.Ordinal))
            {
                markdownItem = item;
                break;
            }
        }

        Assert(
            markdownItem != null && markdownItem.Enabled,
            "export.markdown-command",
            "the latest-reply Markdown export command is present and enabled");
        exportMenu.Close();
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

    private static void WaitUntil(Func<bool> condition, int timeoutMilliseconds, string operation)
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
        Console.Error.WriteLine("FAIL | " + id + " | " + detail);
    }

    private static void Fail(string id, string detail)
    {
        failures++;
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

        public void Write(Action<Form> action)
        {
            Read<object>(delegate(Form current)
            {
                action(current);
                return null;
            });
        }

        public T Read<T>(Func<Form, T> action)
        {
            ThrowIfFaulted();
            Form current = form;
            if (current == null || current.IsDisposed)
            {
                throw new InvalidOperationException(
                    "The packaged MainForm is not available.");
            }

            object result = current.Invoke(new Func<T>(delegate
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
                    current.Close();
                }));
            }

            if (!stopped.WaitOne(15000))
            {
                throw new TimeoutException(
                    "Packaged MainForm did not close within 15 seconds.");
            }

            ThrowIfFaulted();
            ready.Dispose();
            stopped.Dispose();
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
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
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
