using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Developer option only. Synesthe keeps inference on the headset: the people being captioned haven't agreed to
    /// their words leaving it. So online summaries exist only for comparing against the headset model during
    /// development: hidden, and never used, unless the app is launched with <c>-e onlinesum 1</c> (Editor:
    /// SOPHIE_ONLINE_SUMMARIES=1). See <see cref="DevUnlocked"/>.
    /// With that flag and the setting on, an API key on the headset and a network connection, a
    /// saved conversation's text is sent to Anthropic's Claude API to be summarised, instead of to the small model on the
    /// headset. Faster, and better at long conversations. Offline, without a key, or if the request fails, the summary is
    /// made on the headset as before (<see cref="ConversationSummarizer"/>).
    /// Only the text of a conversation you saved and asked to be summarised is sent: never audio, video, faces or voices.
    /// The key is never in the app or the repo. To add it, put it in files/anthropic_key.txt on the headset (see
    /// docs: piped from the clipboard over adb, so it isn't shown or saved anywhere). On the next start the app moves
    /// it into the Android keystore (<c>SecureStore.java</c>: encrypted with a device-bound key that can't be read out)
    /// and deletes the text file. <c>-e forgetkey 1</c> removes it. In the Editor: that file, or ANTHROPIC_API_KEY.
    /// Raw HTTPS through UnityWebRequest: the C# SDK doesn't run under IL2CPP on Android without extra work.
    /// Without a key: the free summary server (tools/summary-server, a Cloudflare Worker on Workers AI, Llama 3.3 70B),
    /// when its address and token are in the app (Resources/summary_server.txt, not in git) or in
    /// files/summary_server.txt on the headset: two lines, the URL and the token.
    /// </summary>
    public static class OnlineSummary
    {
        const string PrefKey = "captions.onlineSummaries";
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        public const string Model = "claude-opus-5-5";
        const int MaxTokens = 8000, TimeoutSeconds = 90;

        /// <summary>The setting: summarise online when possible. Off unless the wearer turns it on.</summary>
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(PrefKey, 0) == 1;
            set { PlayerPrefs.SetInt(PrefKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        static bool? _dev;
        /// <summary>Launched with -e onlinesum 1 (Editor: SOPHIE_ONLINE_SUMMARIES=1). Without it nothing is sent
        /// anywhere and the Conversations tab doesn't offer Online.</summary>
        public static bool DevUnlocked => _dev ??= ReadDevFlag();

        static bool ReadDevFlag()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent.Call<string>("getStringExtra", "onlinesum") == "1";
            }
            catch { return false; }
#else
            return Environment.GetEnvironmentVariable("SOPHIE_ONLINE_SUMMARIES") == "1";
#endif
        }

        public static string KeyPath => Path.Combine(Application.persistentDataPath, "anthropic_key.txt");

        const string StoreName = "anthropic_key";
        static string _key;
        static bool _loaded;

        // Loaded once; again only when a new key file turns up.
        static string ApiKey
        {
            get
            {
                bool fresh;
                try { fresh = File.Exists(KeyPath); } catch { fresh = false; }
                if (!_loaded || fresh) { _loaded = true; _key = LoadKey(); }
                return _key;
            }
        }

        static string LoadKey()
        {
            string file = null;
            try { if (File.Exists(KeyPath)) file = File.ReadAllText(KeyPath).Trim(); } catch { }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var store = new AndroidJavaClass("com.metastarter.week1bench.SecureStore");
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                if (!string.IsNullOrEmpty(file))
                {
                    if (!file.StartsWith("sk-ant-"))
                    {
                        Debug.LogWarning("[Captions] Online summaries: anthropic_key.txt doesn't look like an Anthropic key (sk-ant-…); not used, file deleted");
                        Delete(KeyPath);
                    }
                    else if (store.CallStatic<bool>("put", activity, StoreName, file))
                    {
                        Delete(KeyPath);
                        Debug.Log("[Captions] Online summaries: API key moved into the Android keystore; the text file is deleted");
                    }
                    else
                    {
                        Debug.LogWarning("[Captions] Online summaries: couldn't store the key in the keystore; using the file for now");
                        return file;
                    }
                }
                return store.CallStatic<string>("get", activity, StoreName);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Captions] Online summaries: keystore unavailable ({e.GetType().Name})");
                return file != null && file.StartsWith("sk-ant-") ? file : null;
            }
#else
            return !string.IsNullOrEmpty(file) ? file : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
#endif
        }

        static void Delete(string path)
        {
            try { File.Delete(path); } catch (Exception e) { Debug.LogWarning($"[Captions] Couldn't delete {Path.GetFileName(path)}: {e.GetType().Name}"); }
        }

        /// <summary>Removes the stored key (launch option -e forgetkey 1). Summaries go back to the free server or the headset.</summary>
        public static void ForgetKey()
        {
            if (File.Exists(KeyPath)) Delete(KeyPath);
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var store = new AndroidJavaClass("com.metastarter.week1bench.SecureStore");
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                store.CallStatic("remove", activity, StoreName);
            }
            catch (Exception e) { Debug.LogWarning($"[Captions] Online summaries: keystore unavailable ({e.GetType().Name})"); }
