using System;
using System.Collections;
using System.IO;
using System.Reflection;

internal static class VerifiedPayloadLeaseSmokeTest
{
    private static int Main(string[] args)
    {
        object lease = null;
        try
        {
            if (args.Length != 2)
            {
                throw new ArgumentException(
                    "Usage: VerifiedPayloadLeaseSmokeTest <verifier.exe> <package-copy>");
            }

            string verifierPath = Path.GetFullPath(args[0]);
            string packageRoot = Path.GetFullPath(args[1]);
            Assembly verifier = Assembly.LoadFrom(verifierPath);
            Type program = verifier.GetType("AcceptanceProgram", true);
            MethodInfo checkPackage = program.GetMethod(
                "CheckPackage",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (checkPackage == null)
            {
                throw new MissingMethodException(
                    program.FullName,
                    "CheckPackage");
            }

            lease = Invoke(checkPackage, null, new object[] { packageRoot });
            Type leaseType = lease.GetType();
            MethodInfo assertIntact = RequireMethod(leaseType, "AssertIntact");
            MethodInfo getLibraries = RequireMethod(
                leaseType,
                "GetApplicationLibraryPaths");

            string applicationPath = Path.Combine(
                packageRoot,
                @"app\FilePromptAI.exe");
            string movedApplication = applicationPath + ".moved";
            string appDirectory = Path.GetDirectoryName(applicationPath);
            string movedDirectory = appDirectory + ".moved";
            string replacement = Path.Combine(
                Path.GetDirectoryName(packageRoot),
                "replacement-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(replacement, "replacement");
            try
            {
                AssertIOException(
                    delegate
                    {
                        using (FileStream stream = new FileStream(
                            applicationPath,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete))
                        {
                        }
                    },
                    "write-open");
                AssertIOException(
                    delegate { File.Delete(applicationPath); },
                    "delete");
                AssertIOException(
                    delegate { File.Move(applicationPath, movedApplication); },
                    "file rename");
                AssertIOException(
                    delegate
                    {
                        File.Replace(replacement, applicationPath, null);
                    },
                    "file replacement");
                AssertIOException(
                    delegate { Directory.Move(appDirectory, movedDirectory); },
                    "directory rename");

                string injectedLibrary = Path.Combine(
                    appDirectory,
                    "NPOI.Evil.dll");
                File.WriteAllText(injectedLibrary, "not trusted");
                try
                {
                    string[] libraries = (string[])Invoke(
                        getLibraries,
                        lease,
                        new object[0]);
                    for (int index = 0; index < libraries.Length; index++)
                    {
                        if (string.Equals(
                            libraries[index],
                            injectedLibrary,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "An injected DLL entered the verified load allowlist.");
                        }
                    }
                    AssertInvocationFailure(
                        delegate
                        {
                            Invoke(assertIntact, lease, new object[0]);
                        },
                        "an added package file");
                }
                finally
                {
                    if (File.Exists(injectedLibrary))
                    {
                        File.Delete(injectedLibrary);
                    }
                }

                Invoke(assertIntact, lease, new object[0]);
            }
            finally
            {
                if (File.Exists(replacement))
                {
                    File.Delete(replacement);
                }
            }

            ((IDisposable)lease).Dispose();
            lease = null;

            using (FileStream stream = new FileStream(
                applicationPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                if (stream.Length == 0)
                {
                    throw new InvalidDataException(
                        "The packaged application is unexpectedly empty.");
                }
            }
            Directory.Move(appDirectory, movedDirectory);
            Directory.Move(movedDirectory, appDirectory);

            using (FileStream writer = new FileStream(
                applicationPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete))
            {
                AssertInvocationFailure(
                    delegate
                    {
                        Invoke(
                            checkPackage,
                            null,
                            new object[] { packageRoot });
                    },
                    "a pre-existing writable package handle");
            }

            Console.WriteLine(
                "PASS | verified payload lease blocks writes, replacement, and untrusted DLLs");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL | verified payload lease");
            Console.Error.WriteLine(Unwrap(exception));
            return 1;
        }
        finally
        {
            IDisposable disposable = lease as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
        }
    }

    private static MethodInfo RequireMethod(Type type, string name)
    {
        MethodInfo method = type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
        {
            throw new MissingMethodException(type.FullName, name);
        }
        return method;
    }

    private static object Invoke(
        MethodInfo method,
        object instance,
        object[] arguments)
    {
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException ?? exception;
        }
    }

    private static void AssertIOException(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        throw new InvalidOperationException(
            "The verified package lease allowed " + operation + ".");
    }

    private static void AssertInvocationFailure(
        Action action,
        string description)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(
            "The verifier accepted " + description + ".");
    }

    private static Exception Unwrap(Exception exception)
    {
        Exception actual = exception;
        while (actual is TargetInvocationException &&
            actual.InnerException != null)
        {
            actual = actual.InnerException;
        }
        return actual;
    }
}
