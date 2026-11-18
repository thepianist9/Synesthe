using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Captions in other languages need letters the app's fonts (Inter, Quicksand) don't have: CJK, Devanagari,
    /// Arabic, Hebrew, Thai, and for Quicksand Cyrillic and Greek. Rather than ship (and keep in git) big font atlases,
    /// use the fonts the system already has, as dynamic fallbacks of every TMP text. On the Quest these are Android's
    /// Noto fonts, opened by file (looking them up by family name finds nothing on Android); in the Editor, the Mac's
    /// own by family. Added at startup: a translated caption can be in any script, whatever the spoken language.
    /// Removed again when the director goes (in the Editor the font assets would otherwise keep them).
    /// Letters still need shaping (Arabic joining, right-to-left, Hindi vowel order): see <see cref="ComplexText"/>.
    /// </summary>
    public static class ScriptFonts
    {
        struct Source { public string Script, File; public int Face; public string[] Families; }

        // Per script: the Android file (face index for collections), then macOS family names, first found wins.
        static readonly Source[] Sources =
        {
            new() { Script = "Korean", File = null,
                    Families = new[] { "Apple SD Gothic Neo" } },   // the Android CJK face already has Hangul
            new() { Script = "Hindi", File = "NotoSansDevanagariUI-VF.ttf|NotoSansDevanagari-VF.ttf|NotoSansDevanagari-Regular.ttf",
                    Families = new[] { "Kohinoor Devanagari", "Devanagari Sangam MN" } },
            new() { Script = "Arabic", File = "NotoNaskhArabicUI-Regular.ttf|NotoNaskhArabic-Regular.ttf|NotoSansArabic-Regular.ttf",
                    Families = new[] { "Geeza Pro" } },
            new() { Script = "Hebrew", File = "NotoSansHebrew-Regular.ttf",
                    Families = new[] { "Arial Hebrew" } },
            new() { Script = "Thai", File = "NotoSansThaiUI-Regular.ttf|NotoSansThai-Regular.ttf",
                    Families = new[] { "Thonburi" } },
            // Last: on the Mac, Arial Unicode MS has every script and would win over the better fonts above.
            new() { Script = "Chinese/Japanese/Korean", File = "NotoSansCJK-Regular.ttc", Face = 2,
                    Families = new[] { "PingFang SC", "Hiragino Sans GB", "Heiti SC", "Arial Unicode MS" } },
            new() { Script = "Cyrillic/Greek", File = "RobotoStatic-Regular.ttf|Roboto-Regular.ttf",
                    Families = new[] { "Helvetica Neue", "Arial" } },
        };

        const string SystemFonts = "/system/fonts/";

        static readonly List<TMP_FontAsset> _added = new();
        static readonly HashSet<TMP_FontAsset> _patched = new();
        static bool _global, _loaded;

        /// <summary>What was found, for the log.</summary>
        public static string Summary { get; private set; } = "not loaded";

        /// <summary>Load the system fallbacks (once) and give them to every TMP text and to these fonts.</summary>
        public static void AddTo(params TMP_FontAsset[] fonts)
        {
            if (!_loaded)
            {
                _loaded = true;
                var found = new List<string>();
                var missing = new List<string>();
                foreach (var s in Sources)
                {
                    var f = Load(s);
                    if (f == null) { if (s.File != null || Application.platform != RuntimePlatform.Android) missing.Add(s.Script); continue; }
                    _added.Add(f);
                    found.Add($"{s.Script}: {f.name}");
                }
                Summary = (found.Count > 0 ? string.Join(", ", found) : "none found") +
                          (missing.Count > 0 ? $" · missing: {string.Join(", ", missing)}" : "");
                Debug.Log($"[Captions] Fallback fonts for other scripts: {Summary}");
            }
            // Global: menus, history and summaries use other font assets than the captions.
            if (!_global && TMP_Settings.instance != null)
            {
                _global = true;
                TMP_Settings.fallbackFontAssets ??= new List<TMP_FontAsset>();
                foreach (var f in _added) if (!TMP_Settings.fallbackFontAssets.Contains(f)) TMP_Settings.fallbackFontAssets.Add(f);
            }
            foreach (var font in fonts)
            {
                if (font == null || !_patched.Add(font)) continue;
                font.fallbackFontAssetTable ??= new List<TMP_FontAsset>();
                foreach (var f in _added) if (!font.fallbackFontAssetTable.Contains(f)) font.fallbackFontAssetTable.Add(f);
            }
        }

        static TMP_FontAsset Load(Source s)
        {
            TMP_FontAsset f = null;
            if (Application.platform == RuntimePlatform.Android)
            {
                if (s.File == null) return null;
                foreach (var file in s.File.Split('|'))
                {
                    var path = SystemFonts + file;
                    if (!File.Exists(path)) continue;
                    try { f = TMP_FontAsset.CreateFontAsset(path, s.Face, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024); }
                    catch (System.Exception e) { Debug.LogWarning($"[Captions] font {path}: {e.Message}"); }
                    if (f != null) { f.name = $"{file} (system)"; return f; }
                }
                return null;
            }
            foreach (var family in s.Families)
            {
                try { f = TMP_FontAsset.CreateFontAsset(family, "Regular"); }
                catch (System.Exception e) { Debug.LogWarning($"[Captions] font {family}: {e.Message}"); }
                if (f != null) { f.name = $"{family} (system)"; return f; }
            }
            return null;
        }

        /// <summary>Take the fallbacks off the fonts again and free them.</summary>
        public static void Remove()
        {
            foreach (var font in _patched)
                if (font != null) font.fallbackFontAssetTable?.RemoveAll(f => _added.Contains(f));
            _patched.Clear();
            if (_global && TMP_Settings.instance != null) TMP_Settings.fallbackFontAssets?.RemoveAll(f => _added.Contains(f));
            _global = false;
            foreach (var f in _added) if (f != null) Object.Destroy(f);
            _added.Clear();
            _loaded = false;
        }
    }
}
