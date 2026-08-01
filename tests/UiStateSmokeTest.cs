using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class UiStateSmokeTest
{
    [STAThread]
    private static int Main(string[] args)
    {
        string dataRoot = Path.Combine(
            Path.GetTempPath(),
            "FilePromptUiState-" + Guid.NewGuid().ToString("N"));
        string previousRoot = Environment.GetEnvironmentVariable(
            "FILEPROMPT_DATA_ROOT");
        object form = null;
        try
        {
            if (args.Length != 1)
            {
                throw new ArgumentException(
                    "Usage: UiStateSmokeTest <FilePrompt.exe>");
            }

            string applicationPath = Path.GetFullPath(args[0]);
            ConfigureAssemblyResolution(applicationPath);
            Environment.SetEnvironmentVariable(
                "FILEPROMPT_DATA_ROOT",
                dataRoot);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Assembly application = Assembly.LoadFrom(applicationPath);
            Type formType = application.GetType(
                "FilePromptWin7.MainForm",
                true);
            form = Activator.CreateInstance(formType, true);

            TestCtrlNBusyGuard(formType, form);
            TestDragBusyGuard(formType, form, dataRoot);
            TestSearchCharacterBudget(application, formType, form);
            Console.WriteLine("PASS | UI state hardening");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | UI state hardening");
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
                "FILEPROMPT_DATA_ROOT",
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
                // Temporary state can be removed on the next run.
            }
        }
    }

    private static void TestCtrlNBusyGuard(Type formType, object form)
    {
        object store = GetField(formType, form, "conversationStore");
        int before = ((IList)store.GetType().GetProperty("Sessions")
            .GetValue(store, null)).Count;
        SetField(formType, form, "isAddingFiles", true);
        try
        {
            KeyEventArgs keys = new KeyEventArgs(Keys.Control | Keys.N);
            InvokePrivate(formType, form, "OnMainKeyDown", form, keys);
            int after = ((IList)store.GetType().GetProperty("Sessions")
                .GetValue(store, null)).Count;
            AssertTrue(before == after, "Ctrl+N is blocked during extraction");
        }
        finally
        {
            SetField(formType, form, "isAddingFiles", false);
        }
    }

    private static void TestDragBusyGuard(
        Type formType,
        object form,
        string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        string file = Path.Combine(dataRoot, "drag.txt");
        File.WriteAllText(file, "drag content");
        CancellationTokenSource cancellation = new CancellationTokenSource();
        SetField(
            formType,
            form,
            "generationCancellation",
            cancellation);
        try
        {
            DataObject data = new DataObject();
            data.SetData(DataFormats.FileDrop, new string[] { file });
            DragEventArgs drag = new DragEventArgs(
                data,
                0,
                0,
                0,
                DragDropEffects.Copy,
                DragDropEffects.None);
            InvokePrivate(formType, form, "OnDragEnter", form, drag);
            AssertTrue(
                drag.Effect == DragDropEffects.None,
                "File drop is blocked during generation");
        }
        finally
        {
            SetField(formType, form, "generationCancellation", null);
            cancellation.Dispose();
        }
    }

    private static void TestSearchCharacterBudget(
        Assembly application,
        Type formType,
        object form)
    {
        Type sessionType = application.GetType(
            "FilePromptWin7.ConversationSession",
            true);
        Type messageType = application.GetType(
            "FilePromptWin7.ConversationMessage",
            true);
        object session = Activator.CreateInstance(sessionType, true);
        object message = Activator.CreateInstance(messageType, true);
        string content = new string('a', 25000) + "needle";
        messageType.GetProperty("Role").SetValue(message, "assistant", null);
        messageType.GetProperty("Content").SetValue(message, content, null);
        IList messages = (IList)sessionType.GetProperty("Messages")
            .GetValue(session, null);
        messages.Add(message);

        bool hiddenTail = (bool)InvokePrivate(
            formType,
            form,
            "SessionMatchesFilter",
            session,
            "needle");
        bool visibleHead = (bool)InvokePrivate(
            formType,
            form,
            "SessionMatchesFilter",
            session,
            "aaaaa");
        AssertTrue(!hiddenTail, "Search does not scan beyond its budget");
        AssertTrue(visibleHead, "Search still scans recent content");
    }

    private static object GetField(
        Type type,
        object instance,
        string name)
    {
        return type.GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(instance);
    }

    private static void SetField(
        Type type,
        object instance,
        string name,
        object value)
    {
        type.GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(instance, value);
    }

    private static object InvokePrivate(
        Type type,
        object instance,
        string name,
        params object[] arguments)
    {
        return type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Invoke(instance, arguments);
    }

    private static void ConfigureAssemblyResolution(string applicationPath)
    {
        string directory = Path.GetDirectoryName(applicationPath);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(
            object sender,
            ResolveEventArgs args)
        {
            string candidate = Path.Combine(
                directory,
                new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(candidate)
                ? Assembly.LoadFrom(candidate)
                : null;
        };
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception current = exception;
        while (current is TargetInvocationException &&
            current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + " failed.");
        }

        Console.WriteLine("PASS | " + name);
    }
}
