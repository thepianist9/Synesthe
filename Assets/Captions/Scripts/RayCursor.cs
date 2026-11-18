using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// The spot where a pointing ray meets the UI: a small dot, with a ring around it while it's on something you can
    /// press. Pinching (or squeezing the trigger) draws the ring in around the dot and turns it blue, so you see the
    /// press coming; when it lands, a ripple spreads out from the spot (<see cref="Ripple"/>). A soft glow sits under
    /// the dot, a little wider over something pressable and brighter as the pinch closes. Lies flat facing the
    /// viewer, a hair in front of what it touches. Shared by the mode menu and the microphone menu.
    /// </summary>
    public class RayCursor : MonoBehaviour
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"), BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly Color Idle = new(1f, 1f, 1f, 0.9f), Pressing = new(0.988f, 0.973f, 0.847f, 1f); // cream #FCF8D8: shows on orange buttons and on glass
        static Texture2D _dotTex, _ringTex, _glowTex;

        MeshRenderer _dot, _ring, _ripple, _glow;
        MaterialPropertyBlock _mpb;
        float _onTarget, _rippleAge = 1f;
        Vector3 _rippleAt, _rippleNormal;

        public static RayCursor Create(string name, Transform parent, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<RayCursor>();
            c._mpb = new MaterialPropertyBlock();
            _dotTex ??= Disc(64, 0f);
            _ringTex ??= Disc(128, 0.72f);
            _glowTex ??= Glow(64);
            c._glow = c.Quad("Glow", material, _glowTex);
            c._glow.sortingOrder = 29; // under the dot
            c._dot = c.Quad("Dot", material, _dotTex);
            c._ring = c.Quad("Ring", material, _ringTex);
            c._ripple = c.Quad("Ripple", material, _ringTex);
            c._ripple.transform.SetParent(parent, false); // stays where the press landed
            c._ripple.enabled = false;
            c.Hide();
            return c;
        }

        /// <summary>Put the spot at <paramref name="point"/>, facing <paramref name="viewer"/>. onTarget: over something
        /// pressable. pinch: 0 (open) to 1 (pressed).</summary>
        public void Show(Vector3 point, Vector3 viewer, bool onTarget, float pinch)
        {
            _dot.enabled = _ring.enabled = _glow.enabled = true;
            var toViewer = (viewer - point).normalized;
            transform.SetPositionAndRotation(point + toViewer * 0.002f, Quaternion.LookRotation(-toViewer, Vector3.up));
            // Nearer is smaller on the retina's terms: keep the spot about the same apparent size.
            transform.localScale = Vector3.one * Mathf.Clamp(Vector3.Distance(point, viewer) / 0.7f, 0.6f, 2.5f);
            _onTarget = Mathf.MoveTowards(_onTarget, onTarget ? 1f : 0f, Time.deltaTime * 8f);
            pinch = Mathf.Clamp01(pinch);
            var color = Color.Lerp(Idle, Pressing, pinch);
            Set(_dot, Mathf.Lerp(0.006f, 0.011f, pinch), color);
            Set(_glow, Mathf.Lerp(0.024f, 0.034f, _onTarget) * Mathf.Lerp(1f, 0.8f, pinch),
                new Color(color.r, color.g, color.b, Mathf.Lerp(0.16f, 0.26f, _onTarget) + 0.2f * pinch));
            var ring = Mathf.Lerp(0.024f, 0.014f, pinch) * Mathf.Lerp(0.7f, 1f, _onTarget);
            Set(_ring, ring, new Color(color.r, color.g, color.b, color.a * _onTarget));
        }

        // Hidden, the spot only stops drawing: a ripple already under way still plays out.
        public void Hide()
        {
            _dot.enabled = _ring.enabled = _glow.enabled = false;
            _onTarget = 0f;
        }

        /// <summary>A press landed here: a ring spreads out and fades.</summary>
        public void Ripple()
        {
            _rippleAt = transform.position;
            _rippleNormal = transform.forward;
            _rippleAge = 0f;
        }

        void LateUpdate()
        {
            _rippleAge += Time.deltaTime;
            const float life = 0.35f;
            _ripple.enabled = _rippleAge < life;
            if (!_ripple.enabled) return;
            var t = _rippleAge / life;
            _ripple.transform.SetPositionAndRotation(_rippleAt, Quaternion.LookRotation(_rippleNormal, Vector3.up));
            var size = Mathf.Lerp(0.014f, 0.05f, 1f - (1f - t) * (1f - t)) * transform.localScale.x;
            Set(_ripple, size, new Color(Pressing.r, Pressing.g, Pressing.b, 0.9f * (1f - t)));
        }

        void Set(MeshRenderer r, float size, Color color)
        {
            r.transform.localScale = Vector3.one * size;
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, color);
            r.SetPropertyBlock(_mpb);
        }

        MeshRenderer Quad(string name, Material material, Texture2D tex)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = name;
            q.transform.SetParent(transform, false);
            var r = q.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.sortingOrder = 30; // over the panels and their text
            r.GetPropertyBlock(_mpb);
            _mpb.SetTexture(BaseMapId, tex);
            r.SetPropertyBlock(_mpb);
            return r;
        }

        // A soft round glow: bright in the middle, falling off smoothly to nothing at the edge.
        static Texture2D Glow(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2((x + 0.5f) / size - 0.5f, (y + 0.5f) / size - 0.5f).magnitude * 2f;
                var a = Mathf.Exp(-d * d * 5f) * Mathf.Clamp01(1f - d);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // A white disc with a soft edge; inner > 0 cuts a hole (a ring), as a fraction of the radius.
        static Texture2D Disc(int size, float inner)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            var edge = 1.5f / size;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2((x + 0.5f) / size - 0.5f, (y + 0.5f) / size - 0.5f).magnitude * 2f;
                var a = Mathf.Clamp01((1f - d) / (2 * edge));
                if (inner > 0) a *= Mathf.Clamp01((d - inner) / (2 * edge));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
