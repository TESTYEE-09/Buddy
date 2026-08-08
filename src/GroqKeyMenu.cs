using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LethalAICrewmate
{
    /// <summary>
    /// Small main-menu panel for setting the host Groq key without editing config files.
    /// Uses Unity IMGUI so the mod does not need extra UI dependencies.
    /// </summary>
    public sealed class GroqKeyMenu : MonoBehaviour
    {
        private string _keyBuffer = "";
        private string _status = "";
        private float _statusUntil;
        private bool _initialized;

        private const float PanelWidth = 350f;
        private const float PanelHeight = 138f;

        private static bool IsMainMenu()
        {
            try
            {
                string scene = SceneManager.GetActiveScene().name ?? "";
                return scene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            _keyBuffer = Plugin.ApiKey?.Value ?? "";
        }

        private void OnGUI()
        {
            if (!IsMainMenu() || Plugin.ApiKey == null)
                return;

            EnsureInitialized();

            float x = Mathf.Max(16f, Screen.width - PanelWidth - 24f);
            float y = Mathf.Max(16f, Screen.height - PanelHeight - 24f);

            GUI.Box(new Rect(x, y, PanelWidth, PanelHeight), GUIContent.none);
            GUILayout.BeginArea(new Rect(x + 14f, y + 11f, PanelWidth - 28f, PanelHeight - 22f));

            GUILayout.Label("Lethal AI Crewmate — Groq");
            GUILayout.Label("API key (host only)");

            GUILayout.BeginHorizontal();
            _keyBuffer = GUILayout.PasswordField(_keyBuffer ?? "", '*', GUILayout.Height(26f));
            if (GUILayout.Button("Save", GUILayout.Width(64f), GUILayout.Height(26f)))
                SaveKey();
            GUILayout.EndHorizontal();

            string line;
            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime <= _statusUntil)
                line = _status;
            else if (!string.IsNullOrEmpty(NetMessenger.CompatibilityWarning))
                line = NetMessenger.CompatibilityWarning;
            else if (!string.IsNullOrWhiteSpace(Plugin.ApiKey.Value))
                line = "Key saved. Multiplayer clients do not need a Groq key.";
            else
                line = "Paste a Groq key, then Save.";

            GUILayout.Label(line);
            GUILayout.EndArea();
        }

        private void SaveKey()
        {
            string key = (_keyBuffer ?? "").Trim();
            if (string.IsNullOrEmpty(key))
            {
                SetStatus("Paste a Groq key first.");
                return;
            }

            Plugin.ApiKey.Value = key;
            Plugin.Instance?.Config.Save();
            _keyBuffer = key;
            SetStatus("Groq key saved.");
            Plugin.Log?.LogInfo("Groq API key updated from the main menu.");
        }

        private void SetStatus(string text)
        {
            _status = text;
            _statusUntil = Time.unscaledTime + 3f;
        }
    }
}
