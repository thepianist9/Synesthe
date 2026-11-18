using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Week1Bench
{
    /// <summary>
    /// sherpa-onnx reads models from real file paths, but on Android StreamingAssets live
    /// inside the APK. Copy them once to persistentDataPath on first launch.
    /// </summary>
    public static class ModelFiles
    {
        public static readonly string[] Files =
        {
            "vad/silero_vad.onnx",
            "speaker/titanet_small.onnx",
            "sound/ced_tiny.int8.onnx", "sound/labels.csv",   // (laughs), (cries)… in the captions
        };

        /// <summary>Other languages (SpeechStack.Language.cs): the multilingual live model (~650 MB). Copied after
        /// captions have started, and only when another language may be wanted.</summary>
        public static readonly string[] LanguageFiles =
        {
            $"{SpeechRecognizers.Folder(SpeechRecognizers.Multilingual)}/encoder.onnx", $"{SpeechRecognizers.Folder(SpeechRecognizers.Multilingual)}/decoder.onnx",
            $"{SpeechRecognizers.Folder(SpeechRecognizers.Multilingual)}/joiner.onnx", $"{SpeechRecognizers.Folder(SpeechRecognizers.Multilingual)}/tokens.txt",
        };
        public static bool LanguagesReady { get; private set; }

        // Models an earlier version copied that are no longer used (a changed model gets a new folder, because files
        // that already exist are never copied again). Deleted on launch to free the space.
        // asr = Zipformer 20M, tagging = CED (bench only), asr-parakeet(-v3) = the Parakeet second pass, denoise = GTCRN
        // before it, hotwords = its topic word lists, langid = Whisper tiny's language check (all removed Nov 2026).
        static readonly string[] Obsolete = { "asr-parakeet", "asr-parakeet-v3", "asr", "tagging", "denoise", "hotwords", "asr-nemo480", "langid" };

        // Read once (on the main thread: the first caller is EnsureCopied), so worker threads can build paths too.
        static string _root;
        public static string Root => _root ??= Path.Combine(Application.persistentDataPath, "Models");
        public static string PathOf(string relative) => Path.Combine(Root, relative);
        public static bool Ready { get; private set; }
        public static string Error { get; private set; }

        /// <summary>Copies the shared models plus the live speech model <paramref name="live"/> (only the one in use: each
        /// is a few hundred MB).</summary>
        public static IEnumerator EnsureCopied(System.Action<string> progress, SpeechModel live = SpeechRecognizers.Default)
        {
            Error = null;
            var folder = SpeechRecognizers.Folder(live);
            var files = new System.Collections.Generic.List<string>(Files);
            foreach (var f in new[] { "encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt" }) files.Add($"{folder}/{f}");
            foreach (var old in Obsolete)
            {
                var dir = PathOf(old);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            string missing = null;
            yield return Copy(files, progress, m => missing = m);
            if (missing != null) { Error = missing; yield break; }
            Ready = true;
        }

        /// <summary>All <see cref="LanguageFiles"/> are already copied (a quick check, no copying).</summary>
        public static bool LanguagesOnDisk()
        {
            foreach (var rel in LanguageFiles)
            {
                var p = PathOf(rel);
                if (!File.Exists(p) || new FileInfo(p).Length == 0) return false;
            }
            return true;
        }

        /// <summary>Copies <see cref="LanguageFiles"/>; <see cref="LanguagesReady"/> says whether they're all there (a
        /// build may leave them out).</summary>
        public static IEnumerator EnsureLanguages(System.Action<string> progress = null)
        {
            string missing = null;
            yield return Copy(LanguageFiles, progress, m => missing = m);
            LanguagesReady = missing == null;
            if (missing != null) Debug.LogWarning($"[Bench] Other languages unavailable: {missing}");
        }

        // StreamingAssets/Models/<rel> → Root/<rel>, skipping files already there. Stops at the first missing one.
        static IEnumerator Copy(System.Collections.Generic.IList<string> files, System.Action<string> progress, System.Action<string> missing)
        {
            foreach (var rel in files)
            {
                var dst = PathOf(rel);
                if (File.Exists(dst) && new FileInfo(dst).Length > 0) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                var src = Path.Combine(Application.streamingAssetsPath, "Models", rel);
                progress?.Invoke($"Copying model {rel}…");
#if UNITY_ANDROID && !UNITY_EDITOR
                using var req = UnityWebRequest.Get(src);
                req.downloadHandler = new DownloadHandlerFile(dst) { removeFileOnAbort = true };
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    missing($"Missing {rel}: {req.error}. This build may leave it out (Build APK, all models) or run tools/fetch_dependencies.sh and rebuild.");
                    yield break;
                }
#else
                if (!File.Exists(src)) { missing($"Missing {rel}. Run tools/fetch_dependencies.sh."); yield break; }
                File.Copy(src, dst, true);
                yield return null;
#endif
            }
        }
    }
}
