using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.TextCore.LowLevel;

namespace MetaStarter.Captions.Editor
{
    /// <summary>
    /// Makes the in-headset UI sharp: full render scale on Quest, icon textures that keep their edges when small,
    /// and bigger, finer text atlases (Inter SemiBold with a real Inter Bold for &lt;b&gt;), plus the filled icon font
    /// (Material Symbols Rounded, Apache 2.0). Rerunnable: Tools ▸ Captions ▸ UI quality.
    /// </summary>
    public static class UiQuality
    {
        const string Fonts = "Assets/Captions/Fonts/";
        const string TextFont = Fonts + "Inter UI SDF.asset", BoldFont = Fonts + "Inter UI Bold SDF.asset", IconFont = Fonts + "Icons SDF.asset";
        const string OldFont = Fonts + "Inter-SemiBold SDF.asset";

        /// <summary>The icon glyphs in Assets/Captions/Fonts/MaterialSymbolsRounded-Fill.ttf (a filled, weight-500 subset).</summary>
        public const string IconChars =
            "" +
            "" +
            // translate, mic off, record, stop, pause, language, history, timer, schedule, bug, chevron right, expand,
            // logout, subtitles, stop circle, remove, add, visibility, timelapse
            "\ue8e2\ue02b\ue061\ue047\ue034\ue894\ue889\ue425\ue8b5\ue868\ue5cc\ue5cf\ue9ba\ue048\uef71\ue15b\ue145\ue8f4\ue422" +
            // alerts (SoundEvents): smoke detector, fire, siren, doorbell, door, ringing phone, alarm clock, child,
            // glass break, dog barking, emergency home, crisis alert, warning
            "\ue285\uef55\uf3a7\uefff\ueffd\uf0dd\ue855\ueb41\uf14a\uf149\ue82a\uebe9\uf083";

        public static TMP_FontAsset TextFontAsset() => AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TextFont);
        public static TMP_FontAsset IconFontAsset() => AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(IconFont);

        [MenuItem("Tools/Captions/UI quality")]
        public static string Apply()
        {
            var log = new System.Text.StringBuilder();

            // 1. Render the eye buffers at full resolution (was 0.8: the whole UI was drawn small and stretched).
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (rp == null || rp.renderScale >= 1f) continue;
                log.AppendLine($"{path}: render scale {rp.renderScale} → 1");
                rp.renderScale = 1f;
                EditorUtility.SetDirty(rp);
            }

            // 2. Icons: sharper mip filter, alpha coverage kept in small mips (thin strokes don't fade), trilinear +
            //    anisotropic, ASTC 4x4 on Quest.
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Captions/UI/Icons" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is not TextureImporter ti) continue;
                ti.mipmapEnabled = true;
                ti.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
                ti.mipMapsPreserveCoverage = true;
                ti.alphaTestReferenceValue = 0.5f;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 8;
                ti.alphaIsTransparency = true;
                var android = ti.GetPlatformTextureSettings("Android");
                android.overridden = true;
                android.maxTextureSize = 1024;
                android.format = TextureImporterFormat.ASTC_4x4;
                ti.SetPlatformTextureSettings(android);
                ti.SaveAndReimport();
                log.AppendLine($"{path}: Kaiser mips, coverage, trilinear ×8, ASTC 4x4");
            }

            // 3. Fonts: 2048² atlases sampled at 110 pt with wide padding, instead of 1024² at 90.
            var bold = MakeFont(BoldFont, Fonts + "Inter-Bold.ttf", 2048, 110, 14, Ascii, log);
            var text = MakeFont(TextFont, Fonts + "Inter-SemiBold.ttf", 2048, 110, 14, Ascii, log);
            if (text != null && bold != null)
            {
                // <b> uses the real Bold face instead of a thickened SemiBold.
                text.fontWeightTable[7].regularTypeface = bold;
                bold.fontWeightTable[7].regularTypeface = bold;
                EditorUtility.SetDirty(text);
            }
            var icons = MakeFont(IconFont, Fonts + "MaterialSymbolsRounded-Fill.ttf", 1024, 128, 16, IconChars, log, staticAtlas: true);

            // 4. Point everything in the open scene at the new fonts.
            var old = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(OldFont);
            var swapped = 0;
            foreach (var c in Object.FindObjectsByType<Component>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c == null || c is Transform) continue;
                var so = new SerializedObject(c);
                var it = so.GetIterator();
                var changed = false;
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (old != null && it.objectReferenceValue == old) { it.objectReferenceValue = text; changed = true; swapped++; }
                    else if (it.name == "iconFont" && it.objectReferenceValue != icons) { it.objectReferenceValue = icons; changed = true; swapped++; }
                }
                if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            }
            log.AppendLine($"scene references updated: {swapped}");
            if (swapped > 0) EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            return log.ToString();
        }

        /// <summary>Recuts the icon atlas from the font file with every <see cref="IconChars"/> glyph, in place (references to it stay good).</summary>
        [MenuItem("Tools/Captions/Add new icon glyphs")]
        public static string AddIconGlyphs()
        {
            var fa = IconFontAsset();
            if (fa == null) return "no icon font";
            var before = fa.characterTable.Count;
            fa.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            // From scratch: the glyphs already there were cut from the old font file, whose glyph numbers differ.
            fa.ClearFontAssetData(true);
            fa.TryAddCharacters(IconChars, out var missing);
            fa.atlasPopulationMode = AtlasPopulationMode.Static;
            EditorUtility.SetDirty(fa);
            AssetDatabase.SaveAssets();
            var msg = $"icon glyphs {before} → {fa.characterTable.Count}" + (string.IsNullOrEmpty(missing) ? "" : $", missing {missing.Length}");
            Debug.Log("[UiQuality] " + msg);
            return msg;
        }

        const string Ascii = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
                             "·–—‘’“”…×‹›°•äöüÄÖÜßéèêáàâíóúñç€";

        static TMP_FontAsset MakeFont(string path, string ttf, int atlas, int samplePt, int padding, string chars,
            System.Text.StringBuilder log, bool staticAtlas = false)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(ttf);
            if (font == null) { log.AppendLine($"missing {ttf}"); return null; }
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path) != null) AssetDatabase.DeleteAsset(path);
            var fa = TMP_FontAsset.CreateFontAsset(font, samplePt, padding, GlyphRenderMode.SDFAA, atlas, atlas,
                AtlasPopulationMode.Dynamic, true);
            fa.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(fa, path);
            fa.atlasTextures[0].name = fa.name + " Atlas";
            AssetDatabase.AddObjectToAsset(fa.atlasTextures[0], fa);
            fa.material.name = fa.name + " Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            fa.TryAddCharacters(chars, out var missing);
            if (staticAtlas) fa.atlasPopulationMode = AtlasPopulationMode.Static;
            EditorUtility.SetDirty(fa);
            AssetDatabase.SaveAssets();
            log.AppendLine($"{path}: {fa.characterTable.Count} glyphs, {atlas}² @ {samplePt}pt" +
                           (string.IsNullOrEmpty(missing) ? "" : $", missing {missing.Length}"));
            return fa;
        }
    }
}
