using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// Developer helpers for testing in the world (only with [Debug] DevTools = true):
    ///   Ctrl+B — pink beacons: a tall pink column over every camp, wreck and pond (incl. stone basins) that is loaded.
    ///   Ctrl+F — fly: no gravity, no collisions; WASD along the view, Space up, C down, Shift ×6, mouse wheel = speed.
    /// </summary>
    internal class DevFly : MonoBehaviour
    {
        internal static bool Flying;
        private static float speed = 20f;

        private bool beacons;
        private float nextScan;
        private Material pink;
        private readonly List<GameObject> made = new List<GameObject>();

        private void Update()
        {
            if (!Plugin.DevToolsEnabled.Value) { if (Flying) SetFly(false); return; }
            if (Plugin.FlyKey.Value.IsDown()) SetFly(!Flying);
            if (Plugin.BeaconsKey.Value.IsDown())
            {
                beacons = !beacons;
                if (!beacons) Clear();
                else { nextScan = 0f; }
                Plugin.Log.LogInfo("Beacons " + (beacons ? "on" : "off"));
            }
            if (beacons && Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 3f;
                try { Scan(); } catch (System.Exception e) { Plugin.Log.LogWarning("beacons: " + e.Message); }
            }
            if (Flying)
            {
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (wheel != 0f) speed = Mathf.Clamp(speed * (wheel > 0f ? 1.25f : 0.8f), 2f, 300f);
            }
        }

        private static void SetFly(bool on)
        {
            Flying = on;
            if (!on && Refs.ovrController) Refs.ovrController.Stop();      // no fall speed left over from before
            Plugin.Log.LogInfo(on ? $"Fly on (speed {speed:0} m/s, Shift ×6, wheel to change)" : "Fly off");
        }

        /// <summary>Called instead of the player's own movement while flying.</summary>
        internal static void Move(OVRPlayerController pc)
        {
            var cam = Camera.main ? Camera.main.transform : pc.transform;
            Vector3 d = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) d += cam.forward;
            if (Input.GetKey(KeyCode.S)) d -= cam.forward;
            if (Input.GetKey(KeyCode.D)) d += cam.right;
            if (Input.GetKey(KeyCode.A)) d -= cam.right;
            if (Input.GetKey(KeyCode.Space)) d += Vector3.up;
            if (Input.GetKey(KeyCode.C)) d -= Vector3.up;
            if (d == Vector3.zero) return;
            float v = speed * (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 6f : 1f);
            pc.transform.position += d.normalized * v * Time.deltaTime;
        }

        // ------------------------------------------------------------------ beacons

        private void Scan()
        {
            made.RemoveAll(b => !b);
            if (!pink)
            {
                pink = new Material(Shader.Find("Sprites/Default")) { name = "ti beacon", color = new Color(1f, 0.3f, 0.75f, 0.85f) };
                pink.renderQueue = 3100;
            }
            var have = made.Select(b => b.transform.position).ToList();
            int added = 0;
            foreach (var t in FindObjectsOfType<Transform>())
            {
                string n = t.name;
                bool hit = n.StartsWith("camp ") || n == "pond water" || (n.StartsWith("wreck ") && !n.StartsWith("wreck decor"));
                if (!hit || !t.gameObject.activeInHierarchy) continue;
                var p = t.position;
                if (have.Any(h => new Vector2(h.x - p.x, h.z - p.z).sqrMagnitude < 15f * 15f)) continue;   // one per camp
                var b = new GameObject("ti beacon");
                b.transform.SetParent(t, false);
                b.transform.rotation = Quaternion.identity;
                var ls = t.lossyScale;
                b.transform.localScale = new Vector3(1f / Mathf.Max(0.01f, ls.x), 1f / Mathf.Max(0.01f, ls.y), 1f / Mathf.Max(0.01f, ls.z));
                var lr = b.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.positionCount = 2;
                lr.SetPosition(0, Vector3.zero);
                lr.SetPosition(1, Vector3.up * 400f);
                lr.startWidth = lr.endWidth = 2f;
                lr.sharedMaterial = pink;
                lr.startColor = lr.endColor = pink.color;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                made.Add(b);
                have.Add(p);
                added++;
            }
            if (added > 0) Plugin.Log.LogInfo($"Beacons: +{added} ({made.Count} shown)");
        }

        private void Clear()
        {
            foreach (var b in made) if (b) Destroy(b);
            made.Clear();
        }
    }

    [HarmonyPatch(typeof(OVRPlayerController), "UpdateController")]
    internal static class FlyPatch
    {
        private static bool Prefix(OVRPlayerController __instance)
        {
            if (!DevFly.Flying) return true;
            DevFly.Move(__instance);
            return false;
        }
    }
}
