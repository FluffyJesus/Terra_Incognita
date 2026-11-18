using System.Globalization;
using System.Linq;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Knobs of the PaintedRock shader (field name = shader property without "_"), saved as "name=value; …" in
    /// [Rocks] SculptedPaint and tuned live in the F4 window.
    /// </summary>
    internal class RockPaint
    {
        public float BlotchScale = 3.065f;
        public float Blotch = 0.357f;
        public float Detail = 0.255f;
        public float Hue = 0.642f;
        public float Occlusion = 0.806f;
        public float Cavity = 0.679f;
        public float Ridge = 0.154f;
        public float Wet = 0.343f;
        public float WetHeight = 0.429f;
        public float Sun = 0.221f;
        public float Cracks = 0.637f;
        public float CrackScale = 3.176f;
        public float CrackWidth = 0.04f;
        public float CrackShare = 0.863f;
        public float CrackFine = 0.561f;
        public float CrackLip = 0f;
        public float PlateTone = 0.05f;
        public float SizeFollow = 0.729f;
        public float CrackDark = 0.78f;
        public float CrackSoft = 0.321f;
        public float CrackFlat = 0.244f;
        public float Brightness = 0.5f;
        public float Glossiness = 0.04f;

        internal static readonly string[] Names = typeof(RockPaint).GetFields().Select(f => f.Name).ToArray();
        internal float Get(string n) => (float)typeof(RockPaint).GetField(n).GetValue(this);
        internal void Set(string n, float v) => typeof(RockPaint).GetField(n).SetValue(this, v);

        internal void ApplyTo(Material m)
        {
            foreach (var n in Names) if (m.HasProperty("_" + n)) m.SetFloat("_" + n, Get(n));
        }

        public override string ToString() =>
            string.Join("; ", Names.Select(n => n + "=" + Get(n).ToString("0.###", CultureInfo.InvariantCulture)).ToArray());

        internal static RockPaint Parse(string s)
        {
            var r = new RockPaint();
            foreach (var part in (s ?? "").Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                string n = kv[0].Trim();
                if (Names.Contains(n) && float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) r.Set(n, v);
            }
            return r;
        }
    }
}
