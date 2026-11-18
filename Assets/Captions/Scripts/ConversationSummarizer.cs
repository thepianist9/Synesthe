using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Summaries of saved conversations, made on the headset by a small local model (<see cref="LocalLlm"/>, Qwen3 0.6B:
    /// the smallest that kept to what was said in tests; Gemma 3 270M made things up). Nothing leaves the headset.
    /// A saved conversation is summarised when it ends (<see cref="ConversationLog.Ended"/>), or on request from Past
    /// conversations. The model (<see cref="LlmModel"/>, ~400 MB) is downloaded once, the first time it's needed.
    /// It's loaded only while summarising, on a worker thread, then freed.
    /// Long conversations are summarised in parts that fit the model's context, then the parts' notes together.
    /// </summary>
    public class ConversationSummarizer : MonoBehaviour
    {
        public ConversationLog log;

        const int Context = 4096, NotesTokens = 220, SummaryTokens = 420, Overhead = 260;

        const string SystemPrompt =
            "You summarise conversations that were captured as live captions. The transcript may contain recognition " +
            "mistakes. Only use what is in the transcript; never invent names, facts or tasks.";

        /// <summary>A summary was written (the conversation's id).</summary>
        public event Action<string> Done;

        readonly Queue<string> _queue = new();
        string _current;                 // being summarised (or its model downloaded)
        volatile string _status;         // what's happening to _current, for the history view
        readonly Dictionary<string, string> _failed = new();
        Thread _worker;
        LocalLlm _llm;
        volatile bool _quit;
        volatile bool _stepAside;        // the running summary was cancelled to make way for live captions
        /// <summary>Set by the menu: the main menu is open. A conversation paused there is summarised there (not in
        /// the conversation menu, which is only a moment's pause).</summary>
        public Func<bool> InMainMenu;
        bool Live => log != null && log.Running && !(InMainMenu?.Invoke() ?? false);
        bool _waiting;                   // _current is held back until the main menu

        /// <summary>A summary is being written (or its model downloaded) right now: not just waiting its turn.</summary>
        public bool Working => _current != null && !_waiting;

        /// <summary>How far the summary (or its model's download) is, 0–1; -1 while there's no number yet.</summary>
        public float Progress
        {
            get
            {
                var st = _status;
                if (!Working || st == null) return -1f;
                var m = System.Text.RegularExpressions.Regex.Match(st, @"(\d+)%\s*$");
                return m.Success ? Mathf.Clamp01(int.Parse(m.Groups[1].Value) / 100f) : -1f;
            }
        }
        string _finished;                // set by the worker, picked up on the main thread

        void OnEnable() { if (log != null) log.Ended += Request; }
        void OnDisable() { if (log != null) log.Ended -= Request; }

        void OnDestroy()
        {
            _quit = true;
            _llm?.Cancel();
            _worker?.Join(3000);
        }

        /// <summary>Summarise this saved conversation (again, if it already has a summary).</summary>
        public void Request(string id)
        {
            if (id == null || id == _current || _queue.Contains(id)) return;
            _failed.Remove(id);
            _queue.Enqueue(id);
            if (_current == null) StartCoroutine(Run());
        }

        /// <summary>What's happening to a conversation's summary right now: "Waiting", "Summarising… 40%", the reason it
        /// failed; null if nothing.</summary>
        public string StatusOf(string id) =>
            id == _current ? _status : _queue.Contains(id) ? "Waiting to summarise" : _failed.TryGetValue(id, out var f) ? f : null;

        public bool Busy(string id) => id == _current || _queue.Contains(id);

        IEnumerator Run()
        {
            while (_queue.Count > 0 && !_quit)
            {
                _current = _queue.Dequeue();
                var id = _current;
                var isDay = ConversationLog.IsDay(id);
                ConversationLog.Saved saved = null;
                List<DayEntry> entries = null;
                string heard;
                if (isDay)
                {
                    // A day is summarised from its conversations' summaries: any missing are made first.
                    var day = ConversationLog.Load(id);
                    var missing = day.Parts.FindAll(c => !File.Exists(ConversationLog.SummaryPath(c)) && !_failed.ContainsKey(c) && !_queue.Contains(c));
                    if (missing.Count > 0)
                    {
                        var rest = _queue.ToArray();
                        _queue.Clear();
                        foreach (var c in missing) _queue.Enqueue(c);
                        _queue.Enqueue(id);
                        foreach (var q in rest) _queue.Enqueue(q);
                        _current = null;
                        continue;
                    }
                    entries = DayEntries(day);
                    if (entries.Count == 0) { Fail(id, "None of this day's conversations has a summary yet"); continue; }
                    var sb = new StringBuilder();
                    foreach (var e in entries) sb.Append(e.Full).Append('\n');
                    heard = sb.ToString();
                }
                else
                {
                    if (!File.Exists(ConversationLog.PathOf(id))) { _current = null; continue; }
                    saved = ConversationLog.Load(id);
                    if (saved.Lines.Count == 0)
                    {
                        File.WriteAllText(ConversationLog.SummaryPath(id), "Nothing was said.");
                        Finished(id);
                        continue;
                    }
                    heard = ConversationLog.PlainText(saved);
                }

                // Online first, when it's on and there's a network: no download, no load on the headset, so it doesn't
                // have to wait for the main menu either. If it fails, the headset makes the summary as before.
                if (OnlineSummary.Use)
                {
                    _status = "Summarising online…";
                    var t0 = Time.realtimeSinceStartup;
                    string answer = null, why = null;
                    var user = isDay ? DayRequest(ConversationLog.Load(id), entries, e => e.Full)
                                     : $"Transcript (speaker: words):\n{heard}\n{ConversationAsk}";
                    yield return OnlineSummary.Ask(SystemPrompt, user, (t, e) => { answer = t; why = e; });
                    if (answer != null)
                    {
                        File.WriteAllText(ConversationLog.SummaryPath(id), ConversationLog.OnlineMark + KeepHeardTodos(Clean(answer), heard));
                        Debug.Log($"[Captions] {(isDay ? "Day" : "Conversation")} {id} summarised online in {Time.realtimeSinceStartup - t0:F0} s");
                        Finished(id);
                        continue;
                    }
                    Debug.LogWarning($"[Captions] Online summary of {id} failed ({why}): summarising on the headset");
                }

                if (!LlmModel.Summary.Ready)
                {
                    yield return LlmModel.Summary.Ensure(st => _status = st, "summary");
                    if (!LlmModel.Summary.Ready)
                    {
                        _failed[id] = _status ?? "Couldn't download the summary model";
                        foreach (var q in _queue) _failed[q] = _failed[id];
                        _queue.Clear();
                        _current = null;
                        yield break;
                    }
                }
                // Summaries only happen in the main menu. The summary model takes 2–4 cores: next to a mode it starves
                // speech recognition (captions 10–20 s late), so in a mode it waits, and one that's running stops (and
                // starts over later) the moment a mode starts. Asked for from a mode, it's made back in the main menu.
                _waiting = Live;
                while (Live && !_quit)
                {
                    _status = "Summarises when you're back in the main menu";
                    yield return new WaitForSecondsRealtime(1f);
                }
                _waiting = false;
                if (_quit) break;
                _status = "Summarising…";
                _finished = null;
                _stepAside = false;
                string error = null;
                var path = LlmModel.Summary.Path; // (Unity paths: main thread only)
                var dayInfo = isDay ? ConversationLog.Load(id) : null;
                // Two cores, at the lowest priority: it's background work, and at normal priority on 4 cores it took
                // time from rendering (passthrough stuttered). The model's own worker threads are started from this
                // thread, so they inherit its priority.
                const int threads = 2;
                _worker = new Thread(() =>
                {
                    LowestPriority();
                    try { _finished = isDay ? SummariseDay(dayInfo, entries, heard, path, threads) : Summarise(saved, path, threads); }
                    catch (Exception e) { error = e.Message; }
                }) { IsBackground = true, Name = "Summary", Priority = System.Threading.ThreadPriority.BelowNormal };
                var started = Time.realtimeSinceStartup;
                _worker.Start();
                while (_worker.IsAlive)
                {
                    if (Live && !_stepAside)
                    {
                        _stepAside = true;
                        _llm?.Cancel();
                        Debug.Log($"[Captions] {id}: summary stopped (a mode started), will start over in the main menu");
                    }
                    yield return new WaitForSecondsRealtime(0.25f);
                }
                _worker = null;
                if (_stepAside)
                {
                    // Back to the front of the queue; it waits above until the conversation ends.
                    var rest = _queue.ToArray();
                    _queue.Clear();
                    _queue.Enqueue(id);
                    foreach (var q in rest) _queue.Enqueue(q);
                    _current = null;
                    _status = null;
                    continue;
                }
                if (_finished != null)
                {
                    File.WriteAllText(ConversationLog.SummaryPath(id), _finished);
                    Debug.Log($"[Captions] {(isDay ? "Day" : "Conversation")} {id} summarised in {Time.realtimeSinceStartup - started:F0} s" +
                              (isDay ? $" ({entries.Count} conversations)" : $" ({saved.Lines.Count} lines)"));
                    Finished(id);
                }
                else Fail(id, $"Couldn't summarise: {error}");
            }
            _current = null;
        }

        void Fail(string id, string why)
        {
            _failed[id] = why;
            Debug.LogWarning($"[Captions] {id} not summarised: {why}");
            _current = null;
            _status = null;
        }

        // A summary was written. A conversation on a day with others also brings that day's summary up to date.
        void Finished(string id)
        {
            _current = null;
            _status = null;
            Done?.Invoke(id);
            if (ConversationLog.IsDay(id)) return;
            var day = ConversationLog.DayOf(id);
            if (!Busy(day) && ConversationLog.ConversationsOn(day).Count >= 2) _queue.Enqueue(day);
        }

        // ---------------- a day ----------------

        sealed class DayEntry { public string Full, Short; }

        // Each summarised conversation of the day: a heading (time, length, people) and its summary; Short keeps only its
        // Summary and To do parts, for when the whole day doesn't fit the headset model's context.
        static List<DayEntry> DayEntries(ConversationLog.Saved day)
        {
            var list = new List<DayEntry>();
            foreach (var cid in day.Parts)
            {
                var c = ConversationLog.Load(cid);
                if (c.Summary == null || c.Lines.Count == 0) continue;
                var head = $"Conversation at {c.Start:HH:mm} ({(c.Seconds < 60 ? "under a minute" : $"{Math.Round(c.Seconds / 60)} min")}" +
                           (c.People.Count > 0 ? ", with " + string.Join(", ", c.People) : "") + "):\n";
                var text = c.Summary.Trim();
                var k = text.IndexOf("Key points:", StringComparison.OrdinalIgnoreCase);
                var t = text.IndexOf("To do:", StringComparison.OrdinalIgnoreCase);
                var brief = k >= 0 && t > k ? text[..k] + text[t..] : text;
                list.Add(new DayEntry { Full = head + text + "\n", Short = head + brief.Trim() + "\n" });
            }
            return list;
        }

        static string DayRequest(ConversationLog.Saved day, List<DayEntry> entries, Func<DayEntry, string> pick)
        {
            var sb = new StringBuilder();
            sb.Append($"Summaries of {entries.Count} conversations from {day.Start:dddd d MMMM}, in order:\n\n");
            foreach (var e in entries) sb.Append(pick(e)).Append('\n');
            return sb.Append(DayAsk).ToString();
        }

        const string DayAsk =
            "Write exactly these three parts about the whole day:\n" +
            "Summary: three to five sentences: the main topics of the day and who they came up with.\n" +
            "People: the people talked with, by the names used above, or \"none named\".\n" +
            "To do: every to-do from the conversations above, merged without repeats, each as \"Name: verb …\". " +
            "Write \"none\" if there are none.";

        string SummariseDay(ConversationLog.Saved day, List<DayEntry> entries, string heard, string modelPath, int threads)
        {
            using var llm = _llm = LocalLlm.Load(modelPath, Context, threads);
            try
            {
                // Whole summaries if they fit; else only their Summary and To do parts; else the latest that fit.
                var budget = llm.ContextSize - Overhead - SummaryTokens;
                Func<DayEntry, string> pick = e => e.Full;
                var user = DayRequest(day, entries, pick);
                if (llm.CountTokens(user) > budget) { pick = e => e.Short; user = DayRequest(day, entries, pick); }
                var keep = new List<DayEntry>(entries);
                while (keep.Count > 1 && llm.CountTokens(user) > budget)
                {
                    keep.RemoveAt(0);
                    user = DayRequest(day, keep, pick);
                }
                return KeepHeardTodos(Clean(Ask(llm, user + " /no_think", SummaryTokens, 0, 1)), heard);
            }
            finally { _llm = null; }
        }

        // ---------------- summarising (worker thread) ----------------

#if UNITY_ANDROID && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("c")] static extern int gettid();
        [System.Runtime.InteropServices.DllImport("c")] static extern int setpriority(int which, int who, int prio);
#endif

        // Nice 19 for the calling thread (Linux: per thread, and threads it starts get it too).
        static void LowestPriority()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { setpriority(0 /* PRIO_PROCESS: a thread id here */, gettid(), 19); }
            catch (Exception e) { Debug.LogWarning("[Captions] Summary priority not lowered: " + e.Message); }
#endif
        }

        string Summarise(ConversationLog.Saved saved, string modelPath, int threads)
        {
            using var llm = _llm = LocalLlm.Load(modelPath, Context, threads);
            try
            {
                // Lines into parts that fit the context with room for the instructions and the answer.
                var budget = llm.ContextSize - Overhead - Math.Max(NotesTokens, SummaryTokens);
                var parts = new List<string>();
                var sb = new StringBuilder();
                var used = 0;
                foreach (var l in saved.Lines)
                {
                    var line = $"{l.who}{(l.src == "sign" ? " (signed)" : "")}: {l.text}\n";
                    var n = llm.CountTokens(line);
                    if (used + n > budget && sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); used = 0; }
                    if (n > budget) continue; // one enormous line: skip rather than fail
                    sb.Append(line);
                    used += n;
                }
                if (sb.Length > 0) parts.Add(sb.ToString());
                var heard = string.Concat(parts);

                if (parts.Count == 1) return Final(llm, $"Transcript (speaker: words):\n{parts[0]}", 1, 1, heard);
                var notes = new StringBuilder();
                for (var i = 0; i < parts.Count; i++)
                {
                    if (_quit || _stepAside) throw new Exception("cancelled");
                    var user = $"Part {i + 1} of {parts.Count} of a conversation (speaker: words):\n{parts[i]}\n" +
                               "Write up to five short bullet points: what was talked about, who said what, and any plans or decisions. /no_think";
                    notes.Append($"Part {i + 1}:\n").Append(Clean(Ask(llm, user, NotesTokens, i, parts.Count + 1))).Append("\n\n");
                }
                return Final(llm, $"Notes on each part of one conversation, in order:\n{notes}", parts.Count, parts.Count + 1, heard);
            }
            finally { _llm = null; }
        }

        const string ConversationAsk =
            "Write exactly these three parts:\n" +
            "Summary: two to four sentences on what the conversation was about.\n" +
            "Key points: up to five short bullet points.\n" +
            "To do: only actions someone clearly said they will do later (\"I'll send…\", \"we need to book…\"), " +
            "each as \"Name: verb …\" using words from the transcript. Not descriptions of people, topics or " +
            "things already done. Write \"none\" if nobody said they would do anything.";

        string Final(LocalLlm llm, string material, int step, int steps, string transcript)
        {
            var user = material + "\n" + ConversationAsk + " /no_think";
            return KeepHeardTodos(Clean(Ask(llm, user, SummaryTokens, step - 1, steps)), transcript);
        }

        static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
        {
            "about", "after", "again", "also", "before", "could", "from", "have", "into", "just", "make", "more", "need",
            "should", "some", "that", "their", "them", "then", "there", "they", "this", "will", "with", "would", "your",
        };

        /// <summary>Drops to-dos the conversation doesn't back up: fewer than half of a bullet's words (four letters or
        /// more, after the name) were said in it. Small models sometimes add tasks nobody mentioned
        /// ("Person 2: Mention physical traits or character").</summary>
        internal static string KeepHeardTodos(string summary, string transcript)
        {
            var at = summary.IndexOf("To do:", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return summary;
            var heard = new HashSet<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(transcript.ToLowerInvariant(), @"\p{L}{4,}"))
                heard.Add(Stem(m.Value));
            var lines = summary[(at + 6)..].Split('\n');
            var kept = new List<string>();
            var dropped = 0;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var body = line.TrimStart('-', '*', '•', ' ');
                if (body.StartsWith("none", StringComparison.OrdinalIgnoreCase)) continue;
                var colon = body.IndexOf(':');
                var task = colon >= 0 && colon < 30 ? body[(colon + 1)..] : body;
                int words = 0, found = 0;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(task.ToLowerInvariant(), @"\p{L}{4,}"))
                {
                    if (Common.Contains(m.Value)) continue;
                    words++;
                    if (heard.Contains(Stem(m.Value))) found++;
                }
                if (words == 0 || found * 2 >= words) kept.Add(line);
                else dropped++;
            }
            if (dropped == 0) return summary;
            Debug.Log($"[Captions] Summary: dropped {dropped} to-do{(dropped > 1 ? "s" : "")} not backed by the transcript");
            return summary[..(at + 6)] + "\n" + (kept.Count > 0 ? string.Join("\n", kept) : "None");
        }

        // "booking", "booked", "books" → "book": enough to match a task's words with the transcript's.
        static string Stem(string w)
        {
            foreach (var end in Endings)
                if (w.Length - end.Length >= 4 && w.EndsWith(end, StringComparison.Ordinal)) { w = w[..^end.Length]; break; }
            return w.Length > 5 ? w[..5] : w;
        }
        static readonly string[] Endings = { "ing", "ed", "es", "s" };

        // One request, with its progress shown as a share of all the requests for this conversation.
        string Ask(LocalLlm llm, string user, int maxTokens, int step, int steps)
        {
            var done = false;
            var watcher = new Thread(() =>
            {
                while (!done && !_quit)
                {
                    var (read, of, written) = llm.Progress;
                    var within = of > 0 ? 0.8f * read / of + 0.2f * Mathf.Clamp01(written / (float)maxTokens) : 0f;
                    _status = $"Summarising… {(step + within) / steps * 100f:F0}%";
                    Thread.Sleep(250);
                }
            }) { IsBackground = true };
            watcher.Start();
            try { return llm.Chat(SystemPrompt, user, maxTokens); }
            finally { done = true; }
        }

        // The model's reasoning block (Qwen3 may write an empty one) and stray markdown.
        static string Clean(string s)
        {
            var end = s.IndexOf("</think>", StringComparison.Ordinal);
            if (end >= 0) s = s[(end + 8)..];
            return s.Replace("**", "").Replace("###", "").Replace("##", "").Replace("\"none\"", "None").Trim();
        }
    }
}
