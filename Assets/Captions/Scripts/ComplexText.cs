using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine.TextCore;

namespace MetaStarter.Captions
{
    /// <summary>
    /// TextMesh Pro draws one glyph per character, left to right. That is enough for Latin, Cyrillic, Greek, CJK and
    /// Thai, but not for:
    /// <list type="bullet">
    /// <item>Arabic (also Persian, Urdu): letters take a different shape at the start, middle and end of a word, and
    /// the line reads right to left. Here letters become their joined forms (Unicode presentation forms, which the
    /// system Arabic font has), lam + alef becomes its ligature, and the text is laid out right to left with numbers
    /// and Latin words kept left to right.</item>
    /// <item>Hebrew: right to left (no joining).</item>
    /// <item>Hindi (Devanagari): the short i vowel sign ि is typed after its consonant but drawn before it; moved here.
    /// Vowel signs above and below are placed by the font's mark positioning. Conjuncts (क्ष, त्र…) still show as the
    /// first letter with a visible virama (क्‌ष) — readable, but not typeset: that needs a full shaping engine.</item>
    /// </list>
    /// Attached to a text with <see cref="Attach"/>; text without these scripts passes through untouched.
    /// </summary>
    public sealed class ComplexText : ITextPreprocessor
    {
        readonly TMP_Text _t;
        bool _flipped;

        ComplexText(TMP_Text t) { _t = t; }

        /// <summary>Shape this text's Arabic/Hebrew/Hindi from now on.</summary>
        public static void Attach(TMP_Text t)
        {
            if (t == null || t.textPreprocessor is ComplexText) return;
            t.textPreprocessor = new ComplexText(t);
            var features = new List<OTL_FeatureTag>(t.fontFeatures ?? new List<OTL_FeatureTag>());
            foreach (var f in new[] { OTL_FeatureTag.kern, OTL_FeatureTag.liga, OTL_FeatureTag.mark, OTL_FeatureTag.mkmk })
                if (!features.Contains(f)) features.Add(f);
            features.RemoveAll(f => f == 0);
            t.fontFeatures = features;
        }

        public string PreprocessText(string text)
        {
            if (string.IsNullOrEmpty(text) || !NeedsShaping(text)) { SetRightToLeft(false); return text; }
            var s = Shape(text, out var rtl);
            SetRightToLeft(rtl);
            return s;
        }

        static bool NeedsShaping(string text)
        {
            foreach (var c in text) if (c >= 0x0590 && c < 0x0980 || c >= 0xFB1D && c < 0xFF00) return true;
            return false;
        }

        void SetRightToLeft(bool rtl)
        {
            if (_t == null) return;
            if (_t.isRightToLeftText != rtl) _t.isRightToLeftText = rtl;
            var h = _t.horizontalAlignment;
            if (rtl && h == HorizontalAlignmentOptions.Left) { _t.horizontalAlignment = HorizontalAlignmentOptions.Right; _flipped = true; }
            else if (!rtl && _flipped)
            {
                if (h == HorizontalAlignmentOptions.Right) _t.horizontalAlignment = HorizontalAlignmentOptions.Left;
                _flipped = false;
            }
        }

        /// <summary>The shaped text, in the order TMP should lay it out (right to left when <paramref name="rtl"/>).</summary>
        public static string Shape(string text, out bool rtl)
        {
            var chars = new List<char>(text.Length);
            var tag = new List<bool>(text.Length);   // inside a rich-text tag: left as is
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '<')
                {
                    var end = text.IndexOf('>', i);
                    if (end > i && end - i < 64)
                    {
                        for (; i <= end; i++) { chars.Add(text[i]); tag.Add(true); }
                        i--;
                        continue;
                    }
                }
                if (c >= 0x064B && c <= 0x0652) continue;   // Arabic short-vowel marks: rare in captions, and would sit badly
                chars.Add(c); tag.Add(false);
            }
            ReorderDevanagari(chars, tag);
            JoinArabic(chars, tag);

