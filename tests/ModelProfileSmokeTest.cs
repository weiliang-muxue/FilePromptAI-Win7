using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FilePromptAIWin7
{
    internal static class ModelProfileSmokeTest
    {
        private static int Main(string[] args)
        {
            if (args != null && args.Length == 4 &&
                args[0] == "--hold-exclusive-lock")
            {
                return HoldExclusiveLock(args[1], args[2], args[3]);
            }

            string root = Path.Combine(
                Path.GetTempPath(),
                "FilePromptAIModelProfiles-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string path = Path.Combine(root, "model-profiles.xml");
                ModelProfile profile = new ModelProfile
                {
                    Name = "内网主模型",
                    EndpointUrl = "https://127.0.0.1:9443/v1/chat/completions",
                    ApiKey = "profile-secret-42",
                    ModelName = "offline-model",
                    SystemPrompt = "Answer with cited evidence.",
                    Temperature = 0.25d,
                    TopP = 0.85d,
                    MaxOutputTokens = 3072
                };

                ModelProfileStore store = new ModelProfileStore(path);
                store.Save(new[] { profile });
                string raw = File.ReadAllText(path, Encoding.UTF8);
                Assert(
                    raw.IndexOf(profile.ApiKey, StringComparison.Ordinal) < 0,
                    "API key is not stored in plaintext");
                ModelProfile loaded = new ModelProfileStore(path)
                    .Load()
                    .Single();
                Assert(loaded.Name == profile.Name, "profile name round trip");
                Assert(loaded.EndpointUrl == profile.EndpointUrl, "URL round trip");
                Assert(loaded.ApiKey == profile.ApiKey, "API key round trip");
                Assert(loaded.ModelName == profile.ModelName, "model name round trip");
                Assert(
                    loaded.SystemPrompt == profile.SystemPrompt,
                    "system prompt round trip");
                Assert(
                    loaded.Temperature == profile.Temperature &&
                        loaded.TopP == profile.TopP &&
                        loaded.MaxOutputTokens == profile.MaxOutputTokens,
                    "generation options round trip");

                string anonymousPath = Path.Combine(root, "anonymous.xml");
                ModelProfile anonymousProfile = new ModelProfile
                {
                    Name = "匿名内网模型",
                    EndpointUrl = "http://127.0.0.1:11434/v1/chat/completions",
                    ApiKey = string.Empty,
                    ModelName = "local-model"
                };
                ModelProfileStore anonymousStore =
                    new ModelProfileStore(anonymousPath);
                anonymousStore.Save(new[] { anonymousProfile });
                ModelProfile loadedAnonymous = anonymousStore.Load().Single();
                Assert(
                    loadedAnonymous.ApiKey == string.Empty,
                    "empty API key profile round trip");
                Assert(
                    loadedAnonymous.Name == anonymousProfile.Name &&
                        loadedAnonymous.EndpointUrl ==
                            anonymousProfile.EndpointUrl &&
                        loadedAnonymous.ModelName == anonymousProfile.ModelName,
                    "empty API key profile fields round trip");
                Assert(
                    loadedAnonymous.SystemPrompt == string.Empty &&
                        !loadedAnonymous.Temperature.HasValue &&
                        !loadedAnonymous.TopP.HasValue &&
                        !loadedAnonymous.MaxOutputTokens.HasValue,
                    "legacy optional generation settings default empty");

                string invalidOptionsPath = Path.Combine(
                    root,
                    "invalid-options.xml");
                File.WriteAllText(
                    invalidOptionsPath,
                    "<FilePromptAIModelProfiles version=\"1\">" +
                    "<Profile><Name>兼容配置</Name>" +
                    "<EndpointUrl>http://127.0.0.1/v1/chat/completions</EndpointUrl>" +
                    "<ModelName>model</ModelName>" +
                    "<Temperature>NaN</Temperature><TopP>4</TopP>" +
                    "<MaxOutputTokens>-3</MaxOutputTokens>" +
                    "<ProtectedApiKey></ProtectedApiKey>" +
                    "</Profile></FilePromptAIModelProfiles>",
                    Encoding.UTF8);
                ModelProfile invalidOptions = new ModelProfileStore(
                    invalidOptionsPath).Load().Single();
                Assert(
                    !invalidOptions.Temperature.HasValue &&
                        !invalidOptions.TopP.HasValue &&
                        !invalidOptions.MaxOutputTokens.HasValue,
                    "invalid optional generation settings ignored safely");

                string crossUserPath = Path.Combine(root, "cross-user.xml");
                File.WriteAllText(
                    crossUserPath,
                    "<FilePromptAIModelProfiles version=\"1\">" +
                    "<Profile><Name>不可解密</Name>" +
                    "<EndpointUrl>https://127.0.0.1/v1/chat/completions</EndpointUrl>" +
                    "<ModelName>offline-model</ModelName>" +
                    "<ProtectedApiKey>not-valid-dpapi</ProtectedApiKey>" +
                    "</Profile></FilePromptAIModelProfiles>",
                    Encoding.UTF8);
                ModelProfileStore crossUserStore =
                    new ModelProfileStore(crossUserPath);
                Assert(
                    crossUserStore.Load().Count == 0 &&
                    crossUserStore.IsWriteProtected &&
                    File.Exists(crossUserPath),
                    "unreadable DPAPI entry protects the original store");
                Exception crossUserSaveFailure = CaptureFailure(delegate
                {
                    new ModelProfileStore(crossUserPath).Save(
                        new ModelProfile[0]);
                });
                Assert(
                    crossUserSaveFailure is InvalidOperationException &&
                    File.Exists(crossUserPath),
                    "unreadable DPAPI protection cannot be bypassed by a new store");

                VerifyPartialLoadProtection(root);

                VerifyExclusiveLockPreservesProfile(root, path, profile);

                string unsafePath = Path.Combine(root, "unsafe.xml");
                File.WriteAllText(
                    unsafePath,
                    "<!DOCTYPE profiles [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                    "<FilePromptAIModelProfiles version=\"1\"></FilePromptAIModelProfiles>",
                    Encoding.UTF8);
                ModelProfileStore unsafeStore = new ModelProfileStore(unsafePath);
                unsafeStore.Load();
                Assert(
                    !string.IsNullOrEmpty(unsafeStore.LoadWarning) &&
                    unsafeStore.LoadWarning.IndexOf(
                        "损坏",
                        StringComparison.Ordinal) >= 0 &&
                    !File.Exists(unsafePath) &&
                    Directory.GetFiles(
                        root,
                        "unsafe.xml.corrupt-*.xml").Length == 1,
                    "DTD profile file rejected and preserved as corrupt backup");

                string corruptPath = Path.Combine(root, "corrupt.xml");
                File.WriteAllText(
                    corruptPath,
                    "<FilePromptAIModelProfiles version=\"1\"><Profile>",
                    Encoding.UTF8);
                ModelProfileStore corruptStore =
                    new ModelProfileStore(corruptPath);
                Assert(
                    corruptStore.Load().Count == 0 &&
                    corruptStore.LoadWarning.IndexOf(
                        "已备份损坏文件",
                        StringComparison.Ordinal) >= 0 &&
                    !File.Exists(corruptPath) &&
                    Directory.GetFiles(
                        root,
                        "corrupt.xml.corrupt-*.xml").Length == 1,
                    "malformed profile file preserved as corrupt backup");

                Exception duplicateFailure = null;
                try
                {
                    store.Save(new[]
                    {
                        profile,
                        new ModelProfile
                        {
                            Name = "内网主模型",
                            EndpointUrl = profile.EndpointUrl,
                            ApiKey = "another-secret",
                            ModelName = profile.ModelName
                        }
                    });
                }
                catch (Exception exception)
                {
                    duplicateFailure = exception;
                }

                Assert(
                    duplicateFailure is InvalidOperationException,
                    "duplicate profile names rejected");

                Exception credentialUrlFailure = null;
                try
                {
                    ModelProfileStore.Validate(new ModelProfile
                    {
                        Name = "embedded-credentials",
                        EndpointUrl = "https://user:password@127.0.0.1/v1/chat/completions",
                        ApiKey = "key",
                        ModelName = "model"
                    });
                }
                catch (Exception exception)
                {
                    credentialUrlFailure = exception;
                }
                Assert(
                    credentialUrlFailure is InvalidOperationException,
                    "URL credentials rejected");

                store.Save(new ModelProfile[0]);
                Assert(
                    store.Load().Count == 0,
                    "deleting all profiles persists an empty store");
                Console.WriteLine("PASS | model profiles");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL | model profiles");
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch
                {
                    // A locked temporary folder can be removed on the next run.
                }
            }
        }

        private static void VerifyExclusiveLockPreservesProfile(
            string root,
            string sourcePath,
            ModelProfile expected)
        {
            string lockedPath = Path.Combine(root, "locked.xml");
            string readyPath = Path.Combine(root, "locked.ready");
            string releasePath = Path.Combine(root, "locked.release");
            File.Copy(sourcePath, lockedPath, true);
            byte[] originalBytes = File.ReadAllBytes(lockedPath);
            Process holder = null;
            ModelProfileStore lockedStore =
                new ModelProfileStore(lockedPath);
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = System.Reflection.Assembly
                        .GetExecutingAssembly().Location,
                    Arguments = "--hold-exclusive-lock " +
                        QuoteArgument(lockedPath) + " " +
                        QuoteArgument(readyPath) + " " +
                        QuoteArgument(releasePath),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                holder = Process.Start(startInfo);
                WaitForLockHolder(holder, readyPath);

                Assert(
                    lockedStore.Load().Count == 0,
                    "exclusive lock returns an empty profile list");
                Assert(
                    lockedStore.IsWriteProtected,
                    "exclusive lock enables sticky write protection");
                Assert(
                    lockedStore.LoadWarning.IndexOf(
                        "当前无法访问",
                        StringComparison.Ordinal) >= 0 &&
                    lockedStore.LoadWarning.IndexOf(
                        "损坏",
                        StringComparison.Ordinal) < 0,
                    "exclusive lock reports an access warning");
                Assert(
                    File.Exists(lockedPath) &&
                    Directory.GetFiles(
                        root,
                        "locked.xml.corrupt-*.xml").Length == 0,
                    "exclusive lock does not move the profile file");
            }
            finally
            {
                File.WriteAllText(releasePath, "release", Encoding.ASCII);
                if (holder != null)
                {
                    if (!holder.WaitForExit(5000))
                    {
                        holder.Kill();
                        holder.WaitForExit();
                    }

                    Assert(holder.ExitCode == 0, "exclusive lock holder exits");
                    holder.Dispose();
                }
            }

            Assert(
                originalBytes.SequenceEqual(File.ReadAllBytes(lockedPath)),
                "exclusive lock preserves profile bytes");
            Exception protectedSaveFailure = null;
            try
            {
                lockedStore.Save(new ModelProfile[0]);
            }
            catch (Exception exception)
            {
                protectedSaveFailure = exception;
            }
            Assert(
                protectedSaveFailure is InvalidOperationException &&
                lockedStore.IsWriteProtected &&
                originalBytes.SequenceEqual(File.ReadAllBytes(lockedPath)),
                "write protection remains sticky after the lock is released");
            ModelProfileStore secondStore = new ModelProfileStore(lockedPath);
            ModelProfile loaded = secondStore.Load().Single();
            Assert(
                loaded.Name == expected.Name &&
                loaded.EndpointUrl == expected.EndpointUrl &&
                loaded.ApiKey == expected.ApiKey &&
                loaded.ModelName == expected.ModelName,
                "profile loads after exclusive lock is released");
            Exception secondStoreSaveFailure = CaptureFailure(delegate
            {
                secondStore.Save(new ModelProfile[0]);
            });
            Assert(
                secondStore.IsWriteProtected &&
                secondStoreSaveFailure is InvalidOperationException &&
                originalBytes.SequenceEqual(File.ReadAllBytes(lockedPath)),
                "new profile store cannot bypass sticky path protection");
        }

        private static void VerifyPartialLoadProtection(string root)
        {
            string path = Path.Combine(root, "partial-load.xml");
            string content =
                "<FilePromptAIModelProfiles version=\"1\">" +
                "<Profile><Name>有效配置</Name>" +
                "<EndpointUrl>http://127.0.0.1/v1/chat/completions</EndpointUrl>" +
                "<ModelName>valid-model</ModelName>" +
                "<ProtectedApiKey></ProtectedApiKey></Profile>" +
                "<Profile><Name>不可解密</Name>" +
                "<EndpointUrl>http://127.0.0.1/v1/chat/completions</EndpointUrl>" +
                "<ModelName>dpapi-model</ModelName>" +
                "<ProtectedApiKey>not-valid-dpapi</ProtectedApiKey></Profile>" +
                "<Profile><Name>校验失败</Name>" +
                "<EndpointUrl>not-a-url</EndpointUrl>" +
                "<ModelName>invalid-model</ModelName>" +
                "<ProtectedApiKey></ProtectedApiKey></Profile>" +
                "<Profile><Name>未知字段</Name>" +
                "<EndpointUrl>http://127.0.0.1/v1/chat/completions</EndpointUrl>" +
                "<ModelName>unknown-field-model</ModelName>" +
                "<FutureField>must-not-disappear</FutureField>" +
                "<ProtectedApiKey></ProtectedApiKey></Profile>" +
                "</FilePromptAIModelProfiles>";
            File.WriteAllText(path, content, Encoding.UTF8);
            byte[] original = File.ReadAllBytes(path);
            ModelProfileStore store = new ModelProfileStore(path);
            ModelProfile loaded = store.Load().Single();
            Assert(
                loaded.Name == "有效配置" &&
                store.IsWriteProtected &&
                store.LoadWarning.IndexOf(
                    "部分模型配置",
                    StringComparison.Ordinal) >= 0,
                "partial model profile load keeps valid entries and protects store");
            Exception sameStoreFailure = CaptureFailure(delegate
            {
                store.Save(new[] { loaded });
            });
            ModelProfileStore secondStore = new ModelProfileStore(
                Path.Combine(root, ".", "partial-load.xml"));
            Exception secondStoreFailure = CaptureFailure(delegate
            {
                secondStore.Save(new ModelProfile[0]);
            });
            Assert(
                sameStoreFailure is InvalidOperationException &&
                secondStoreFailure is InvalidOperationException &&
                secondStore.IsWriteProtected &&
                original.SequenceEqual(File.ReadAllBytes(path)),
                "partial load cannot be overwritten through any store instance");
            Assert(
                File.ReadAllText(path, Encoding.UTF8).IndexOf(
                    "must-not-disappear",
                    StringComparison.Ordinal) >= 0,
                "skipped profile bytes remain in the original file");
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
                return exception;
            }
        }

        private static int HoldExclusiveLock(
            string profilePath,
            string readyPath,
            string releasePath)
        {
            try
            {
                using (FileStream stream = new FileStream(
                    profilePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    File.WriteAllText(readyPath, "ready", Encoding.ASCII);
                    DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                    while (!File.Exists(releasePath))
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

        private static void WaitForLockHolder(Process holder, string readyPath)
        {
            if (holder == null)
            {
                throw new InvalidOperationException(
                    "Exclusive lock holder did not start.");
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(readyPath))
            {
                if (holder.HasExited)
                {
                    throw new InvalidOperationException(
                        "Exclusive lock holder exited before acquiring the lock. " +
                        "Exit code: " + holder.ExitCode);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "Timed out waiting for the exclusive profile lock.");
                }

                Thread.Sleep(25);
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
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
}
