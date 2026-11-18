using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaStarter.Captions
{
    /// <summary>
    /// A small local language model, run by <see cref="LocalLlm"/>. Not in the APK: downloaded once, the first time
    /// it's needed (or pushed over USB to persistentDataPath/Models/llm/).
    /// <see cref="Summary"/> (Qwen3 0.6B, ~400 MB) writes the conversation summaries (<see cref="ConversationSummarizer"/>).
    /// Captions aren't translated with one: Gemma 3 1B took 2–7 s a sentence on a Quest 3, too slow for a fast
    /// conversation (<see cref="FirefoxTranslations"/> takes milliseconds).
    /// </summary>
    public sealed class LlmModel
    {
        public static readonly LlmModel Summary = new("Qwen3-0.6B-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q4_K_M.gguf", 396705472, "400 MB");

        public readonly string FileName;
        readonly string _url, _size;
        readonly long _bytes;

        LlmModel(string fileName, string url, long bytes, string size)
        {
            FileName = fileName;
            _url = url;
            _bytes = bytes;
            _size = size;
        }

        public string Path => System.IO.Path.Combine(Week1Bench.ModelFiles.Root, "llm", FileName);
        public bool Ready => File.Exists(Path) && new FileInfo(Path).Length == _bytes;

        bool _downloading;
        float _progress;
        /// <summary>Why the last download failed (null if it didn't).</summary>
        public string Error { get; private set; }

        /// <summary>Have the model on disk: download it, or wait for a download already running. <paramref name="status"/>
        /// gets progress text ("Getting the summary model (one time, 400 MB)… 40%").</summary>
        public IEnumerator Ensure(Action<string> status, string what)
        {
            if (Ready) yield break;
            if (_downloading)
            {
                while (_downloading)
                {
                    status?.Invoke($"Getting the {what} model (one time, {_size})… {_progress * 100f:F0}%");
                    yield return new WaitForSecondsRealtime(0.25f);
                }
                yield break;
            }
            _downloading = true;
            Error = null;
            var part = Path + ".part";
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                if (File.Exists(part)) File.Delete(part);
            }
            catch (Exception e) { Error = e.Message; _downloading = false; yield break; }
            using var req = UnityWebRequest.Get(_url);
            req.downloadHandler = new DownloadHandlerFile(part) { removeFileOnAbort = true };
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                _progress = req.downloadProgress;
                status?.Invoke($"Getting the {what} model (one time, {_size})… {_progress * 100f:F0}%");
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (req.result != UnityWebRequest.Result.Success)
            {
                Error = req.error;
                status?.Invoke($"Couldn't get the {what} model: {req.error}. Check the headset's Wi-Fi.");
                Debug.LogWarning($"[Captions] {FileName} download failed: {req.error}");
            }
            else
            {
                try
                {
                    if (File.Exists(Path)) File.Delete(Path);
                    File.Move(part, Path);
                    Debug.Log($"[Captions] {FileName} downloaded");
                }
                catch (Exception e) { Error = e.Message; }
            }
            _downloading = false;
        }
    }
}