            int rtlCount = 0, ltrCount = 0;
            for (int i = 0; i < chars.Count; i++)
            {
                if (tag[i]) continue;
                var d = Direction(chars[i]);
                if (d > 0) rtlCount++; else if (d < 0) ltrCount++;
            }
            rtl = rtlCount > ltrCount;
            // TMP lays the whole line out one way; runs that read the other way are reversed so they come out right.
            ReverseRuns(chars, tag, rtl ? -1 : 1);
            if (rtl)
                for (int i = 0; i < chars.Count; i++)
                    if (!tag[i] && Mirror.TryGetValue(chars[i], out var m)) chars[i] = m;
            var sb = new StringBuilder(chars.Count);
            foreach (var c in chars) sb.Append(c);
            return sb.ToString();
        }

        // ---- direction ----------------------------------------------------------------------------------------

        /// <summary>+1 right to left, −1 left to right, 0 neutral (spaces, punctuation, marks).</summary>
        static int Direction(char c)
        {
            if (c >= 0x0590 && c <= 0x08FF || c >= 0xFB1D && c <= 0xFDFF || c >= 0xFE70 && c <= 0xFEFF)
                return c >= 0x0660 && c <= 0x0669 || c >= 0x06F0 && c <= 0x06F9 ? -1 : (IsMark(c) ? 0 : 1);   // Arabic digits read left to right
            return char.IsLetterOrDigit(c) ? -1 : 0;
        }

        static bool IsMark(char c)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.EnclosingMark;
        }

        /// <summary>Reverse each run of the given direction (+1 RTL, −1 LTR): from its first to its last strong letter,
        /// neutrals inside included, combining marks kept after their letter.</summary>
        static void ReverseRuns(List<char> chars, List<bool> tag, int dir)
        {
            int i = 0;
            while (i < chars.Count)
            {
                if (tag[i] || Direction(chars[i]) != dir) { i++; continue; }
                int start = i, last = i;
                for (int j = i + 1; j < chars.Count && !tag[j]; j++)
                {
                    var d = Direction(chars[j]);
                    if (d == dir || d == 0 && IsMark(chars[j])) last = j;
                    else if (d != 0) break;
                }
                ReverseClusters(chars, start, last);
                i = last + 1;
            }
        }

        static void ReverseClusters(List<char> chars, int start, int last)
        {
            if (last <= start) return;
            var clusters = new List<string>();
            var cur = new StringBuilder();
            for (int i = start; i <= last; i++)
            {
                var c = chars[i];
                if (cur.Length > 0 && !IsMark(c) && !char.IsLowSurrogate(c)) { clusters.Add(cur.ToString()); cur.Clear(); }
                cur.Append(c);
            }
            if (cur.Length > 0) clusters.Add(cur.ToString());
            int k = start;
            for (int n = clusters.Count - 1; n >= 0; n--) foreach (var c in clusters[n]) chars[k++] = c;
        }

        static readonly Dictionary<char, char> Mirror = new()
        {
            ['('] = ')', [')'] = '(', ['['] = ']', [']'] = '[', ['{'] = '}', ['}'] = '{', ['«'] = '»', ['»'] = '«',
        };

        // ---- Devanagari ---------------------------------------------------------------------------------------

        const char IMatra = 'ि', Nukta = '़', Virama = '्';
        static bool IsDevConsonant(char c) => c >= 0x0915 && c <= 0x0939 || c >= 0x0958 && c <= 0x095F || c >= 0x0978 && c <= 0x097F;

        /// <summary>Move each ि before the consonant cluster it follows (क्षि → ि + क्ष).</summary>
        static void ReorderDevanagari(List<char> chars, List<bool> tag)
        {
            for (int i = 1; i < chars.Count; i++)
            {
                if (chars[i] != IMatra || tag[i]) continue;
                int s = i - 1;
                if (chars[s] == Nukta && s > 0) s--;
                if (!IsDevConsonant(chars[s])) continue;
                // Back over "consonant (+nukta) + virama" pairs in front of it.
                while (s >= 2 && chars[s - 1] == Virama)
                {
                    int p = s - 2;
                    if (chars[p] == Nukta && p > 0) p--;
                    if (!IsDevConsonant(chars[p])) break;
                    s = p;
                }
                chars.RemoveAt(i); tag.RemoveAt(i);
                chars.Insert(s, IMatra); tag.Insert(s, false);
            }
        }

        // ---- Arabic -------------------------------------------------------------------------------------------

        // Letter → isolated, final, initial, medial forms (0 = no such form: joins only to the letter before it).
        static readonly Dictionary<char, (char iso, char fin, char ini, char med)> Forms = BuildForms();

        static Dictionary<char, (char, char, char, char)> BuildForms()
        {
            var d = new Dictionary<char, (char, char, char, char)>();
            void Dual(int c, int first) => d[(char)c] = ((char)first, (char)(first + 1), (char)(first + 2), (char)(first + 3));
            void Right(int c, int first) => d[(char)c] = ((char)first, (char)(first + 1), '\0', '\0');
            d['ء'] = ('ﺀ', '\0', '\0', '\0');   // hamza: never joins
            Right(0x0622, 0xFE81); Right(0x0623, 0xFE83); Right(0x0624, 0xFE85); Right(0x0625, 0xFE87);
            Dual(0x0626, 0xFE89); Right(0x0627, 0xFE8D); Dual(0x0628, 0xFE8F); Right(0x0629, 0xFE93);
            Dual(0x062A, 0xFE95); Dual(0x062B, 0xFE99); Dual(0x062C, 0xFE9D); Dual(0x062D, 0xFEA1); Dual(0x062E, 0xFEA5);
            Right(0x062F, 0xFEA9); Right(0x0630, 0xFEAB); Right(0x0631, 0xFEAD); Right(0x0632, 0xFEAF);
            Dual(0x0633, 0xFEB1); Dual(0x0634, 0xFEB5); Dual(0x0635, 0xFEB9); Dual(0x0636, 0xFEBD);
            Dual(0x0637, 0xFEC1); Dual(0x0638, 0xFEC5); Dual(0x0639, 0xFEC9); Dual(0x063A, 0xFECD);
            Dual(0x0641, 0xFED1); Dual(0x0642, 0xFED5); Dual(0x0643, 0xFED9); Dual(0x0644, 0xFEDD);
            Dual(0x0645, 0xFEE1); Dual(0x0646, 0xFEE5); Dual(0x0647, 0xFEE9); Right(0x0648, 0xFEED);
            Right(0x0649, 0xFEEF); Dual(0x064A, 0xFEF1);
            // Persian / Urdu letters (Presentation Forms-A).
            Dual(0x067E, 0xFB56); Dual(0x0679, 0xFB66); Dual(0x0686, 0xFB7A); Right(0x0688, 0xFB88); Right(0x0691, 0xFB8C);
            Right(0x0698, 0xFB8A); Dual(0x06A9, 0xFB8E); Dual(0x06AF, 0xFB92); Right(0x06BA, 0xFB9E);
            Dual(0x06BE, 0xFBAA); Dual(0x06C1, 0xFBA6); Right(0x06D2, 0xFBAE); Dual(0x06CC, 0xFBFC);
            return d;
        }

        const char Tatweel = 'ـ', Lam = 'ل';

        static bool JoinsBoth(char c) => c == Tatweel || Forms.TryGetValue(c, out var f) && f.med != '\0';
        static bool Joins(char c) => c == Tatweel || Forms.TryGetValue(c, out var f) && f.fin != '\0';
        static bool Transparent(char c) => c >= 0x064B && c <= 0x065F || c == 0x0670 || c >= 0x06D6 && c <= 0x06ED;

        static char LamAlef(char alef, bool final) => alef switch
        {
            'آ' => final ? 'ﻶ' : 'ﻵ',
            'أ' => final ? 'ﻸ' : 'ﻷ',
            'إ' => final ? 'ﻺ' : 'ﻹ',
            'ا' => final ? 'ﻼ' : 'ﻻ',
            _ => '\0',
        };

        static void JoinArabic(List<char> chars, List<bool> tag)
        {
            var src = chars.ToArray();
            var isTag = tag.ToArray();
            char Prev(int i) { for (int j = i - 1; j >= 0; j--) { if (isTag[j]) return '\0'; if (!Transparent(src[j])) return src[j]; } return '\0'; }
            char Next(int i, out int at)
            {
                for (int j = i + 1; j < src.Length; j++) { if (isTag[j]) break; if (!Transparent(src[j])) { at = j; return src[j]; } }
                at = -1; return '\0';
            }
            var skip = new bool[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                if (isTag[i] || skip[i] || !Forms.TryGetValue(src[i], out var f)) continue;
                var prev = Prev(i);
                bool fromRight = JoinsBoth(prev);
                var next = Next(i, out var nextAt);
                if (src[i] == Lam && nextAt == i + 1)
                {
                    var lig = LamAlef(next, fromRight);
                    if (lig != '\0') { chars[i] = lig; skip[nextAt] = true; continue; }
                }
                bool toLeft = f.med != '\0' && Joins(next);
                chars[i] = fromRight && toLeft ? f.med : fromRight && f.fin != '\0' ? f.fin : toLeft ? f.ini : f.iso;
            }
            for (int i = src.Length - 1; i >= 0; i--) if (skip[i]) { chars.RemoveAt(i); tag.RemoveAt(i); }
        }
    }
}
