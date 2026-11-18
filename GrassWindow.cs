using System.Linq;
using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// In-game window (hotkey, default F6) to tune grass on Terra Incognita islands live:
    ///   • on/off and a density multiplier (0–2) per detail prototype — saved to config [Vegetation] GrassOverrides;
    ///   • "solo": only that prototype on every island, to see which name is which grass.
    /// Changes are planted again on all our islands with "Apply" (same seed → same layout, just thinner/thicker).
    /// </summary>
    internal class GrassWindow : MonoBehaviour
    {
        private bool open;
        private Rect rect = new Rect(40, 60, 560, 560);
        private Vector2 scroll;
        private bool dirty;
        private CursorLockMode prevLock;
        private bool prevVisible;

        private void Start() => DetailGrass.LoadOverrides();

        private void Update()
        {
            if (!Plugin.DevToolsEnabled.Value) { open = false; return; }      // a developer window
            if (Plugin.GrassWindowKey.Value.IsDown())
            {
                open = !open;
                if (open) { prevLock = Cursor.lockState; prevVisible = Cursor.visible; }
                else { Cursor.lockState = prevLock; Cursor.visible = prevVisible; }
            }
            if (open) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }   // the game re-locks it
        }

        private void OnGUI()
        {
            if (!open) return;
            rect = GUILayout.Window(0x7E1A, rect, Draw, "Terra Incognita — grass & rocks");
        }

        private void Draw(int id)
        {
            var entries = DetailGrass.PaletteEntries().ToList();
            if (entries.Count == 0)
            {
                GUILayout.Label("No islands planted yet.");
                GUI.DragWindow();
                return;
            }
            GUILayout.Label(DetailGrass.Solo != null
                ? $"SOLO: only {DetailGrass.Solo} is planted (press Solo again to leave)"
                : "Multiplier per grass type: 0 = off, 1 = as configured. Apply re-plants all islands.");

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(420));
            foreach (var group in entries.GroupBy(e => e.region))
            {
                GUILayout.Label($"<b>{group.Key}</b>");
                foreach (var e in group)
                {
                    if (e.key == null) continue;
                    float m = DetailGrass.Overrides.TryGetValue(e.key, out var v) ? v : 1f;
                    GUILayout.BeginHorizontal();
                    bool on = GUILayout.Toggle(m > 0f, "", GUILayout.Width(18));
                    GUILayout.Label($"{e.key}  (share {e.weight:0.##})", GUILayout.Width(210));
                    float nm = GUILayout.HorizontalSlider(on ? m : 0f, 0f, 2f, GUILayout.Width(110));
                    GUILayout.Label(nm.ToString("0.00"), GUILayout.Width(34));
                    if (GUILayout.Button(DetailGrass.Solo == e.key ? "Unsolo" : "Solo", GUILayout.Width(56)))
                    {
                        DetailGrass.Solo = DetailGrass.Solo == e.key ? null : e.key;
                        DetailGrass.ReapplyAll();
                    }
                    // really seaweed: grows under water by the shore instead of on land
                    bool weed = DetailGrass.Seaweed.Contains(e.key);
                    bool nw = GUILayout.Toggle(weed, "seaweed", GUILayout.Width(72));
                    if (nw != weed) { if (nw) DetailGrass.Seaweed.Add(e.key); else DetailGrass.Seaweed.Remove(e.key); dirty = true; }
                    GUILayout.EndHorizontal();

                    if (!on && m > 0f) nm = 0f;
                    else if (on && m <= 0f && nm <= 0f) nm = 1f;      // ticking the box brings it back at 1
                    nm = Mathf.Round(nm * 20f) / 20f;
                    if (Mathf.Abs(nm - m) > 0.001f) { DetailGrass.Overrides[e.key] = nm; dirty = true; }
                }
            }
            GUILayout.Space(8);
            GUILayout.Label("<b>Rocks</b> (all regions; off = hidden now and not placed next launch)");
            foreach (var (rid, size) in PropLibrary.RockList())
            {
                bool was = PropLibrary.RockEnabled(rid);
                bool now = GUILayout.Toggle(was, $" {rid}   {size}");
                if (now != was) PropLibrary.SetRockEnabled(rid, now);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(dirty ? "Apply *" : "Apply"))
            {
                int n = DetailGrass.ReapplyAll();
                Plugin.Log.LogInfo($"Grass re-planted on {n} terrains");
                dirty = false;
            }
            if (GUILayout.Button("Save to config"))
            {
                Plugin.GrassOverrides.Value = DetailGrass.SaveOverrides();
                Plugin.Seaweed.Value = DetailGrass.SaveSeaweed();
                Plugin.Instance.Config.Save();
                Plugin.Log.LogInfo("GrassOverrides = " + Plugin.GrassOverrides.Value + "; Seaweed = " + Plugin.Seaweed.Value);
            }
            if (GUILayout.Button("Reset all"))
            {
                DetailGrass.Overrides.Clear();
                DetailGrass.Solo = null;
                dirty = true;
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }
    }
}
