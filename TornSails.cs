using System.Collections.Generic;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>Makes a wreck's rag live cloth once it is active (see TornSails.Hang).</summary>
    internal class RagHanger : MonoBehaviour
    {
        private void Start()
        {
            TornSails.Hang(gameObject);
            Destroy(this);
        }
    }

    /// <summary>
    /// Torn sails on wrecks, drawn the way Sail Damage draws a sail torn at 100 %: the whole canvas stays a smooth
    /// mesh; the torn-away lower part and the ragged holes are cut out of the canvas texture (alpha + double-sided
    /// cutout shader), with a frayed noise edge. Where Sail Damage keeps the sail on its rig, a wreck's sail is a copy:
    ///   • shape: the sail's flat rest mesh (renderer scale), placed so its head lies along the yard where the live
    ///     sail's head is (line fit of the head vertices, rest → as drawn), hanging down;
    ///   • tear line: "depth below the yard" per vertex, fitted linearly over the UVs (as Sail Damage fits height),
    ///     so the texture knows which texels are below the tear;
    ///   • cloth: head particles pinned, the rest free under gravity, a light breeze and gusts.
    /// </summary>
    internal static class TornSails
    {
        private const int Size = 256;
        private static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
        private static readonly Dictionary<Mesh, bool[]> pins = new Dictionary<Mesh, bool[]>();
        private static Shader cutout;
        private static bool searched;

        // Sail Damage uses the game's double-sided cutout ("Ciconia Studio/Double Sided/Transparent/Diffuse Bump Cutout")
        private static Shader Cutout()
        {
            if (searched) return cutout;
            searched = true;
            foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
                if (s && s.name.EndsWith("Double Sided/Transparent/Diffuse Bump Cutout")) { cutout = s; break; }
            if (!cutout) cutout = Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse");
            Plugin.Debug($"Torn sails: shader {(cutout ? cutout.name : "none")}");
            return cutout;
        }

        /// <param name="depth">depth below the yard over the UVs: d(u,v) = x·u + y·v + z (0 head … 1 foot)</param>
        public static Material Make(Material src, int seed, Vector4 depth)
        {
            string key = src.name + "#" + seed;
            if (cache.TryGetValue(key, out var m) && m) return m;
            var sh = Cutout();
            m = new Material(src) { name = src.name + " (torn)" };
            if (sh) m.shader = sh;
            m.mainTexture = Bake(src, new System.Random(seed), depth);
            m.mainTextureScale = Vector2.one;      // tiling is baked in
            m.mainTextureOffset = Vector2.zero;
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            var c = src.HasProperty("_Color") ? src.color : Color.white;
            m.color = Color.Lerp(c, new Color(0.62f, 0.58f, 0.5f), 0.35f) * 0.92f;   // sun-bleached, grimy
            cache[key] = m;
            return m;
        }

        private static Texture2D Bake(Material src, System.Random rng, Vector4 depth)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true) { name = "torn canvas", wrapMode = TextureWrapMode.Clamp };
            var px = new Color[Size * Size];
            var main = src.HasProperty("_MainTex") ? src.mainTexture : null;
            if (main)
            {
                // copy the canvas (with its tiling) through the GPU: game textures aren't CPU-readable
                var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32);
                var prev = RenderTexture.active;
                Graphics.Blit(main, rt, src.mainTextureScale, src.mainTextureOffset);
                RenderTexture.active = rt;
                var tmp = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                tmp.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                px = tmp.GetPixels();
                Object.Destroy(tmp);
            }
            else for (int i = 0; i < px.Length; i++) px[i] = Color.white;

            // torn below `keep` (fraction of the depth kept from the head) with a frayed edge; holes in the rest
            float ox = (float)rng.NextDouble() * 100f, oz = (float)rng.NextDouble() * 100f;
            float keep = depth.w + ((float)rng.NextDouble() - 0.5f) * 0.06f;
            float holeAmt = Mathf.Lerp(0.62f, 0.7f, (float)rng.NextDouble());
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float u = x / (float)Size, v = y / (float)Size;
                    float d = depth.x * u + depth.y * v + depth.z;
                    float edge = keep + (Mathf.PerlinNoise(u * 7f + ox, v * 7f + oz) - 0.5f) * 0.3f
                                      + (Mathf.PerlinNoise(u * 45f + ox, v * 45f + 3f) - 0.5f) * 0.07f;
                    float holes = Mathf.PerlinNoise(u * 6f + ox, v * 6f + oz) * 0.65f + Mathf.PerlinNoise(u * 19f + oz, v * 19f + ox) * 0.35f;
                    bool gone = d > edge || (d > 0.1f && holes > holeAmt);
                    var c = px[y * Size + x];
                    c.a = gone ? 0f : 1f;
                    px[y * Size + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply(true, false);
            return tex;
        }

        // ------------------------------------------------------------------ the rag (shape + cloth)

        /// <summary>
        /// The sail as rigged and set, in wreck template space: the cloth mesh skinned by its own bones with the reef
        /// animation at frame 0 (= sail set; ReefEffectAnimUniversal plays "1 - currentUnroll"), exactly where the game
        /// draws it on the yard. Pins = the game's own pinned cloth particles that lie on a yard (the head laced to
        /// the yard); its sheet corners are let go.
        /// </summary>
        public static Mesh Rag(Sail sail, SkinnedMeshRenderer smr, Matrix4x4 worldToTpl, List<(Vector3 a, Vector3 b)> yards,
                               out Vector4 depth, out string why)
        {
            depth = Vector4.zero; why = null;
            var mesh = smr.sharedMesh;
            if (!mesh || !mesh.isReadable) { why = "mesh not readable"; return null; }
            var X = RigPose(sail, smr, worldToTpl);
            int n = X.Length;
            var uv = mesh.uv; var tri = mesh.triangles;
            if (n < 4 || uv.Length != n) { why = $"{n} vertices, {uv.Length} uv"; return null; }

            // the game's pins (maxDistance ~0), particles = welded vertices in order of first appearance
            var gamePin = new bool[n];
            var cloth = sail ? sail.cloth : smr.GetComponent<Cloth>();
            if (cloth)
            {
                var co = cloth.coefficients;
                var rv = mesh.vertices;
                var idx = new int[n]; var seen = new Dictionary<Vector3, int>();
                for (int i = 0; i < n; i++) { if (!seen.TryGetValue(rv[i], out int p)) { p = seen.Count; seen[rv[i]] = p; } idx[i] = p; }
                bool welded = co.Length == seen.Count, direct = co.Length == n;
                for (int i = 0; i < n; i++)
                {
                    int p = welded ? idx[i] : direct ? i : -1;
                    if (p >= 0 && co[p].maxDistance < 0.01f) gamePin[i] = true;
                }
            }

            // Head = the sail's whole upper edge laced along its yard (the game pins only a few points; the lacing
            // holds the rest). The yard: the one carrying the game pins, else the highest one the sail touches.
            // Edge = boundary vertices of the mesh within 1.2 m of the yard's chord (yards are bent, so a tight
            // distance caught only the ends), plus the game pins on it; the outermost 1–2 at each end hang free.
            var edgeCount = new Dictionary<long, int>();
            for (int k = 0; k + 2 < tri.Length; k += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = tri[k + e], b = tri[k + (e + 1) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    edgeCount[key] = edgeCount.TryGetValue(key, out int c) ? c + 1 : 1;
                }
            var boundary = new bool[n];
            foreach (var kv in edgeCount) if (kv.Value == 1) { boundary[(int)(kv.Key >> 32)] = true; boundary[(int)(kv.Key & 0xffffffff)] = true; }

            int yi = -1, bestPins = 0; float bestY = float.MinValue;
            for (int k = 0; k < yards.Count; k++)
            {
                var y = yards[k];
                int pinsOn = 0; bool touches = false;
                for (int i = 0; i < n; i++)
                {
                    float d = DistToSegment(X[i], y.a, y.b);
                    if (gamePin[i] && d < 0.6f) pinsOn++;
                    if (boundary[i] && d < 1.2f) touches = true;
                }
                float my = (y.a.y + y.b.y) * 0.5f;
                if (pinsOn > bestPins || (pinsOn == bestPins && touches && my > bestY)) { yi = k; bestPins = pinsOn; bestY = my; }
            }
            var head = new List<int>();
            string src = "yard edge";
            if (yi >= 0)
            {
                var y = yards[yi];
                var ax = (y.b - y.a).normalized;
                for (int i = 0; i < n; i++)
                {
                    float d = DistToSegment(X[i], y.a, y.b);
                    if ((boundary[i] && d < 1.2f) || (gamePin[i] && d < 0.6f)) head.Add(i);
                }
                // The yard is bent, its chord is straight: the edge points found so far (the middle) give the bend —
                // offset from the chord as a parabola along the yard — and every boundary point within 0.45 m of that
                // curve, over the whole yard length, joins the head (the ends were missed before).
                // grown in passes: each fit reaches a little further along the yard (one pass stopped at the mast)
                for (int pass = 0; pass < 4 && head.Count >= 4; pass++)
                {
                    float yl = (y.b - y.a).magnitude;
                    double s0 = head.Count, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
                    var r0 = Vector3.zero; var r1 = Vector3.zero; var r2 = Vector3.zero;
                    foreach (int i in head)
                    {
                        float s = Vector3.Dot(X[i] - y.a, ax) / yl;
                        var off = X[i] - y.a - ax * (s * yl);
                        s1 += s; s2 += s * s; s3 += s * s * s; s4 += s * s * s * s;
                        r0 += off; r1 += off * s; r2 += off * (s * s);
                    }
                    // normal equations [s0 s1 s2; s1 s2 s3; s2 s3 s4] c = r (per component)
                    double detQ = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
                    if (System.Math.Abs(detQ) > 1e-9)
                    {
                        Vector3 Solve(Vector3 a0, Vector3 a1, Vector3 a2, int col)
                        {
                            // Cramer: replace column `col`
                            var res = Vector3.zero;
                            for (int c = 0; c < 3; c++)
                            {
                                double b0 = a0[c], b1 = a1[c], b2 = a2[c];
                                double m00 = s0, m01 = s1, m02 = s2, m10 = s1, m11 = s2, m12 = s3, m20 = s2, m21 = s3, m22 = s4;
                                if (col == 0) { m00 = b0; m10 = b1; m20 = b2; } else if (col == 1) { m01 = b0; m11 = b1; m21 = b2; } else { m02 = b0; m12 = b1; m22 = b2; }
                                double d = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
                                res[c] = (float)(d / detQ);
                            }
                            return res;
                        }
                        Vector3 q0 = Solve(r0, r1, r2, 0), q1 = Solve(r0, r1, r2, 1), q2 = Solve(r0, r1, r2, 2);
                        var set = new HashSet<int>(head);
                        for (int i = 0; i < n; i++)
                        {
                            if (!boundary[i] || set.Contains(i)) continue;
                            float s = Vector3.Dot(X[i] - y.a, ax) / yl;
                            if (s < -0.05f || s > 1.05f) continue;
                            var curve = y.a + ax * (s * yl) + q0 + q1 * s + q2 * (s * s);
                            if ((X[i] - curve).magnitude < 0.55f) { head.Add(i); set.Add(i); }
                        }
                    }
                }
                // To the corners: walk the mesh's outline (vertices welded by position, so UV seams don't break it)
                // from the head found so far, both ways, while it runs on smoothly; a turn over 35° is the sail's
                // corner (peak / tack), the last point laced. The parabola still lost the upper end of a bent yard.
                var wid = new int[n]; var weldPos = new List<Vector3>(); var wmap = new Dictionary<Vector3, int>();
                for (int i = 0; i < n; i++) { if (!wmap.TryGetValue(X[i], out int w)) { w = weldPos.Count; wmap[X[i]] = w; weldPos.Add(X[i]); } wid[i] = w; }
                var wEdges = new Dictionary<long, int>();
                for (int k = 0; k + 2 < tri.Length; k += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = wid[tri[k + e]], b = wid[tri[k + (e + 1) % 3]];
                        if (a == b) continue;
                        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                        wEdges[key] = wEdges.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                var adj = new Dictionary<int, List<int>>();
                foreach (var kv in wEdges)
                {
                    if (kv.Value != 1) continue;
                    int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                    if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<int>(); la.Add(b);
                    if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<int>(); lb.Add(a);
                }
                var headW = new HashSet<int>(); foreach (int i in head) headW.Add(wid[i]);
                var laced = new HashSet<int>(headW);
                foreach (int start in headW)
                {
                    if (!adj.TryGetValue(start, out var nbs)) continue;
                    foreach (int first in nbs)
                    {
                        int prev = start, cur = first;
                        for (int step = 0; step < 400; step++)
                        {
                            laced.Add(cur);
                            if (!adj.TryGetValue(cur, out var next)) break;
                            int nx = -1;
                            foreach (int c in next) if (c != prev) { nx = c; break; }
                            if (nx < 0) break;
                            var d1 = weldPos[cur] - weldPos[prev]; var d2 = weldPos[nx] - weldPos[cur];
                            if (Vector3.Angle(d1, d2) > 35f) break;            // a corner: stop, it is laced
                            // only along the yard: points drifting off it (the leech/foot) stop the walk
                            if (DistToSegment(weldPos[nx], y.a, y.b) > 2.5f) break;
                            if (d2.sqrMagnitude > 1e-6f && Mathf.Abs(Vector3.Dot(d2.normalized, ax)) < 0.75f) break;   // turning off the yard
                            prev = cur; cur = nx;
                        }
                    }
                }
                head.Clear();
                for (int i = 0; i < n; i++) if (laced.Contains(wid[i])) head.Add(i);
            }
            if (head.Count < 2)
            {
                src = "top";
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < n; i++) { lo = Mathf.Min(lo, X[i].y); hi = Mathf.Max(hi, X[i].y); }
                if (hi - lo < 0.3f) { why = $"height span {hi - lo:0.00} m"; return null; }
                head.Clear();
                for (int i = 0; i < n; i++) if ((X[i].y - lo) / (hi - lo) > 0.92f) head.Add(i);
            }

            // depth below the head line, per vertex
            var c0 = Mean(X, head); var dir = Axis(X, head, c0);
            var depthV = new float[n]; float dMax = 0f;
            for (int i = 0; i < n; i++)
            {
                var w = X[i] - c0; w -= dir * Vector3.Dot(w, dir);
                depthV[i] = w.magnitude; dMax = Mathf.Max(dMax, depthV[i]);
            }
            if (dMax < 0.3f) { why = $"sail depth {dMax:0.00} m"; return null; }
            // least squares d = a·u + b·v + c over the vertices (normalised depth), as Sail Damage fits height
            double su = 0, sv = 0, suu = 0, svv = 0, suv = 0, sd = 0, sud = 0, svd = 0;
            for (int i = 0; i < n; i++)
            {
                double u = uv[i].x, w = uv[i].y, d = depthV[i] / dMax;
                su += u; sv += w; suu += u * u; svv += w * w; suv += u * w; sd += d; sud += u * d; svd += w * d;
            }
            double det = suu * (svv * n - sv * sv) - suv * (suv * n - sv * su) + su * (suv * sv - svv * su);
            if (System.Math.Abs(det) < 1e-12) { why = "degenerate uv"; return null; }
            double fa = (sud * (svv * n - sv * sv) - suv * (svd * n - sv * sd) + su * (svd * sv - svv * sd)) / det;
            double fb = (suu * (svd * n - sd * sv) - sud * (suv * n - sv * su) + su * (suv * sd - svd * su)) / det;
            double fc = (suu * (svv * sd - svd * sv) - suv * (suv * sd - svd * su) + sud * (suv * sv - svv * su)) / det;
            // tear line by area: keep the 35–60 % of the canvas nearest the yard (vertices ≈ evenly spread)
            var sorted = new float[n]; for (int i = 0; i < n; i++) sorted[i] = depthV[i] / dMax;
            System.Array.Sort(sorted);
            float keepFrac = Mathf.Lerp(0.35f, 0.6f, Mathf.Abs(mesh.name.GetHashCode() ^ n) % 1000 / 1000f);
            float keep = sorted[Mathf.Clamp((int)(keepFrac * n), 0, n - 1)];
            depth = new Vector4((float)fa, (float)fb, (float)fc, keep);
            Vector2 uMin = uv[0], uMax = uv[0];
            for (int i = 1; i < n; i++) { uMin = Vector2.Min(uMin, uv[i]); uMax = Vector2.Max(uMax, uv[i]); }
            Plugin.Debug($"Torn sails: {mesh.name}: head {head.Count}/{n} by {src}, depth {dMax:0.0} m, " +
                               $"uv {uMin}–{uMax}, fit d = {fa:0.00}u + {fb:0.00}v + {fc:0.00}, keep {keep:0.00}");

            var m = new Mesh { name = mesh.name + " (rag)" };
            m.vertices = X; m.uv = uv; m.triangles = tri;
            m.RecalculateNormals(); m.RecalculateBounds();
            var pin = new bool[n]; foreach (int i in head) pin[i] = true;
            pins[m] = pin;
            return m;
        }

        /// <summary>
        /// Put a sail in its "set" pose (currentUnroll = 1) for copying, whatever reefs it: the armature clip at frame 0
        /// (ReefEffectAnimUniversal plays 1 - currentUnroll), scale-reefed parts back to full size
        /// (ReefEffectScaleUniversal), the square boom down and its cloth unflattened (ReefEffectSquareBoom). Furled in
        /// port, a square sail came out crumpled. Returns the action that puts everything back.
        /// </summary>
        public static System.Action SetSailSet(Sail sail)
        {
            var saved = new List<(Transform t, Vector3 p, Quaternion r, Vector3 s)>();
            void Save(Transform t) { if (t) saved.Add((t, t.localPosition, t.localRotation, t.localScale)); }
            if (!sail) return () => { };
            try
            {
                var reef = sail.GetComponent<ReefEffectAnimUniversal>();
                var anim = !reef ? null : reef.overrideAnimator ? reef.overrideAnimator : reef.transform.parent ? reef.transform.parent.GetComponent<Animator>() : null;
                var smr = sail.cloth ? sail.cloth.GetComponent<SkinnedMeshRenderer>() : null;
                if (smr && smr.bones != null) foreach (var b in smr.bones) Save(b);
                if (anim) foreach (var t in anim.GetComponentsInChildren<Transform>(true)) Save(t);
                foreach (var sc in sail.GetComponentsInChildren<ReefEffectScaleUniversal>(true)) Save(sc.transformToScale);
                var booms = sail.GetComponentsInChildren<ReefEffectSquareBoom>(true);
                foreach (var bm in booms) { Save(bm.transform); }
                if (sail.cloth) Save(sail.cloth.transform);

                if (anim)
                {
                    var clips = anim.runtimeAnimatorController ? anim.runtimeAnimatorController.animationClips : null;
                    string name = string.IsNullOrEmpty(reef.clipName) ? "ArmatureAction" : reef.clipName;
                    var clip = clips == null ? null : System.Array.Find(clips, c => c && c.name == name) ?? (clips.Length > 0 ? clips[0] : null);
                    if (clip) clip.SampleAnimation(anim.gameObject, 0f);       // frame 0 = sail set
                }
                foreach (var sc in sail.GetComponentsInChildren<ReefEffectScaleUniversal>(true))
                    if (sc.transformToScale) sc.transformToScale.localScale = Vector3.one;
                var downF = HarmonyLib.AccessTools.Field(typeof(ReefEffectSquareBoom), "downPosition");
                foreach (var bm in booms)
                {
                    // downPosition = the boom's localPosition at Start (the set position); not started → already there
                    if (downF != null && downF.GetValue(bm) is Vector3 down && down != Vector3.zero) bm.transform.localPosition = down;
                    var s = bm.transform.localScale; s.x = 1f; bm.transform.localScale = s;
                    if (sail.cloth) { var cs = sail.cloth.transform.localScale; cs.y = 1f; cs.z = 1f; sail.cloth.transform.localScale = cs; }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Torn sails: set pose of {sail.name}: {e.Message}"); }
            return () => { foreach (var s in saved) if (s.t) { s.t.localPosition = s.p; s.t.localRotation = s.r; s.t.localScale = s.s; } };
        }

        // Skin the cloth mesh with its bones as they are now (the caller has set the sail: SetSailSet): bone matrices ×
        // bind poses. No bones → the renderer's own matrix.
        private static Vector3[] RigPose(Sail sail, SkinnedMeshRenderer smr, Matrix4x4 worldToTpl)
        {
            var mesh = smr.sharedMesh;
            var v = mesh.vertices;
            var X = new Vector3[v.Length];
            var bones = smr.bones;
            if (bones == null || bones.Length == 0 || mesh.boneWeights.Length != v.Length)
            {
                var m = worldToTpl * smr.transform.localToWorldMatrix;
                for (int i = 0; i < v.Length; i++) X[i] = m.MultiplyPoint3x4(v[i]);
                return X;
            }
            var bp = mesh.bindposes; var bw = mesh.boneWeights;
            var mats = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++) mats[b] = bones[b] ? worldToTpl * bones[b].localToWorldMatrix * bp[b] : Matrix4x4.zero;
            for (int i = 0; i < v.Length; i++)
            {
                var w = bw[i];
                X[i] = mats[w.boneIndex0].MultiplyPoint3x4(v[i]) * w.weight0 + mats[w.boneIndex1].MultiplyPoint3x4(v[i]) * w.weight1
                     + mats[w.boneIndex2].MultiplyPoint3x4(v[i]) * w.weight2 + mats[w.boneIndex3].MultiplyPoint3x4(v[i]) * w.weight3;
            }
            return X;
        }

        private static Vector3 Mean(Vector3[] p, List<int> idx)
        {
            var c = Vector3.zero; foreach (int i in idx) c += p[i];
            return c / idx.Count;
        }

        // principal direction of a point set (power iteration on the covariance)
        private static Vector3 Axis(Vector3[] p, List<int> idx, Vector3 c)
        {
            float xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
            foreach (int i in idx) { var d = p[i] - c; xx += d.x * d.x; yy += d.y * d.y; zz += d.z * d.z; xy += d.x * d.y; xz += d.x * d.z; yz += d.y * d.z; }
            var v = new Vector3(1f, 0.7f, 0.3f);
            for (int k = 0; k < 30; k++)
            {
                v = new Vector3(xx * v.x + xy * v.y + xz * v.z, xy * v.x + yy * v.y + yz * v.z, xz * v.x + yz * v.y + zz * v.z);
                if (v.sqrMagnitude < 1e-12f) return Vector3.right;
                v.Normalize();
            }
            return v;
        }

        private static float DistToSegment(Vector3 q, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(q - a, ab) / ab.sqrMagnitude);
            return (q - (a + ab * t)).magnitude;
        }

        /// <summary>Turn a placed rag (MeshFilter + MeshRenderer, active, in its final pose) into live cloth.</summary>
        public static void Hang(GameObject go)
        {
            var mf = go.GetComponent<MeshFilter>(); var mr = go.GetComponent<MeshRenderer>();
            if (!mf || !mr || !mf.sharedMesh || !pins.TryGetValue(mf.sharedMesh, out var pin)) return;
            var mesh = mf.sharedMesh; var mats = mr.sharedMaterials;
            Object.DestroyImmediate(mr); Object.DestroyImmediate(mf);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh; smr.sharedMaterials = mats;
            smr.localBounds = new Bounds(mesh.bounds.center, mesh.bounds.size + Vector3.one * 8f);   // the rag swings out
            var cloth = go.AddComponent<Cloth>();
            var co = cloth.coefficients;
            // particles = vertices welded by position, in order of first appearance (as Unity builds them)
            var verts = mesh.vertices;
            var seen = new Dictionary<Vector3, int>();
            var anyPin = new List<bool>();
            for (int i = 0; i < verts.Length; i++)
            {
                if (seen.TryGetValue(verts[i], out int j)) { anyPin[j] |= pin[i]; continue; }
                seen[verts[i]] = anyPin.Count; anyPin.Add(pin[i]);
            }
            if (co.Length != anyPin.Count && co.Length == verts.Length) anyPin = new List<bool>(pin);
            if (co.Length != anyPin.Count)
            {
                Plugin.Log.LogWarning($"Torn sails: {co.Length} cloth particles vs {anyPin.Count} welded vertices — sail left still");
                Object.DestroyImmediate(cloth);
                return;
            }
            for (int i = 0; i < co.Length; i++) { co[i].maxDistance = anyPin[i] ? 0f : float.MaxValue; co[i].collisionSphereDistance = 0f; }
            cloth.coefficients = co;
            cloth.useGravity = true;
            cloth.useTethers = true;
            cloth.damping = 0.3f;
            cloth.bendingStiffness = 0.4f;          // canvas: hangs in folds, doesn't crumple
            cloth.stretchingStiffness = 0.95f;
            cloth.clothSolverFrequency = 180f;
            cloth.sleepThreshold = 0.001f;
            cloth.externalAcceleration = new Vector3(3f, 0f, 2f);    // a light breeze
            cloth.randomAcceleration = Vector3.one * 6f;              // and gusts: the rag stirs
        }
    }
}
