using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Firefox's translation models (Mozilla, MPL-2.0) and the engine Firefox runs them with (bergamot translator,
    /// native: libsophie_mt, tools/mt/build_mt.sh). One small int8 Marian model per language pair to or from English,
    /// 17–44 MB each, downloaded from Mozilla the first time a pair is needed and kept in Models/mt/.
    /// On a Mac a caption sentence takes 3–45 ms (Gemma 3 1B: 1–4 s); two languages that aren't English go through it.
    /// Made on the main thread; then not thread-safe: used from <see cref="CaptionTranslator"/>'s one thread.
    /// </summary>
    public sealed class FirefoxTranslations : IDisposable
    {
        const string Lib = "sophie_mt";
        [DllImport(Lib)] static extern IntPtr smt_load(byte[] model, byte[] lex, byte[] srcVocab, byte[] trgVocab);
        [DllImport(Lib)] static extern void smt_free(IntPtr m);
        [DllImport(Lib)] static extern int smt_translate(IntPtr a, IntPtr b, byte[] text, byte[] output, int outCap);
        [DllImport(Lib)] static extern IntPtr smt_last_error();

        // Mozilla's model list (Remote Settings) and where its files are.
        const string RecordsUrl = "https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/translations-models/records";
        const string FilesUrl = "https://firefox-settings-attachments.cdn.mozilla.net/";
        const double ListMaxAgeDays = 7;

        [Serializable] class Attachment { public string hash, location; public long size; }
        [Serializable] class Record { public string name, fileType, fromLang, toLang, version, filter_expression; public Attachment attachment; }
        [Serializable] class RecordList { public Record[] data; }

        // Read when it's made: persistentDataPath (under ModelFiles.Root) is main-thread only.
        readonly string Dir = Path.Combine(Week1Bench.ModelFiles.Root, "mt");

        readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
        readonly Dictionary<string, IntPtr> _loaded = new();
        Record[] _records;

        /// <summary>What it's doing, for the menu ("Getting Hindi translation (one time, 21 MB)… 40%"); null when idle.</summary>
        public string Status { get; private set; }

        /// <summary>Mozilla's code for a caption language (Chinese is "zh-Hans" there).</summary>
        static string Code(string lang) => lang == "zh" ? "zh-Hans" : lang;

        /// <summary>Translates <paramref name="text"/> (one or more sentences). Downloads the models it needs first.</summary>
        public string Translate(string text, string from, string to)
        {
            if (from == to) return text;
            var route = Route(Code(from), Code(to));
            var a = Load(route[0]);
            var b = route.Length > 1 ? Load(route[1]) : IntPtr.Zero;
            var input = Encoding.UTF8.GetBytes(text + "\0");
            var output = new byte[Math.Max(4096, input.Length * 8)];
            var n = smt_translate(a, b, input, output, output.Length);
            if (n < 0) throw new Exception("translating failed: " + Marshal.PtrToStringAnsi(smt_last_error()));
            return Encoding.UTF8.GetString(output, 0, n);
        }

        /// <summary>Gets the models for from → to ready (downloads, loads) without translating anything.</summary>
        public void Prepare(string from, string to)
        {
            if (from == to) return;
            foreach (var pair in Route(Code(from), Code(to))) Load(pair);
        }

        // The pair (or two, through English) that translate from → to.
        string[] Route(string from, string to)
        {
            if (Has(from, to)) return new[] { $"{from}-{to}" };
            if (from != "en" && to != "en" && Has(from, "en") && Has("en", to)) return new[] { $"{from}-en", $"en-{to}" };
            throw new Exception($"no translation model for {CaptionTranslator.Name(from)} → {CaptionTranslator.Name(to)}");
        }

        bool Has(string from, string to) => Files(from, to).Length > 0;

        // One pair's newest files that Firefox on Android ships (the list also has Nightly-only and desktop-only versions).
        Record[] Files(string from, string to)
        {
            var all = Records().Where(r => r.fromLang == from && r.toLang == to && ForAndroid(r.filter_expression)).ToArray();
            if (all.Length == 0) return all;
            var newest = all.Max(r => Version(r.version));
            return all.Where(r => Version(r.version) == newest).ToArray();
        }

        static bool ForAndroid(string filter) => string.IsNullOrWhiteSpace(filter) || filter.Trim() == "env.appinfo.OS == 'Android'";

        // "2.1" > "2.1a1" > "2.0".
        static long Version(string v)
        {
            var m = Regex.Match(v ?? "", @"^(\d+)\.(\d+)(?:([ab])(\d+))?$");
            if (!m.Success) return -1;
            return long.Parse(m.Groups[1].Value) * 1_000_000 + long.Parse(m.Groups[2].Value) * 1000 +
                   (m.Groups[3].Success ? (m.Groups[3].Value == "a" ? 0 : 100) + int.Parse(m.Groups[4].Value) : 999);
        }

        Record[] Records()
        {
            if (_records != null) return _records;
            var path = Path.Combine(Dir, "models.json");
            string json = null;
            var fresh = File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays < ListMaxAgeDays;
            if (!fresh)
            {
                try
                {
                    Status = "Getting the list of translation languages…";
                    json = _http.GetStringAsync(RecordsUrl).GetAwaiter().GetResult();
                    Directory.CreateDirectory(Dir);
                    // Written whole or not at all: a write cut off (the headset slept, the app was stopped) once left the
                    // file the right size but all zeros, and every translation failed (2026-11-18).
                    var tmp = path + ".part";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch (Exception e)
                {
                    if (!File.Exists(path)) { Status = null; throw new Exception("couldn't get the translation languages (" + e.Message + "). Check the headset's Wi-Fi."); }
                }
                finally { Status = null; }
            }
            json ??= File.ReadAllText(path);
            try { _records = JsonUtility.FromJson<RecordList>(json)?.data; }
            catch (Exception) { _records = null; }
            if (_records == null || _records.Length == 0)
            {
                // A damaged list: drop it, so the next call gets a fresh one (if this one wasn't just downloaded).
                _records = null;
                try { File.Delete(path); } catch (Exception) { }
                if (fresh) return Records();
                throw new Exception("the list of translation languages was damaged. Check the headset's Wi-Fi and try again.");
            }
            return _records;
        }

        IntPtr Load(string pair)
        {
            if (_loaded.TryGetValue(pair, out var h)) return h;
            var (model, lex, src, trg) = OnDisk(pair, null);
            h = smt_load(Z(model), Z(lex), Z(src), Z(trg));
            if (h == IntPtr.Zero) throw new Exception($"the {pair} model didn't load: " + Marshal.PtrToStringAnsi(smt_last_error()));
            _loaded[pair] = h;
            return h;
        }

        /// <summary>Downloads (doesn't load) what from → to needs. <paramref name="what"/>: the progress text's start.</summary>
        public void Fetch(string from, string to, string what)
        {
            if (from == to) return;
            foreach (var pair in Route(Code(from), Code(to))) OnDisk(pair, what);
        }

        /// <summary>Megabytes still to download for from → to (0: all on disk).</summary>
        public long MissingMb(string from, string to)
        {
            if (from == to) return 0;
            long bytes = 0;
            foreach (var pair in Route(Code(from), Code(to)))
            {
                var (a, b) = Langs(pair);
                foreach (var r in Files(a, b))
                {
                    var file = Path.Combine(Dir, pair, r.name);
                    if (!File.Exists(file) || new FileInfo(file).Length != r.attachment.size) bytes += r.attachment.size;
                }
            }
            return (bytes + (1 << 20) - 1) >> 20;
        }

        // "zh-Hans-en": the language codes can have a dash themselves.
        static (string from, string to) Langs(string pair)
        {
            if (pair.StartsWith("zh-Hans-")) return ("zh-Hans", pair[8..]);
            var i = pair.IndexOf('-');
            return (pair[..i], pair[(i + 1)..]);
        }

        // The pair's files (model, lexical shortlist, source and target vocabularies), downloaded if they aren't yet.
        (string model, string lex, string src, string trg) OnDisk(string pair, string what)
        {
            var (from, to) = Langs(pair);
            var files = Files(from, to);
            var dir = Path.Combine(Dir, pair);
            var mb = Math.Max(1, files.Sum(f => f.attachment.size) / (1024 * 1024));
            var other = from == "en" ? to : from;
            what ??= $"Getting {CaptionTranslator.Name(other == "zh-Hans" ? "zh" : other)} translation (one time, {mb} MB)";
            string Get(string type)
            {
                var r = files.FirstOrDefault(f => f.fileType == type);
                if (r == null) return "";
                var file = Path.Combine(dir, r.name);
                if (File.Exists(file) && new FileInfo(file).Length == r.attachment.size) return file;
                Download(r, file, what);
                return file;
            }
            var model = Get("model");
            var lex = Get("lex");
            var vocab = Get("vocab");
            var src = vocab.Length > 0 ? vocab : Get("srcvocab");
            var trg = vocab.Length > 0 ? vocab : Get("trgvocab");
            return (model, lex, src, trg);
        }

        void Download(Record r, string file, string what)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            // Its own name: a translator being replaced (translation turned off and on) may still be getting the same file.
            var part = $"{file}.{Guid.NewGuid():N}.part";
            try
            {
                using (var resp = _http.GetAsync(FilesUrl + r.attachment.location, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    using var input = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                    using var output = File.Create(part);
                    var buf = new byte[1 << 16];
                    long got = 0;
                    int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        got += n;
                        Status = $"{what}… {got * 100 / Math.Max(1, r.attachment.size)}%";
                    }
                }
                using (var sha = SHA256.Create())
                using (var s = File.OpenRead(part))
                {
                    var hash = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                    if (hash != r.attachment.hash) throw new Exception($"{r.name} came down damaged");
                }
                if (File.Exists(file) && new FileInfo(file).Length == r.attachment.size) File.Delete(part); // the other one won
                else
                {
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(part, file);
                }
            }
            catch (Exception e)
            {
                try { File.Delete(part); } catch { /* ignore */ }
                throw new Exception($"couldn't get {r.name} ({e.Message}). Check the headset's Wi-Fi.");
            }
            finally { Status = null; }
        }

        static byte[] Z(string s) => Encoding.UTF8.GetBytes((s ?? "") + "\0");

        public void Dispose()
        {
            foreach (var h in _loaded.Values) smt_free(h);
            _loaded.Clear();
            _http.Dispose();
        }
    }
}
