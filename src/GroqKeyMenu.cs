using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace LethalAICrewmate
{
    /// <summary>
    /// Small dependency-free main-menu panel for setting/testing the host Groq key.
    /// The value is stored only in the local BepInEx config and is never sent to clients.
    /// </summary>
    public sealed class GroqKeyMenu : MonoBehaviour
    {
        private const string ModelsEndpoint = "https://api.groq.com/openai/v1/models";
        private const float PanelWidth = 390f;
        private const float PanelHeight = 166f;

        private string _keyBuffer = "";
        private string _status = "";
        private float _statusUntil;
        private bool _initialized;
        private bool _testing;

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
            _keyBuffer = "";
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
            GUILayout.Label("API key (host only — never shared with clients)");

            _keyBuffer = GUILayout.PasswordField(_keyBuffer ?? "", '*', GUILayout.Height(26f));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", GUILayout.Height(26f)))
                SaveKey(showStatus: true);

            GUI.enabled = !_testing;
            if (GUILayout.Button(_testing ? "Testing..." : "Test", GUILayout.Height(26f)))
                BeginTest();
            GUI.enabled = true;

            if (GUILayout.Button("Clear", GUILayout.Height(26f)))
                ClearKey();
            GUILayout.EndHorizontal();

            string line;
            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime <= _statusUntil)
                line = _status;
            else if (!string.IsNullOrEmpty(NetMessenger.CompatibilityWarning))
                line = NetMessenger.CompatibilityWarning;
            else if (GroqSecrets.HasKey)
                line = "Key available to this host only.";
            else
                line = "Paste a Groq key, Save, then Test.";

            GUILayout.Label(line);
            GUILayout.EndArea();
        }

        private string NormalizeBuffer()
        {
            return (_keyBuffer ?? "").Trim();
        }

        private bool SaveKey(bool showStatus)
        {
            string key = NormalizeBuffer();
            if (string.IsNullOrEmpty(key))
            {
                if (showStatus) SetStatus("Paste a Groq key first.");
                return false;
            }

            if (!GroqSecrets.SetFromMenu(key))
            {
                if (showStatus) SetStatus("Invalid Groq key.");
                return false;
            }
            _keyBuffer = key;
            if (showStatus) SetStatus(Plugin.PersistApiKey?.Value == true
                ? "Groq key saved locally."
                : "Groq key saved for this game session only.");
            Plugin.Log?.LogInfo("Groq API key updated from the main menu without logging its value.");
            return true;
        }

        private void ClearKey()
        {
            _keyBuffer = "";
            GroqSecrets.ClearMenuKey();
            SetStatus("Groq key cleared.");
            Plugin.Log?.LogInfo("Groq API key cleared from local config.");
        }

        private void BeginTest()
        {
            if (_testing) return;
            if (!SaveKey(showStatus: false))
            {
                SetStatus("Paste a Groq key first.");
                return;
            }
            if (Plugin.Host == null)
            {
                SetStatus("Could not start key test.");
                return;
            }

            _testing = true;
            SetStatus("Testing Groq key...");
            Plugin.Host.StartCoroutine(TestKey(NormalizeBuffer()));
        }

        private IEnumerator TestKey(string key)
        {
            using (var request = UnityWebRequest.Get(ModelsEndpoint))
            {
                request.SetRequestHeader("Authorization", "Bearer " + key);
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 10;
                yield return request.SendWebRequest();

                bool ok = string.IsNullOrEmpty(request.error) && request.responseCode >= 200 && request.responseCode < 300;
                if (ok)
                    SetStatus("Groq key works.");
                else if (request.responseCode == 401 || request.responseCode == 403)
                    SetStatus("Groq rejected this key.");
                else if (request.responseCode > 0)
                    SetStatus("Groq test failed (HTTP " + request.responseCode + ").");
                else
                    SetStatus("Could not reach Groq. Check internet.");
            }
            _testing = false;
        }

        private void SetStatus(string text)
        {
            _status = text;
            _statusUntil = Time.unscaledTime + 4f;
        }
    }
}
