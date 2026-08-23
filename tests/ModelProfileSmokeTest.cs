using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FilePromptAIWin7
{
    internal static class ModelProfileSmokeTest
    {
        private static int Main()
        {
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
                    File.Exists(crossUserPath),
                    "unreadable DPAPI entry skipped without blocking store");

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
                    !File.Exists(unsafePath),
                    "DTD profile file rejected and preserved as corrupt backup");

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
