using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Floating name over Buddy + scanner label (ScanNode).
    /// </summary>
    public static class BuddyNameTag
    {
        private static string SanitizeName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return "Buddy";
            var sb = new System.Text.StringBuilder(displayName.Length);
            foreach (char c in displayName)
            {
                if (c == '\n' || c == '\r' || c == '\t' || char.IsControl(c)) continue;
                sb.Append(c);
            }
            string name = sb.ToString().Trim();
            if (name.Length > 24) name = name.Substring(0, 24).TrimEnd();
            return string.IsNullOrEmpty(name) ? "Buddy" : name;
        }

        public static void Attach(MaskedPlayerEnemy enemy, string displayName)
        {
            if (enemy == null) return;
            try
            {
                string name = SanitizeName(displayName);

                // Scanner / Z label
                try
                {
                    var scans = enemy.GetComponentsInChildren<ScanNodeProperties>(true);
                    if (scans != null)
                    {
                        foreach (var scan in scans)
                        {
                            if (scan == null) continue;
                            scan.headerText = name;
                            try { scan.subText = "AI crewmate"; } catch { /* field may differ */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"ScanNode name: {ex.Message}");
                }

                // World nameplate
                Transform existing = enemy.transform.Find("BuddyNameTag");
                if (existing != null)
                    UnityEngine.Object.Destroy(existing.gameObject);

                var go = new GameObject("BuddyNameTag");
                go.transform.SetParent(enemy.transform, false);
                go.transform.localPosition = new Vector3(0f, 2.35f, 0f);

                var tm = go.AddComponent<TextMesh>();
                tm.text = name;
                tm.fontSize = 48;
                tm.characterSize = 0.045f;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.color = new Color(0.45f, 0.95f, 1f, 1f); // cyan-ish crew tag
                tm.fontStyle = FontStyle.Bold;

                // Soft outline via second mesh
                var outline = new GameObject("Outline");
                outline.transform.SetParent(go.transform, false);
                outline.transform.localPosition = new Vector3(0.01f, -0.01f, 0.01f);
                var otm = outline.AddComponent<TextMesh>();
                otm.text = name;
                otm.fontSize = 48;
                otm.characterSize = 0.045f;
                otm.anchor = TextAnchor.MiddleCenter;
                otm.alignment = TextAlignment.Center;
                otm.color = new Color(0f, 0f, 0f, 0.75f);
                otm.fontStyle = FontStyle.Bold;

                go.AddComponent<BuddyNameTagBillboard>();

                Plugin.Log?.LogInfo($"Name tag attached: '{name}'");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"BuddyNameTag.Attach: {ex.Message}");
            }
        }
    }

    public class BuddyNameTagBillboard : MonoBehaviour
    {
        private void LateUpdate()
        {
            try
            {
                var cam = Camera.main;
                if (cam == null)
                {
                    // LC often uses the player camera
                    try
                    {
                        var local = StartOfRound.Instance?.localPlayerController;
                        if (local != null && local.gameplayCamera != null)
                            cam = local.gameplayCamera;
                    }
                    catch { /* ignore */ }
                }
                if (cam == null) return;

                // Face camera
                transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
            }
            catch
            {
                // never break enemy
            }
        }
    }
}
