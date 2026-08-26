using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class UninstallerUserDataSmokeTest
{
    private const string DataRootVariable = "FILEPROMPTAI_DATA_ROOT";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(
                "Usage: UninstallerUserDataSmokeTest <Uninstall-FilePromptAI.exe>");
            return 2;
        }

        string previousDataRoot = Environment.GetEnvironmentVariable(
            DataRootVariable,
            EnvironmentVariableTarget.Process);
        string runRoot = Path.Combine(
            Path.GetTempPath(),
            "FilePromptAI-UninstallerDataTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            string uninstallerPath = Path.GetFullPath(args[0]);
            Assembly assembly = Assembly.LoadFrom(uninstallerPath);
            Type programType = assembly.GetType(
                "FilePromptAIUninstaller.Program",
                true);
            BindingFlags staticFlags = BindingFlags.Static |
                BindingFlags.NonPublic;
            MethodInfo getDefaultRoot = RequireMethod(
                programType,
                "GetDefaultDataRoot",
                staticFlags);
            MethodInfo canDelete = RequireMethod(
                programType,
                "CanDeleteDefaultUserData",
                staticFlags);
            MethodInfo deleteTree = RequireMethod(
                programType,
                "TryDeleteUserDataTree",
                staticFlags);
            MethodInfo runWorkerCore = RequireMethod(
                programType,
                "RunWorkerCore",
                staticFlags);

            Directory.CreateDirectory(runRoot);
            string defaultRoot = (string)getDefaultRoot.Invoke(null, null);
            TestDataRootPolicy(
                programType,
                canDelete,
                defaultRoot,
                runRoot);
            TestWorkerGuard(runWorkerCore, runRoot);
            TestLockedFileIsZeroDeletion(deleteTree, runRoot);
            TestReparsePointIsZeroDeletion(deleteTree, runRoot);

            Console.WriteLine(
                "PASS | user-data uninstall policy and two-phase deletion are safe");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(Unwrap(exception));
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DataRootVariable,
                previousDataRoot,
                EnvironmentVariableTarget.Process);
            TryDeleteOwnedTestRoot(runRoot);
        }
    }

    private static void TestDataRootPolicy(
        Type programType,
        MethodInfo canDelete,
        string defaultRoot,
        string runRoot)
    {
        AssertPolicy(canDelete, null, true, "unset override");
        AssertPolicy(canDelete, "   ", true, "blank override");
        AssertPolicy(canDelete, defaultRoot, true, "default override");
        AssertPolicy(
            canDelete,
            Path.Combine(defaultRoot, "."),
            true,
            "normalized default override");

        string customRoot = Path.Combine(runRoot, "custom-data");
        AssertPolicy(canDelete, customRoot, false, "custom override");
        AssertCustomRootDisablesUi(programType, customRoot, runRoot);
    }

    private static void AssertPolicy(
        MethodInfo method,
        string configuredRoot,
        bool expected,
        string scenario)
    {
        Environment.SetEnvironmentVariable(
            DataRootVariable,
            configuredRoot,
            EnvironmentVariableTarget.Process);
        object[] arguments = new object[] { string.Empty };
        bool actual = (bool)method.Invoke(null, arguments);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                scenario + " returned an unexpected deletion policy.");
        }

        string message = (string)arguments[0];
        if (!expected &&
            (string.IsNullOrEmpty(message) ||
                message.IndexOf("自定义", StringComparison.Ordinal) < 0 ||
                message.IndexOf("保留", StringComparison.Ordinal) < 0))
        {
            throw new InvalidOperationException(
                scenario + " did not return a clear preservation message.");
        }
    }

    private static void AssertCustomRootDisablesUi(
        Type programType,
        string customRoot,
        string runRoot)
    {
        Environment.SetEnvironmentVariable(
            DataRootVariable,
            customRoot,
            EnvironmentVariableTarget.Process);
        Type formType = programType.GetNestedType(
            "UninstallForm",
            BindingFlags.NonPublic);
        if (formType == null)
        {
            throw new InvalidOperationException(
                "The private uninstall form type was not found.");
        }

        ConstructorInfo constructor = formType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic,
            null,
            new Type[] { typeof(string) },
            null);
        if (constructor == null)
        {
            throw new InvalidOperationException(
                "The private uninstall form constructor was not found.");
        }

        object formObject = constructor.Invoke(new object[] { runRoot });
        using (Form form = (Form)formObject)
        {
            FieldInfo field = formType.GetField(
                "deleteDataCheckBox",
                BindingFlags.Instance | BindingFlags.NonPublic);
            CheckBox checkBox = field == null
                ? null
                : field.GetValue(form) as CheckBox;
            PropertyInfo deleteData = formType.GetProperty(
                "DeleteData",
                BindingFlags.Instance | BindingFlags.Public);
            if (checkBox == null ||
                deleteData == null ||
                checkBox.Enabled ||
                checkBox.Checked ||
                (bool)deleteData.GetValue(form, null) ||
                checkBox.Text.IndexOf("保留", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "A custom data root did not disable data deletion in the UI.");
            }
        }
    }

    private static void TestWorkerGuard(
        MethodInfo runWorkerCore,
        string runRoot)
    {
        string packageRoot = Path.Combine(runRoot, "worker-package");
        string customRoot = Path.Combine(runRoot, "worker-custom-data");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(customRoot);
        string sentinel = Path.Combine(packageRoot, "must-remain.txt");
        File.WriteAllText(sentinel, "worker guard", Encoding.UTF8);
        Environment.SetEnvironmentVariable(
            DataRootVariable,
            customRoot,
            EnvironmentVariableTarget.Process);
        Environment.ExitCode = 0;
        string[] workerArguments = new string[]
        {
            "--execute",
            packageRoot,
            "--delete-data",
            "true",
            "--parent-pid",
            "2147483647",
            "--parent-start-ticks",
            "1",
            "--app-pid",
            "0",
            "--app-start-ticks",
            "0"
        };
        runWorkerCore.Invoke(null, new object[] { workerArguments, true });
        if (Environment.ExitCode != 4 ||
            !File.Exists(sentinel) ||
            !Directory.Exists(customRoot))
        {
            throw new InvalidOperationException(
                "The worker did not reject custom-root data deletion before changing files.");
        }
    }

    private static void TestLockedFileIsZeroDeletion(
        MethodInfo deleteTree,
        string runRoot)
    {
        string dataRoot = Path.Combine(
            Path.Combine(runRoot, "locked"),
            "FilePromptAI-Win7");
        Directory.CreateDirectory(dataRoot);
        string first = Path.Combine(dataRoot, "a-first.txt");
        string lockedPath = Path.Combine(dataRoot, "z-locked.txt");
        byte[] firstBytes = Encoding.UTF8.GetBytes("ordinary file must remain");
        byte[] lockedBytes = Encoding.UTF8.GetBytes("locked file must remain");
        File.WriteAllBytes(first, firstBytes);
        File.WriteAllBytes(lockedPath, lockedBytes);

        using (FileStream locked = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            string error;
            bool alreadyMissing;
            if (InvokeDeleteTree(
                deleteTree,
                dataRoot,
                out alreadyMissing,
                out error))
            {
                throw new InvalidOperationException(
                    "A locked user-data file unexpectedly passed preflight.");
            }

            if (alreadyMissing || string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException(
                    "Locked-file rejection did not report its preflight failure.");
            }

            AssertFileBytes(first, firstBytes, "ordinary file after locked preflight");
            if (!File.Exists(lockedPath) || locked.Length != lockedBytes.Length)
            {
                throw new InvalidOperationException(
                    "The occupied file changed during locked preflight.");
            }
        }

        AssertFileBytes(
            lockedPath,
            lockedBytes,
            "locked file after locked preflight");

        string retryError;
        bool retryMissing;
        if (!InvokeDeleteTree(
            deleteTree,
            dataRoot,
            out retryMissing,
            out retryError) ||
            retryMissing ||
            Directory.Exists(dataRoot))
        {
            throw new InvalidOperationException(
                "User-data deletion did not succeed after the lock was released: " +
                retryError);
        }
    }

    private static void TestReparsePointIsZeroDeletion(
        MethodInfo deleteTree,
        string runRoot)
    {
        string scenarioRoot = Path.Combine(runRoot, "reparse");
        string dataRoot = Path.Combine(scenarioRoot, "FilePromptAI-Win7");
        string outsideRoot = Path.Combine(scenarioRoot, "outside");
        string junction = Path.Combine(dataRoot, "z-external");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(outsideRoot);
        string ordinary = Path.Combine(dataRoot, "a-first.txt");
        string outside = Path.Combine(outsideRoot, "outside.txt");
        byte[] ordinaryBytes = Encoding.UTF8.GetBytes("must remain before junction");
        byte[] outsideBytes = Encoding.UTF8.GetBytes("must remain outside");
        File.WriteAllBytes(ordinary, ordinaryBytes);
        File.WriteAllBytes(outside, outsideBytes);

        CreateJunction(junction, outsideRoot);
        try
        {
            string error;
            bool alreadyMissing;
            if (InvokeDeleteTree(
                deleteTree,
                dataRoot,
                out alreadyMissing,
                out error))
            {
                throw new InvalidOperationException(
                    "A user-data junction unexpectedly passed preflight.");
            }

            if (alreadyMissing || string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException(
                    "Junction rejection did not report its preflight failure.");
            }

            AssertFileBytes(ordinary, ordinaryBytes, "ordinary file before junction");
            AssertFileBytes(outside, outsideBytes, "outside junction target");
            if (!Directory.Exists(junction))
            {
                throw new InvalidOperationException(
                    "Junction preflight unexpectedly removed the junction itself.");
            }
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, false);
            }
        }
    }

    private static bool InvokeDeleteTree(
        MethodInfo method,
        string root,
        out bool alreadyMissing,
        out string error)
    {
        object[] arguments = new object[] { root, false, string.Empty };
        bool result = (bool)method.Invoke(null, arguments);
        alreadyMissing = (bool)arguments[1];
        error = (string)arguments[2];
        return result;
    }

    private static void CreateJunction(string junction, string target)
    {
        string commandInterpreter = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        ProcessStartInfo start = new ProcessStartInfo();
        start.FileName = commandInterpreter;
        start.Arguments =
            "/d /q /c mklink /J " + Quote(junction) + " " + Quote(target);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using (Process process = Process.Start(start))
        {
            if (process == null)
            {
                throw new InvalidOperationException(
                    "Windows did not start the junction fixture command.");
            }

            process.WaitForExit();
            if (process.ExitCode != 0 || !Directory.Exists(junction))
            {
                throw new InvalidOperationException(
                    "Windows could not create the junction fixture.");
            }
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static void AssertFileBytes(
        string path,
        byte[] expected,
        string scenario)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(scenario + " was deleted.");
        }

        byte[] actual = File.ReadAllBytes(path);
        if (actual.Length != expected.Length)
        {
            throw new InvalidOperationException(scenario + " changed length.");
        }

        int index;
        for (index = 0; index < actual.Length; index++)
        {
            if (actual[index] != expected[index])
            {
                throw new InvalidOperationException(scenario + " changed bytes.");
            }
        }
    }

    private static MethodInfo RequireMethod(
        Type type,
        string name,
        BindingFlags flags)
    {
        MethodInfo method = type.GetMethod(name, flags);
        if (method == null)
        {
            throw new InvalidOperationException(
                "Required uninstaller method was not found: " + name);
        }

        return method;
    }

    private static Exception Unwrap(Exception exception)
    {
        TargetInvocationException invocation = exception as TargetInvocationException;
        while (invocation != null && invocation.InnerException != null)
        {
            exception = invocation.InnerException;
            invocation = exception as TargetInvocationException;
        }

        return exception;
    }

    private static void TryDeleteOwnedTestRoot(string runRoot)
    {
        try
        {
            string systemTemp = Path.GetFullPath(
                Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string resolved = Path.GetFullPath(runRoot);
            string name = Path.GetFileName(resolved);
            if (resolved.StartsWith(
                    systemTemp + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                name.StartsWith(
                    "FilePromptAI-UninstallerDataTest-",
                    StringComparison.Ordinal) &&
                Directory.Exists(resolved))
            {
                Directory.Delete(resolved, true);
            }
        }
        catch
        {
            // Test cleanup must not hide the original safety failure.
        }
    }
}
