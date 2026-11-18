using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Seagulls over the new islands — heralds of land. The game's own flock (Seagulls: a particle swarm with its
    /// cries; by day it settles over the island, at night it rises 600 m and falls silent) is cloned from a vanilla
    /// island onto each of ours, centred, at the vanilla flock's height above its island.
    /// </summary>
    internal static class Gulls
    {
        public static int Place(IList<SpawnedIsland> islands)
        {
            if (!Plugin.GullsEnabled.Value) return 0;
            var src = Resources.FindObjectsOfTypeAll<Seagulls>()
                .Where(s => s && s.gameObject.scene.IsValid() && !s.name.StartsWith("TerraIncognita"))
                .OrderBy(s => Mathf.Abs(s.transform.localPosition.y - 60f))     // a typical flock, not an odd one
                .FirstOrDefault();
            if (!src) { Plugin.Log.LogInfo("Gulls: no vanilla seagull flock found"); return 0; }
            float h = src.transform.localPosition.y;
            int n = 0;
            foreach (var s in islands)
            {
                if (!s.Root) continue;
                var g = Object.Instantiate(src.gameObject, s.Root, false);
                g.name = "TerraIncognita seagulls";
                // at least 20 m over the island's top (the vanilla flock's ~60 m put them level with a volcano's summit)
                float hi = h;
                var terr = s.Root.GetComponentInChildren<Terrain>(true);
                if (terr && terr.terrainData)
                {
                    float top = s.Root.InverseTransformPoint(terr.GetPosition() + Vector3.up * terr.terrainData.bounds.max.y).y;
                    hi = Mathf.Max(h, top + 20f);
                }
                g.transform.localPosition = new Vector3(0f, hi, 0f);
                g.transform.localRotation = src.transform.localRotation;
                // the game's Seagulls flips between "settled" and "600 m up, silent" every 2–5 game hours all day, and
                // its clone kept the state of a far-off flock that wasn't playing — ours is driven by GullFlock instead
                var gs = g.GetComponent<Seagulls>();
                if (gs) Object.DestroyImmediate(gs);
                var ps = g.GetComponent<ParticleSystem>();
                if (ps)
                {
                    var main = ps.main;
                    if (main.simulationSpace == ParticleSystemSimulationSpace.Custom) main.simulationSpace = ParticleSystemSimulationSpace.Local;
                    main.playOnAwake = true;
                    main.prewarm = true;                                    // birds already in the air when it switches on
                    Upright(g.transform, ps);
                }
                var au = g.GetComponent<AudioSource>();
                if (au) { au.playOnAwake = false; au.loop = true; }
                g.AddComponent<GullFlock>().height = hi;
                // the source is often switched off (its island far away): the clone was born switched off and never
                // ran at all — no birds, no cries, not even GullFlock's report
                g.SetActive(true);
                n++;
            }
            var sps = src.GetComponent<ParticleSystem>();
            if (sps) Plugin.Log.LogInfo($"Gulls source: shape rot {sps.shape.rotation.ToString("0")} pos {sps.shape.position.ToString("0.#")}, orbital mode {sps.velocityOverLifetime.orbitalZ.mode}, " +
                                        $"linear ({sps.velocityOverLifetime.x.constant:0.##}, {sps.velocityOverLifetime.y.constant:0.##}, {sps.velocityOverLifetime.z.constant:0.##}), offset {sps.velocityOverLifetime.orbitalOffsetX.constant:0.#}/{sps.velocityOverLifetime.orbitalOffsetY.constant:0.#}/{sps.velocityOverLifetime.orbitalOffsetZ.constant:0.#}");
            Plugin.Log.LogInfo($"Gulls: {n} flocks from {src.transform.parent?.name}/{src.name} at {h:0} m " +
                               (sps ? $"(source: switched {(src.gameObject.activeSelf ? "on" : "OFF")}, space {sps.main.simulationSpace}, scaling {sps.main.scalingMode}, shape {sps.shape.shapeType} r {sps.shape.radius:0}, " +
                                      $"particles {sps.particleCount}, playing {sps.isPlaying}, renderer {(sps.GetComponent<ParticleSystemRenderer>()?.enabled)})" : "(no particles on it)"));
            return n;
        }

        /// <summary>
        /// The game's flock lies on its side (local rot 270, 240, 0: its Z is the world vertical — the circle and the
        /// orbit are about Z). A bird can only be turned cleanly about the flock's own Y (any other turn passes through
        /// Euler angles at the ±90° lock and flips), so the flock is stood upright and its emitter circle, orbit and
        /// drift are turned into the new frame — the same motion in the world.
        /// </summary>
        private static void Upright(Transform g, ParticleSystem ps)
        {
            var old = g.rotation;                        // old local → world (= new local: the new rotation is identity)
            g.rotation = Quaternion.identity;
            var shape = ps.shape;
            shape.rotation = (old * Quaternion.Euler(shape.rotation)).eulerAngles;
            shape.position = old * shape.position;
            var vol = ps.velocityOverLifetime;
            if (!vol.enabled) return;
            Vector3 V(ParticleSystem.MinMaxCurve a, ParticleSystem.MinMaxCurve b, ParticleSystem.MinMaxCurve c) => old * new Vector3(a.constant, b.constant, c.constant);
            var orb = V(vol.orbitalX, vol.orbitalY, vol.orbitalZ);
            vol.orbitalX = orb.x; vol.orbitalY = orb.y; vol.orbitalZ = orb.z;
            var off = V(vol.orbitalOffsetX, vol.orbitalOffsetY, vol.orbitalOffsetZ);
            vol.orbitalOffsetX = off.x; vol.orbitalOffsetY = off.y; vol.orbitalOffsetZ = off.z;
            if (vol.x.mode == ParticleSystemCurveMode.Constant && vol.y.mode == ParticleSystemCurveMode.Constant && vol.z.mode == ParticleSystemCurveMode.Constant)
            {
                var lin = V(vol.x, vol.y, vol.z);
                vol.x = lin.x; vol.y = lin.y; vol.z = lin.z;
            }
        }
    }

    /// <summary>
    /// Our island's flock: by day (05–19 h) it circles at its height with particles and cries on; at night it is
    /// switched off. Cries only within earshot. The first time the player comes close, its state goes to the log.
    /// </summary>
    internal class GullFlock : MonoBehaviour
    {
        public float height;
        private ParticleSystem ps;
        private AudioSource au;
        private bool? day;
        private static int reports;
        private float nextReport;

        private void OnEnable()
        {
            ps = GetComponent<ParticleSystem>();
            au = GetComponent<AudioSource>();
            day = null;                                                  // re-apply after the island switches on
        }

        // ---- heading: the birds keep whatever turn they were born with while the orbit carries them round, so they
        // fly backwards on one side of the circle and sideways on another. Each frame every bird is turned to face
        // where it actually moved since the last frame (orbit + noise, whatever moves it).
        private ParticleSystem.Particle[] buf;
        private readonly Dictionary<uint, Vector3> lastPos = new Dictionary<uint, Vector3>();
        private readonly Dictionary<uint, Vector3> lastDir = new Dictionary<uint, Vector3>();
        private ParticleSystemRenderer rend;
        private bool steer;
        private static bool described;
        private float tilt;
        private static bool tiltLogged;

        private void Start()
        {
            rend = GetComponent<ParticleSystemRenderer>();
            if (!ps || !rend) return;
            var main = ps.main; var vol = ps.velocityOverLifetime; var rol = ps.rotationOverLifetime;
            if (!described)
            {
                described = true;
                Plugin.Log.LogInfo($"Gulls particles: render {rend.renderMode} align {rend.alignment} mesh {(rend.mesh ? rend.mesh.name : "-")}, " +
                                   $"start speed {main.startSpeed.constant:0.#}, start rot3D {main.startRotation3D}, rot over life {rol.enabled}, " +
                                   $"orbital {vol.enabled} ({vol.orbitalX.constant:0.##}, {vol.orbitalY.constant:0.##}, {vol.orbitalZ.constant:0.##}) space {vol.space}, " +
                                   $"noise {ps.noise.enabled}, local rot {transform.localEulerAngles.ToString("0")}");
            }
            // each bird turned about the vertical only, in the flock's space — the flock stands upright (Gulls.Upright).
            // The seagull mesh need not be Y-up (the game tilts it with its start rotation): its flat axis (wings and
            // body span the other two) is its up; laid level by a turn about X written straight into the Euler angles
            // (x, yaw, 0) — Ry·Rx, never converted from a quaternion, so never at the ±90° lock.
            steer = rend.renderMode == ParticleSystemRenderMode.Mesh;
            if (!steer) return;
            float sx = main.startRotationX.mode == ParticleSystemCurveMode.TwoConstants
                ? (main.startRotationX.constantMin + main.startRotationX.constantMax) * 0.5f : main.startRotationX.constant;
            float sy = main.startRotationY.constant, sz = main.startRotationZ.constant;
            var mb = rend.mesh ? rend.mesh.bounds.size : Vector3.one;
            float vanillaX = sx * Mathf.Rad2Deg;
            if (Mathf.Abs(Mathf.Abs(vanillaX) - 90f) < 30f) tilt = Mathf.Sign(vanillaX) * 90f;          // the game's own tilt
            else if (mb.z < mb.x && mb.z < mb.y) tilt = -90f;                                         // Z-up mesh
            else tilt = 0f;
            if (!tiltLogged && (tiltLogged = true)) Plugin.Log.LogInfo($"Gulls: seagull mesh size {mb.ToString("0.##")}, game start rotation ({vanillaX:0}, {sy * Mathf.Rad2Deg:0}, {sz * Mathf.Rad2Deg:0}) " +
                               $"[{main.startRotationX.mode}], rotation over life {(rol.enabled ? $"{rol.x.constant * Mathf.Rad2Deg:0}/{rol.y.constant * Mathf.Rad2Deg:0}/{rol.z.constant * Mathf.Rad2Deg:0} °/s" : "off")} → tilt {tilt:0}");
            rend.alignment = ParticleSystemRenderSpace.Local;
            main.startRotation3D = true;
            rol.enabled = false;
            var rbs = ps.rotationBySpeed; rbs.enabled = false;
        }

        private void LateUpdate()
        {
            if (!steer || !ps || !rend.isVisible) return;
            int n = ps.particleCount;
            if (n == 0) return;
            if (buf == null || buf.Length < ps.main.maxParticles) buf = new ParticleSystem.Particle[ps.main.maxParticles];
            n = ps.GetParticles(buf);
            float yawFix = Plugin.GullHeadingYaw.Value;
            for (int i = 0; i < n; i++)
            {
                uint id = buf[i].randomSeed;
                var p = buf[i].position;
                if (lastPos.TryGetValue(id, out var q))
                {
                    var d = transform.TransformDirection(p - q);                // particles live in the flock's space
                    d.y = 0f;
                    if (d.sqrMagnitude > 1e-8f)
                    {
                        d.Normalize();
                        var dir = lastDir.TryGetValue(id, out var o) ? Vector3.Slerp(o, d, 0.2f) : d;    // no twitching
                        lastDir[id] = dir;
                        float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + yawFix;
                        buf[i].rotation3D = new Vector3(tilt + Plugin.GullTilt.Value, yaw, 0f);
                    }
                }
                lastPos[id] = p;
            }
            ps.SetParticles(buf, n);
            if (lastPos.Count > 4 * n) { lastPos.Clear(); lastDir.Clear(); }    // dead birds' ids
        }

        private void Update()
        {
            float t = Sun.sun ? Sun.sun.localTime : 12f;
            bool d = t > 5f && t < 19f;
            if (day != d)
            {
                day = d;
                if (ps)
                {
                    var em = ps.emission; em.enabled = d;
                    if (d && !ps.isPlaying) ps.Play();
                    if (!d) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
                if (au) { if (d && !au.isPlaying) au.Play(); if (!d) au.Stop(); }
            }
            if (reports < 2 && Time.time > nextReport && Refs.observerMirror)
            {
                nextReport = Time.time + 15f;
                var me = Refs.observerMirror.transform.position;
                var flat = new Vector2(me.x - transform.position.x, me.z - transform.position.z).magnitude;
                if (flat < 500f)
                {
                    reports++;
                    var r = GetComponent<ParticleSystemRenderer>();
                    Plugin.Log.LogInfo($"Gulls over {transform.parent?.name}: {flat:0} m away, {transform.position.y - me.y:0} m above you, time {t:0.0}, " +
                                       (ps ? $"playing {ps.isPlaying}, emitting {ps.emission.enabled}, birds {ps.particleCount}/{ps.main.maxParticles}, " : "no particles, ") +
                                       $"renderer {(r ? r.enabled + " " + (r.sharedMaterial ? r.sharedMaterial.name : "no material") : "-")}, " +
                                       $"sound {(au ? au.isPlaying + " " + (au.clip ? au.clip.name : "no clip") + " vol " + au.volume.ToString("0.##") + " maxDist " + au.maxDistance.ToString("0") : "-")}");
                }
            }
        }
    }
}