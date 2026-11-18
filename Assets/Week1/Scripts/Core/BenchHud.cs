using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// World-space text panel that lazily follows the head (only re-centres when you look
    /// more than 35° away), plus a caption line used by the speech tests.
    /// Built from legacy TextMesh so it needs no extra packages or font imports.
    /// </summary>
    public class BenchHud : MonoBehaviour
    {
        public Transform head;
        public float distance = 1.2f;

        [Tooltip("Unlit material for the panel background. Must be an asset so its shader ships in the build.")]
        public Material panelMaterial;
        [Tooltip("Text material (GUI/Text Shader). Must be an asset so its shader ships in the build.")]
        public Material textMaterial;

        TextMesh _title, _body, _caption, _footer;
        Transform _panel;
        bool _placed, _following;

        void Awake()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _panel = new GameObject("HUD Panel").transform;
            _panel.SetParent(transform, false);

            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(bg.GetComponent<Collider>());
            bg.name = "Background";
            bg.transform.SetParent(_panel, false);
            bg.transform.localPosition = new Vector3(0, -0.02f, 0.005f);
            bg.transform.localScale = new Vector3(0.95f, 0.62f, 1);
            // Shader.Find only works for shaders that are in the build; scene-referenced material assets are.
            var bgShader = panelMaterial != null ? panelMaterial.shader : Shader.Find("Universal Render Pipeline/Unlit");
            if (bgShader != null && bgShader.isSupported)
            {
                var mat = panelMaterial != null ? new Material(panelMaterial) : new Material(bgShader);
                mat.color = new Color(0.04f, 0.06f, 0.12f, 0.85f);
                bg.GetComponent<MeshRenderer>().sharedMaterial = mat;
            }
            else
            {
                Debug.LogWarning("[Bench] HUD panel shader missing from build — background hidden.");
                bg.SetActive(false);
            }

            _title = MakeText("Title", font, new Vector3(-0.44f, 0.27f, 0), 0.0105f, new Color(0.65f, 0.71f, 0.99f));
            _body = MakeText("Body", font, new Vector3(-0.44f, 0.21f, 0), 0.0075f, Color.white);
            _caption = MakeText("Caption", font, new Vector3(-0.44f, -0.17f, 0), 0.0095f, new Color(0.2f, 0.83f, 0.6f));
            _footer = MakeText("Footer", font, new Vector3(-0.44f, -0.28f, 0), 0.006f, new Color(0.6f, 0.62f, 0.67f));
        }

        TextMesh MakeText(string name, Font font, Vector3 pos, float size, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_panel, false);
            go.transform.localPosition = pos;
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = size;
            tm.anchor = TextAnchor.UpperLeft;
            tm.color = color;
            var mr = go.GetComponent<MeshRenderer>();
            if (textMaterial != null)
                mr.sharedMaterial = new Material(textMaterial) { mainTexture = font.material.mainTexture };
            else
                mr.sharedMaterial = font.material;
            return tm;
        }

        public void Set(string title, string body, string footer = null)
        {
            if (_title == null) { Debug.Log($"[Bench] {title}: {body}"); return; }
            _title.text = title;
            _body.text = body;
            if (footer != null) _footer.text = footer;
        }

        public void Caption(string text)
        {
            if (_caption != null) _caption.text = Wrap(text, 52);
        }

        static string Wrap(string s, int width)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= width) return s ?? "";
            var sb = new System.Text.StringBuilder();
            var line = 0;
            foreach (var word in s.Split(' '))
            {
                if (line + word.Length > width) { sb.Append('\n'); line = 0; }
                sb.Append(word).Append(' ');
                line += word.Length + 1;
            }
            return sb.ToString();
        }

        void LateUpdate()
        {
            if (head == null) return;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) return;
            var target = head.position + fwd * distance + Vector3.down * 0.1f;
            var toPanel = Vector3.ProjectOnPlane(_panel.position - head.position, Vector3.up).normalized;
            var firstFrame = !_placed;
            if (firstFrame || Vector3.Angle(fwd, toPanel) > 35f || Vector3.Distance(_panel.position, target) > 0.6f)
                _following = true;
            if (_following)
            {
                _panel.position = Vector3.Lerp(_panel.position, target, firstFrame ? 1f : 0.08f);
                if (Vector3.Distance(_panel.position, target) < 0.03f) _following = false;
            }
            _placed = true;
            _panel.rotation = Quaternion.LookRotation(_panel.position - head.position, Vector3.up);
        }
    }
}
