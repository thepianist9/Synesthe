using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Translates finished captions into the viewer's language on the headset, with Firefox's translation models
    /// (<see cref="FirefoxTranslations"/>: one small model per language pair, downloaded the first time it's needed).
    /// A sentence takes milliseconds (3–45 ms on a Mac), so the translation replaces the caption about as soon as it's
    /// finished; with Gemma 3 1B it was 2–7 s on a Quest 3, and in a fast conversation the translation came too late.
    /// Jobs run one at a time on their own thread.
    /// </summary>
    public sealed class CaptionTranslator : IDisposable
    {
        public sealed class Job
        {
            public object Tag;          // the caller's (the caption segment)
            public string Text, From, To;
            public string Result, Error;
            public double Seconds;
            public bool Live;           // the caption still being spoken (shown at once, translated again when it's finished)
            internal bool PrepareOnly;  // get the models ready, translate nothing
            internal int Run;           // which run of captions it belongs to (see Clear)
        }

        readonly ConcurrentQueue<Job> _todo = new(), _done = new();
        readonly AutoResetEvent _signal = new(false);
        readonly Thread _thread;
        volatile bool _quit;
        readonly FirefoxTranslations _mt = new();

        /// <summary>The last failure (no model for that language, no Wi-Fi for the download, …); null if none.</summary>
        public string Error { get; private set; }

        /// <summary>What it's doing, for the menu (downloading a language's model); null when idle.</summary>
        public string Status => _mt.Status ?? _fetch?.Status;

        // Downloads (no loading) every language's model in the background: see FetchAll.
        FirefoxTranslations _fetch;
        Thread _fetcher;
        volatile int _fetchRun;

        /// <summary>Make it on the main thread.</summary>
        public CaptionTranslator()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Translate", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        volatile int _run;

        public void Request(Job job)
        {
            job.Run = _run;
            _todo.Enqueue(job);
            _signal.Set();
        }

        /// <summary>Download and load what from → to needs now, so the first caption doesn't wait for it.</summary>
        public void Prepare(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return;
            Request(new Job { From = from, To = to, PrepareOnly = true });
        }

        /// <summary>
        /// Downloads, in the background and once, the models that translate each of <paramref name="froms"/> into
        /// <paramref name="to"/> (about 1.1 GB for the 34 speech languages), so a caption in any language people may speak
        /// is translated without waiting for a download. Only downloads: a model is loaded when a caption needs it.
        /// Call on the main thread; a new call (another target language) replaces the running one.
        /// </summary>
        public void FetchAll(IEnumerable<string> froms, string to)
        {
            var run = ++_fetchRun;
            var langs = new List<string>(froms);
            langs.Remove(to);
            if (to != "en") { langs.Remove("en"); langs.Insert(0, "en"); } // en → to first: every other one goes through it
            _fetch ??= new FirefoxTranslations(); // here: it reads the model folder, a main-thread call
            var mt = _fetch;
            var previous = _fetcher;
            _fetcher = new Thread(() =>
            {
                previous?.Join(); // one at a time: the old one stops after its current file
                FetchError = null;
                long mb = 0;
                try { foreach (var l in langs) mb += mt.MissingMb(l, to); }
                catch (Exception e) { FetchError = e.Message; return; }
                if (mb == 0) return;
                for (var i = 0; i < langs.Count && run == _fetchRun && !_quit; i++)
                {
                    try { mt.Fetch(langs[i], to, $"Getting translation for every language ({i + 1} of {langs.Count}, {mb} MB in all, once)"); }
                    catch (Exception e) { FetchError = $"{Name(langs[i])}: {e.Message}"; } // the rest may still come down
                }
                UnityEngine.Debug.Log($"[Captions] translation fetch into {to}: {(FetchError ?? "every language ready")} ({mb} MB)");
            }) { IsBackground = true, Name = "Translate fetch", Priority = ThreadPriority.Lowest };
            _fetcher.Start();
        }

        /// <summary>The last download that failed in <see cref="FetchAll"/> (tried again next time); null if none.</summary>
        public string FetchError { get; private set; }

        /// <summary>A finished translation (or failure), on the caller's thread.</summary>
        public bool TryGet(out Job job)
        {
            while (_done.TryDequeue(out job))
                if (job.Run == _run) return true; // older: asked for before Clear, nobody wants it now
            return false;
        }

        /// <summary>Forget every caption waiting to be translated (a new conversation starts, or back to the menu):
        /// the queue is emptied, and the one being translated now is dropped when it's done. Model loading stays.</summary>
        public void Clear()
        {
            _run++;
            var keep = new List<Job>();
            while (_todo.TryDequeue(out var j)) if (j.PrepareOnly) keep.Add(j);
            foreach (var j in keep) { j.Run = _run; _todo.Enqueue(j); }
            while (_done.TryDequeue(out _)) { }
        }

        void Loop()
        {
            BackgroundThread.Nice(5); // captions in the other language can wait a moment; the frame can't
            while (!_quit)
            {
                if (!_todo.TryDequeue(out var job)) { _signal.WaitOne(500); continue; }
                try
                {
                    var sw = Stopwatch.StartNew();
                    if (job.PrepareOnly) { _mt.Prepare(job.From, job.To); Error = null; continue; }
                    job.Result = _mt.Translate(job.Text, job.From, job.To).Trim();
                    job.Seconds = sw.Elapsed.TotalSeconds;
                    if (job.Result.Length == 0) job.Error = "empty translation";
                    else Error = null;
                }
                catch (Exception e) { job.Error = Error = e.Message; }
                if (!job.PrepareOnly) _done.Enqueue(job);
            }
            _mt.Dispose();
        }

        /// <summary>Testing aid: translates a few conversation lines into Hindi and German (and Spanish into Hindi,
        /// through English) on a background thread, logging each with its time.</summary>
        public static void Bench(Action<string> log)
        {
            string[] lines =
            {
                "Maybe it's just an unusual trait. I'm grateful for that, especially as a teacher I can hopefully help explain some different concepts with my face as well.",
                "My dad is a pretty funny guy.", "Yeah, but I think I also have this sense of humor.", "Oh, you think so?",
                "Who are you most like in your family?",
            };
            var t = new CaptionTranslator(); // here: it reads the model folder, a main-thread call
            new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                var jobs = new List<Job>();
                foreach (var to in new[] { "hi", "de" })
                    foreach (var l in lines) jobs.Add(new Job { Text = l, From = "en", To = to });
                jobs.Add(new Job { Text = "Mi padre es un hombre muy gracioso.", From = "es", To = "hi" });
                // Each pair's first sentence includes its download and load; the same line again shows the plain time.
                jobs.Add(new Job { Text = lines[1], From = "en", To = "hi" });
                foreach (var j in jobs) t.Request(j);
                var got = 0;
                while (got < jobs.Count && sw.Elapsed.TotalSeconds < 600)
                {
                    if (t.TryGet(out var j)) { got++; log($"{j.From}→{j.To} {j.Seconds * 1000:F0} ms | {j.Text} => {j.Error ?? j.Result}"); }
                    else Thread.Sleep(20);
                }
                log($"done: {got} in {sw.Elapsed.TotalSeconds:F1} s (downloads and loading included)");
                t.Dispose();
            }) { IsBackground = true, Name = "Translate bench" }.Start();
        }

        public void Dispose()
        {
            _quit = true;
            _fetchRun++;
            _signal.Set();
            _thread.Join(3000);
            var fetch = _fetch;
            if (fetch != null) new Thread(() => { _fetcher?.Join(); fetch.Dispose(); }) { IsBackground = true }.Start();
        }

        /// <summary>English names of the languages captions can be in (the prompt's, and for the menu).</summary>
        public static string Name(string code) => Names.TryGetValue(code ?? "", out var n) ? n : code;

        static readonly Dictionary<string, string> Names = new()
        {
            ["en"] = "English", ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German", ["it"] = "Italian",
            ["pt"] = "Portuguese", ["nl"] = "Dutch", ["sv"] = "Swedish", ["da"] = "Danish", ["nb"] = "Norwegian",
            ["fi"] = "Finnish", ["pl"] = "Polish", ["cs"] = "Czech", ["sk"] = "Slovak", ["sl"] = "Slovenian",
            ["hr"] = "Croatian", ["hu"] = "Hungarian", ["ro"] = "Romanian", ["bg"] = "Bulgarian", ["el"] = "Greek",
            ["et"] = "Estonian", ["lt"] = "Lithuanian", ["lv"] = "Latvian", ["uk"] = "Ukrainian", ["ru"] = "Russian",
            ["tr"] = "Turkish", ["ar"] = "Arabic", ["he"] = "Hebrew", ["hi"] = "Hindi", ["th"] = "Thai",
            ["vi"] = "Vietnamese", ["ja"] = "Japanese", ["ko"] = "Korean", ["zh"] = "Chinese",
        };

        /// <summary>The headset's language as a two-letter code (English if it isn't one captions can be in).</summary>
        public static string DeviceLanguage()
        {
            var code = UnityEngine.Application.systemLanguage switch
            {
                UnityEngine.SystemLanguage.German => "de", UnityEngine.SystemLanguage.French => "fr",
                UnityEngine.SystemLanguage.Spanish => "es", UnityEngine.SystemLanguage.Italian => "it",
                UnityEngine.SystemLanguage.Portuguese => "pt", UnityEngine.SystemLanguage.Dutch => "nl",
                UnityEngine.SystemLanguage.Swedish => "sv", UnityEngine.SystemLanguage.Danish => "da",
                UnityEngine.SystemLanguage.Norwegian => "nb", UnityEngine.SystemLanguage.Finnish => "fi",
                UnityEngine.SystemLanguage.Polish => "pl", UnityEngine.SystemLanguage.Czech => "cs",
                UnityEngine.SystemLanguage.Slovak => "sk", UnityEngine.SystemLanguage.Slovenian => "sl",
                UnityEngine.SystemLanguage.Hungarian => "hu", UnityEngine.SystemLanguage.Romanian => "ro",
                UnityEngine.SystemLanguage.Bulgarian => "bg", UnityEngine.SystemLanguage.Greek => "el",
                UnityEngine.SystemLanguage.Estonian => "et", UnityEngine.SystemLanguage.Lithuanian => "lt",
                UnityEngine.SystemLanguage.Latvian => "lv", UnityEngine.SystemLanguage.Ukrainian => "uk",
                UnityEngine.SystemLanguage.Russian => "ru", UnityEngine.SystemLanguage.Turkish => "tr",
                UnityEngine.SystemLanguage.Arabic => "ar", UnityEngine.SystemLanguage.Hebrew => "he",
                UnityEngine.SystemLanguage.Hindi => "hi", UnityEngine.SystemLanguage.Thai => "th",
                UnityEngine.SystemLanguage.Vietnamese => "vi", UnityEngine.SystemLanguage.Japanese => "ja",
                UnityEngine.SystemLanguage.Korean => "ko", UnityEngine.SystemLanguage.Chinese => "zh",
                UnityEngine.SystemLanguage.ChineseSimplified => "zh", UnityEngine.SystemLanguage.ChineseTraditional => "zh",
                _ => "en",
            };
            return code;
        }
    }
}
