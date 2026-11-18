using UnityEngine;

namespace TerraIncognita
{
    /// <summary>
    /// In-game window (hotkey, default F4; developer tools on) to tune how the sculpted rocks are painted (the
    /// PaintedRock shader's knobs): changes show at once; Save writes them to [Rocks] SculptedPaint.
    /// </summary>
    internal class RockTextureWindow : MonoBehaviour
    {
        private bool open;
        private Rect rect = new Rect(40, 60, 430, 660);
        private CursorLockMode prevLock;
        private bool prevVisible;
        private string status = "";

        // field, label, min, max
        private static readonly (string n, string label, float lo, float hi)[] Knobs =
        {
            ("BlotchScale", "Blotch size (m)", 1f, 30f),
            ("Blotch", "Blotches", 0f, 0.6f),
            ("Detail", "Small blotches", 0f, 0.4f),
            ("Hue", "Warm/cool patches", 0f, 1f),
            ("Occlusion", "Hollows darker", 0f, 1f),
            ("Cavity", "Folds darker", 0f, 1f),
            ("Ridge", "Ridges lighter", 0f, 0.6f),
            ("Wet", "Wet band darker", 0f, 0.8f),
            ("WetHeight", "Wet band height (m)", 0f, 6f),
            ("Sun", "Bleached tops", 0f, 0.6f),
            ("Cracks", "Cracks", 0f, 1f),
            ("CrackScale", "Crack spacing (m)", 0.5f, 15f),
            ("CrackWidth", "Crack width", 0.005f, 0.25f),
            ("CrackShare", "Share of joints cracked", 0f, 1f),
            ("CrackFine", "Fine network strength", 0f, 1f),
            ("CrackLip", "Light lip under cracks", 0f, 1f),
            ("PlateTone", "Blocks tone spread", 0f, 0.3f),
            ("SizeFollow", "Pattern follows rock size", 0f, 1f),
            ("CrackDark", "Crack darkness", 0f, 1f),
            ("CrackSoft", "Crack softness (edge)", 0f, 1f),
            ("CrackFlat", "Flat slabs", 0f, 1f),
            ("Brightness", "Brightness", 0.2f, 1.5f),
            ("Glossiness", "Gloss", 0f, 0.6f),
        };

        private void Update()
        {
            if (!Plugin.DevToolsEnabled.Value) { open = false; return; }
            if (Plugin.RockTextureWindowKey.Value.IsDown())
            {
                open = !open;
                if (open) { prevLock = Cursor.lockState; prevVisible = Cursor.visible; }
                else { Cursor.lockState = prevLock; Cursor.visible = prevVisible; }
            }
            if (open) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        private void OnGUI()
        {
            if (!open) return;
            rect = GUILayout.Window(0x7E1B, rect, Draw, "Terra Incognita — rock paint");
        }

        private void Draw(int id)
        {
            var p = RockSculptor.Paint;
            GUILayout.Label("How the sculpted rocks are painted. Changes show at once.");
            bool changed = false;
            foreach (var k in Knobs)
            {
                float v = p.Get(k.n);
                GUILayout.BeginHorizontal();
                GUILayout.Label(k.label, GUILayout.Width(150));
                float nv = GUILayout.HorizontalSlider(v, k.lo, k.hi, GUILayout.Width(180));
                GUILayout.Label(nv.ToString("0.###"), GUILayout.Width(50));
                GUILayout.EndHorizontal();
                if (!Mathf.Approximately(nv, v)) { p.Set(k.n, nv); changed = true; }
            }
            if (changed) RockSculptor.Repaint();
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save")) { RockSculptor.SavePaint(); status = "saved to config"; }
            if (GUILayout.Button("Defaults")) { RockSculptor.ResetPaint(); status = "defaults"; }
            if (GUILayout.Button("Close")) { open = false; Cursor.lockState = prevLock; Cursor.visible = prevVisible; }
            GUILayout.EndHorizontal();
            GUILayout.Label(status);
            GUI.DragWindow();
        }
    }
}
