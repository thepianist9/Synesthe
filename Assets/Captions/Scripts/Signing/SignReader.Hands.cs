using TMPro;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Sign to speech shows what hand tracking sees: each tracked hand drawn as lines over the real one (the fingers
    /// from the wrist, and the knuckles), and above the signing hand what was just read: a sign (green), a letter
    /// (amber), or a movement it couldn't place with the sign it looked most like (grey, "? water"). The lines are
    /// faint outside signing space, blue inside it, amber while a handshape is held for a letter and green when a sign
    /// is read.
    /// </summary>
    public partial class SignReader
    {
        [Tooltip("Draw the tracked hands and what was read (sign to speech only).")]
        public bool showHands = true;
        [Tooltip("URP Unlit transparent material for the hand lines (the menu's panel material).")]
        public Material handMaterial;

        static readonly int[][] Chains =
        {
            new[] { 0, 1, 2, 3, 4 }, new[] { 0, 5, 6, 7, 8 }, new[] { 0, 9, 10, 11, 12 },
            new[] { 0, 13, 14, 15, 16 }, new[] { 0, 17, 18, 19, 20 }, new[] { 5, 9, 13, 17 },
        };
        static readonly Color Faint = new(1f, 1f, 1f, 0.3f), Ready = new(0.55f, 0.8f, 1f, 0.85f),
            Holding = new(1f, 0.75f, 0.3f, 0.95f), ReadOk = new(0.4f, 0.95f, 0.5f, 0.95f), Unsure = new(0.8f, 0.8f, 0.82f, 0.9f);
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"), BaseMapId = Shader.PropertyToID("_BaseMap");

        LineRenderer[][] _handLines;
        MaterialPropertyBlock _handMpb;
        TextMeshPro _readLabel;
        float _readAt = -9f, _okAt = -9f;
        Color _readColor;

        void BuildHands()
        {
            if (_handLines != null || handMaterial == null) return;
            _handMpb = new MaterialPropertyBlock();
            _handLines = new LineRenderer[2][];
            for (var h = 0; h < 2; h++)
            {
                _handLines[h] = new LineRenderer[Chains.Length];
                for (var c = 0; c < Chains.Length; c++)
                {
                    var go = new GameObject($"Tracked hand {(h == 0 ? "R" : "L")} {c}");
                    go.transform.SetParent(transform, false);
                    var lr = go.AddComponent<LineRenderer>();
                    lr.useWorldSpace = true;
                    lr.positionCount = Chains[c].Length;
                    lr.widthMultiplier = 0.0035f;
                    lr.numCapVertices = 2;
                    lr.numCornerVertices = 2;
                    lr.alignment = LineAlignment.View;
                    lr.sharedMaterial = handMaterial;
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    lr.enabled = false;
                    _handLines[h][c] = lr;
                }
            }
            var t = new GameObject("Read sign").AddComponent<TextMeshPro>();
            t.transform.SetParent(transform, false);
            if (font != null) t.font = font;
            t.fontSize = 0.32f;
            t.alignment = TextAlignmentOptions.Bottom;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.outlineWidth = 0.25f;
            t.outlineColor = new Color32(0, 0, 0, 200);
            t.rectTransform.sizeDelta = new Vector2(0.4f, 0.05f);
            t.text = "";
            _readLabel = t;
        }

        /// <summary>What the reader just made of a movement or handshape, shown above the signing hand.</summary>
        void ShowRead(string text, Color color, bool ok)
        {
            if (_readLabel == null) return;
            _readLabel.text = text;
            _readColor = color;
            _readAt = Time.time;
            if (ok) _okAt = Time.time;
        }

        void UpdateHands(bool on, Hand lead, in Body body)
        {
            if (_handLines == null) { if (on && showHands) BuildHands(); if (_handLines == null) return; }
            var show = on && showHands;
            for (var h = 0; h < 2; h++)
            {
                var hand = h == 0 ? _right : _left;
                var visible = show && hand.Valid;
                var color = !visible ? Faint
                    : !Up(hand, body) ? Faint
                    : hand == lead && Time.time - _okAt < 0.7f ? ReadOk
                    : hand == lead && _held != '\0' ? Holding
                    : Ready;
                for (var c = 0; c < Chains.Length; c++)
                {
                    var lr = _handLines[h][c];
                    lr.enabled = visible;
                    if (!visible) continue;
                    for (var i = 0; i < Chains[c].Length; i++) lr.SetPosition(i, hand.P[Chains[c][i]]);
                    lr.GetPropertyBlock(_handMpb);
                    _handMpb.SetColor(BaseColorId, color);
                    _handMpb.SetTexture(BaseMapId, Texture2D.whiteTexture);
                    lr.SetPropertyBlock(_handMpb);
                }
            }

            var age = Time.time - _readAt;
            var labelOn = show && age < 2f && _readLabel.text.Length > 0;
            _readLabel.gameObject.SetActive(labelOn);
            if (!labelOn) return;
            var anchor = lead != null ? lead.P[9] : _readLabel.transform.position;
            _readLabel.transform.position = anchor + Vector3.up * 0.09f;
            var toEye = _readLabel.transform.position - head.position;
            if (toEye.sqrMagnitude > 1e-4f) _readLabel.transform.rotation = Quaternion.LookRotation(toEye, Vector3.up);
            var c2 = _readColor;
            c2.a *= Mathf.Clamp01((2f - age) / 0.5f);
            _readLabel.color = c2;
        }

        void HideHands()
        {
            if (_handLines == null) return;
            foreach (var hand in _handLines) foreach (var lr in hand) lr.enabled = false;
            _readLabel.gameObject.SetActive(false);
        }
    }
}
