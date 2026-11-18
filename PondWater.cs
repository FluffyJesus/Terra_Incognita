using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// A pond's water surface: a flat fan mesh whose outline follows the pond (radius per angle, metres), so it can be
    /// irregular, plus a shared semi-transparent material with a procedural ripple normal map scrolled slowly.
    ///
    /// The game only knows the ocean (Crest) as water: PlayerSwimming.inShallowWater is set from the ocean height,
    /// and FootstepsAudio then plays the splashing "wet" steps on terrain. Standing in one of our ponds sets the same
    /// flag, so walking in gives the vanilla water steps.
    /// </summary>
    internal class PondWater : MonoBehaviour
    {
        internal static readonly List<PondWater> All = new List<PondWater>();
        private const int Segments = 64;
        private const float RippleTile = 1.5f;     // metres per ripple texture tile: fine, faint ripples

        private float[] radii;                     // per segment, metres (local, unscaled)

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        /// <summary>Create a water surface at <paramref name="centre"/> (world) with outline radius(angle) in metres.</summary>
        public static GameObject Create(Transform parent, Vector3 centre, System.Func<float, float> radius)
        {
            var go = new GameObject("pond water");
            go.transform.SetParent(parent, false);
            go.transform.position = centre;
            go.transform.rotation = Quaternion.identity;
            var ls = parent.lossyScale;
            go.transform.localScale = new Vector3(1f / ls.x, 1f / ls.y, 1f / ls.z);   // metres in world

            var w = go.AddComponent<PondWater>();
            w.radii = new float[Segments];
            for (int i = 0; i < Segments; i++) w.radii[i] = radius(i * Mathf.PI * 2f / Segments);
            go.AddComponent<MeshFilter>().sharedMesh = w.BuildMesh(centre);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Material();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>
        /// Make the surface a drinking-water source (click with a bottle). capacity 0 = endless.
        /// The clickable part is a separate, motionless object at the full water level: when the collider sank with
        /// the water during a click, the pointer lost it mid-click and the game dropped the bottle (spilling it).
        /// </summary>
        /// <summary>
        /// Ponds are carved before the scenery scenes are visited: once Fey Valley's lake material is in hand, every
        /// pond already made switches to it (all, including those on islands not switched on yet).
        /// </summary>
        public static void UseFeyWater()
        {
            if (!PropLibrary.FeyWater) return;
            mat = PropLibrary.FeyWater;
            feyStyle = true;
            int n = 0;
            foreach (var w in Resources.FindObjectsOfTypeAll<PondWater>())
            {
                if (!w || !w.gameObject.scene.IsValid()) continue;
                var mr = w.GetComponent<MeshRenderer>();
                if (mr) { mr.sharedMaterial = mat; n++; }
            }
            Plugin.Log.LogInfo($"Pond material: Fey Valley water [{mat.shader.name}] on {n} ponds");
        }

        public static void MakeSource(GameObject water, string id, float capacity, float drop)
        {
            var w = water.GetComponent<PondWater>();
            float r = 0f;
            foreach (var x in w.radii) r = Mathf.Max(r, x);
            var click = new GameObject("pond water (click)");
            click.transform.SetParent(water.transform.parent, false);
            click.transform.position = water.transform.position;
            click.transform.rotation = water.transform.rotation;
            click.transform.localScale = water.transform.localScale;
            click.AddComponent<MeshFilter>().sharedMesh = water.GetComponent<MeshFilter>().sharedMesh;
            var mr = click.AddComponent<MeshRenderer>();                 // GoPointerButton requires a Renderer; the
            mr.sharedMaterial = Material();                              // visible water is the other object
            mr.enabled = false;
            var box = click.AddComponent<BoxCollider>();                 // thin trigger at the surface: the pointer
            box.isTrigger = true;                                        // hits it, the player walks through it
            box.size = new Vector3(2f * r, 0.3f, 2f * r);
            box.center = new Vector3(0f, -0.1f, 0f);
            var src = click.AddComponent<WaterSource>();
            src.id = id;
            src.capacity = capacity;
            src.surface = water.transform;
            src.fullY = water.transform.localPosition.y;
            src.emptyY = src.fullY - drop / Mathf.Max(water.transform.lossyScale.y, 1e-4f);
        }
        private Mesh BuildMesh(Vector3 centre)
        {
            var v = new Vector3[Segments + 1]; var uv = new Vector2[Segments + 1]; var n = new Vector3[Segments + 1];
            var tri = new int[Segments * 3];
            // UVs in world metres (offset by the centre so neighbouring ponds don't share a pattern)
            Vector2 UV(Vector3 p) => new Vector2((p.x + centre.x) / RippleTile, (p.z + centre.z) / RippleTile);
            v[0] = Vector3.zero; uv[0] = UV(v[0]); n[0] = Vector3.up;
            for (int i = 0; i < Segments; i++)
            {
                float a = i * Mathf.PI * 2f / Segments;
                v[i + 1] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radii[i];
                uv[i + 1] = UV(v[i + 1]);
                n[i + 1] = Vector3.up;
                tri[i * 3] = 0; tri[i * 3 + 1] = i + 2 > Segments ? 1 : i + 2; tri[i * 3 + 2] = i + 1;   // clockwise from above
            }
            var m = new Mesh { name = "terra incognita pond", vertices = v, uv = uv, normals = n, triangles = tri };
            m.RecalculateTangents();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Is a point inside this pond's outline and below the surface (+ <paramref name="above"/>)?</summary>
        public bool Contains(Vector3 p, float above)
        {
            var d = p - transform.position;
            float dist = new Vector2(d.x, d.z).magnitude;
            float a = Mathf.Atan2(d.z, d.x);
            if (a < 0f) a += Mathf.PI * 2f;
            int i = Mathf.FloorToInt(a / (Mathf.PI * 2f) * Segments) % Segments;
            if (dist > radii[i]) return false;
            return d.y < above && d.y > -4f;
        }

        public static bool PlayerIn(Vector3 p)
        {
            foreach (var w in All) if (w && w.Contains(p, PlayerAbove)) return true;
            return false;
        }

        // FootstepsAudio sits ~1 m above the feet (it raycasts 1.25 m down for the ground)
        private const float PlayerAbove = 1.1f;

        // ---------------------------------------------------------------- material + ripples
        private static Material mat;
        private static bool feyStyle;
        private static int lastFrame = -1;

        private void Update()
        {
            if (!mat || feyStyle || Time.frameCount == lastFrame) return;      // once per frame for all ponds
            lastFrame = Time.frameCount;
            // Standard samples _BumpMap with _MainTex's tiling/offset, the detail normal with _DetailAlbedoMap's:
            // two layers drifting in different directions, so the pattern never just slides as one sheet
            float t = Time.time * Plugin.PondRippleSpeed.Value;
            mat.mainTextureOffset = new Vector2(t * 0.6f, t * 0.35f);
            mat.SetTextureOffset("_DetailAlbedoMap", new Vector2(-t * 0.45f, t * 0.7f));
        }

        /// <summary>
        /// Standard shader, "Transparent" mode (premultiplied: keeps sky reflections and the sun highlight while the
        /// bottom shows through) with a fixed opacity, and a tileable ripple normal map. Config [Ponds].
        /// </summary>
        internal static Material Material()
        {
            if (mat) return mat;
            var sh = Shader.Find("Standard");
            if (!sh) foreach (var s in Resources.FindObjectsOfTypeAll<Shader>()) if (s.name == "Standard") { sh = s; break; }
            mat = new Material(sh ? sh : Shader.Find("Legacy Shaders/Transparent/Diffuse")) { name = "terra incognita pond" };
            var col = new Color(0.16f, 0.42f, 0.45f, 0.55f);
            var parts = (Plugin.PondColor.Value ?? "").Split(',');
            for (int i = 0; i < 4 && i < parts.Length; i++)
                if (float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                    col[i] = f;
            mat.color = col;
            if (sh)
            {
                mat.SetFloat("_Mode", 3f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHABLEND_ON");
                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.SetFloat("_Glossiness", Plugin.PondSmoothness.Value);
                mat.SetFloat("_Metallic", 0f);
                if (Plugin.PondRippleStrength.Value > 0f)
                {
                    var ripples = RippleNormalMap();
                    mat.SetTexture("_BumpMap", ripples);
                    mat.SetFloat("_BumpScale", Plugin.PondRippleStrength.Value);
                    mat.EnableKeyword("_NORMALMAP");
                    // second, finer layer (detail normal map; neutral grey detail albedo = no colour change)
                    mat.SetTexture("_DetailNormalMap", ripples);
                    mat.SetFloat("_DetailNormalMapScale", Plugin.PondRippleStrength.Value * 0.7f);
                    mat.SetTexture("_DetailAlbedoMap", Grey());
                    mat.SetTextureScale("_DetailAlbedoMap", new Vector2(1.7f, 1.7f));
                    mat.EnableKeyword("_DETAIL_MULX2");
                }
                mat.renderQueue = 3000;
            }
            Plugin.Log.LogInfo($"Pond material: {mat.shader.name}, colour {col}, ripples {Plugin.PondRippleStrength.Value}");
            return mat;
        }

        private static Texture2D Grey()
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "pond detail grey" };
            var px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = new Color(0.5f, 0.5f, 0.5f, 1f);
            t.SetPixels(px); t.Apply(false, true);
            return t;
        }

        /// <summary>
        /// Tileable ripple normal map (128²): a sum of sine waves whose wave vectors are whole numbers of cycles per
        /// tile, so the pattern repeats seamlessly. RGB-encoded normal with alpha 1 (Standard's unpack handles it).
        /// </summary>
        private static Texture2D RippleNormalMap()
        {
            const int N = 128;
            var rng = new System.Random(7);
            var waves = new List<(float kx, float ky, float ph, float amp)>();
            // 24 waves in all directions, 3–11 cycles per tile, amplitude falling with frequency: a choppy, irregular
            // surface instead of the visible stripes 9 waves gave
            for (int i = 0; i < 24; i++)
            {
                int kx, ky; float k;
                do { kx = rng.Next(-11, 12); ky = rng.Next(-11, 12); k = Mathf.Sqrt(kx * kx + ky * ky); } while (k < 3f || k > 11f);
                waves.Add((kx, ky, (float)rng.NextDouble() * Mathf.PI * 2f, Mathf.Pow(k, -1.4f)));
            }
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true, true) { name = "pond ripples", wrapMode = TextureWrapMode.Repeat };
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = 0f, dy = 0f;
                    foreach (var w in waves)
                    {
                        float arg = Mathf.PI * 2f * (w.kx * x + w.ky * y) / N + w.ph;
                        float c = Mathf.Cos(arg) * w.amp * Mathf.PI * 2f / N;
                        dx += c * w.kx; dy += c * w.ky;
                    }
                    var nrm = new Vector3(-dx * 9f, -dy * 9f, 1f).normalized;
                    px[y * N + x] = new Color(nrm.x * 0.5f + 0.5f, nrm.y * 0.5f + 0.5f, nrm.z * 0.5f + 0.5f, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }
    }

    /// <summary>Set the shallow-water flag right before the footsteps decide which clips to play.</summary>
    [HarmonyPatch(typeof(FootstepsAudio), "Update")]
    internal static class PondFootstepsPatch
    {
        private static void Prefix(FootstepsAudio __instance)
        {
            if (PondWater.All.Count > 0 && PondWater.PlayerIn(__instance.transform.position))
                PlayerSwimming.inShallowWater = true;
        }
    }
}