#endif
            _key = null; _loaded = true;
            Debug.Log("[Captions] Online summaries: API key removed");
        }

        /// <summary>Launch option -e sumtest 1: one short request, to check the key, the network and the answer.
        /// Logs where it went, how long it took and the answer's length (not the key).</summary>
        public static IEnumerator SelfTest()
        {
            var where = HasKey ? "Anthropic" : HasServer ? "free server" : "nowhere (no key or server)";
            var t0 = Time.realtimeSinceStartup;
            string text = null, why = null;
            yield return Ask("Summarise the conversation in one sentence.",
                "Anna: Can we move the team meeting to Thursday at three?\nBen: Yes, and I'll book the small room.",
                (t, e) => { text = t; why = e; });
            var secs = Time.realtimeSinceStartup - t0;
            if (text != null) Debug.Log($"[Captions] Online summary test via {where}: OK in {secs:F1} s · \"{text}\"");
            else Debug.LogWarning($"[Captions] Online summary test via {where}: failed in {secs:F1} s · {why}");
        }

        public static bool HasKey => !string.IsNullOrEmpty(ApiKey);

        // The free server: URL and token, from files/summary_server.txt on the headset, else built into the app.
        static (string url, string token) Server
        {
            get
            {
                string text = null;
                try
                {
                    var path = Path.Combine(Application.persistentDataPath, "summary_server.txt");
                    if (File.Exists(path)) text = File.ReadAllText(path);
                }
                catch { }
                text ??= Resources.Load<TextAsset>("summary_server")?.text;
                var lines = (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return lines.Length >= 2 && lines[0].Trim().StartsWith("https://") ? (lines[0].Trim(), lines[1].Trim()) : (null, null);
            }
        }

        public static bool HasServer => Server.url != null;
        /// <summary>Somewhere to summarise online: your own Anthropic key, or the free server.</summary>
        public static bool CanGoOnline => HasKey || HasServer;
        /// <summary>Who the text goes to, for the settings note.</summary>
        public static string Provider => HasKey ? "Anthropic (Claude)" : "the free summary server (Cloudflare, Llama)";

        /// <summary>The next summary goes online: developer flag, the setting is on, there's somewhere to send it, and a network.</summary>
        public static bool Use => DevUnlocked && Enabled && CanGoOnline && Application.internetReachability != NetworkReachability.NotReachable;

        [Serializable] class Message { public string role; public string content; }
        [Serializable] class OutputConfig { public string effort; }
        [Serializable]
        class Request
        {
            public string model; public int max_tokens; public string system; public Message[] messages;
            public OutputConfig output_config; public string fallbacks;
        }
        [Serializable] class Block { public string type; public string text; }
        [Serializable] class ApiError { public string type; public string message; }
        [Serializable] class Response { public Block[] content; public string stop_reason; public ApiError error; }

        /// <summary>One request. <paramref name="done"/> gets the answer's text, or null and why it failed.</summary>
        public static IEnumerator Ask(string system, string user, Action<string, string> done)
        {
            var key = ApiKey;
            if (string.IsNullOrEmpty(key))
            {
                if (HasServer) { yield return AskServer(system, user, done); yield break; }
                done(null, "no API key or summary server"); yield break;
            }
            var body = JsonUtility.ToJson(new Request
            {
                model = Model,
                max_tokens = MaxTokens,
                system = system,
                messages = new[] { new Message { role = "user", content = user } },
                output_config = new OutputConfig { effort = "low" }, // a summary: low effort is quick and enough
                fallbacks = "default",                               // a declined request is retried on another model
            });
            using var req = new UnityWebRequest(Endpoint, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = TimeoutSeconds,
            };
            req.SetRequestHeader("content-type", "application/json");
            req.SetRequestHeader("x-api-key", key);
            req.SetRequestHeader("anthropic-version", "2026-10-21");
            req.SetRequestHeader("anthropic-beta", "server-side-fallback-2026-07-01");
            yield return req.SendWebRequest();

            Response r = null;
            try { r = JsonUtility.FromJson<Response>(req.downloadHandler.text ?? ""); } catch { }
            if (req.result != UnityWebRequest.Result.Success)
            {
                var why = r?.error?.message;
                done(null, $"{(req.responseCode > 0 ? $"HTTP {req.responseCode}" : req.error)}{(string.IsNullOrEmpty(why) ? "" : ": " + why)}");
                yield break;
            }
            if (r == null || r.content == null) { done(null, "unreadable answer"); yield break; }
            if (r.stop_reason == "refusal") { done(null, "declined"); yield break; }
            var sb = new StringBuilder();
            foreach (var b in r.content) if (b != null && b.type == "text") sb.Append(b.text);
            var text = sb.ToString().Trim();
            if (text.Length == 0) { done(null, $"empty answer ({r.stop_reason})"); yield break; }
            done(text, null);
        }

        [Serializable] class ServerRequest { public string system; public string user; }
        [Serializable] class ServerResponse { public string text; public string model; public ApiError error; }

        static IEnumerator AskServer(string system, string user, Action<string, string> done)
        {
            var (url, token) = Server;
            var body = JsonUtility.ToJson(new ServerRequest { system = system, user = user });
            using var req = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = TimeoutSeconds,
            };
            req.SetRequestHeader("content-type", "application/json");
            req.SetRequestHeader("authorization", "Bearer " + token);
            yield return req.SendWebRequest();
            ServerResponse r = null;
            try { r = JsonUtility.FromJson<ServerResponse>(req.downloadHandler.text ?? ""); } catch { }
            if (req.result != UnityWebRequest.Result.Success)
            {
                var why = r?.error?.message;
                done(null, $"server {(req.responseCode > 0 ? $"HTTP {req.responseCode}" : req.error)}{(string.IsNullOrEmpty(why) ? "" : ": " + why)}");
                yield break;
            }
            var text = r?.text?.Trim();
            if (string.IsNullOrEmpty(text)) { done(null, "server: empty answer"); yield break; }
            Debug.Log($"[Captions] Summary server answered with {r.model}");
            done(text, null);
        }
    }
}
