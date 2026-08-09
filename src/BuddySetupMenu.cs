using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace LethalAICrewmate
{
    /// <summary>Main-menu settings panel for AI provider, secure keys and opt-in response logging.</summary>
    public sealed class BuddySetupMenu : MonoBehaviour
    {
        private const float PanelWidth = 460f;
        private const float PanelHeight = 420f;

        private string _keyBuffer = "";
        private string _status = "";
        private float _statusUntil;
        private bool _initialized;
        private bool _testing;
        private GUIStyle _panel;
        private GUIStyle _title;
        private GUIStyle _muted;
        private GUIStyle _providerButton;
        private GUIStyle _primaryButton;
        private GUIStyle _secondaryButton;
        private GUIStyle _sectionTitle;
        private GUIStyle _warning;
        private Texture2D _panelTexture;
        private Texture2D _accentTexture;
        private Texture2D _buttonTexture;

        private static bool IsMainMenu()
        {
            try
            {
                string scene = SceneManager.GetActiveScene().name ?? "";
                return scene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            _keyBuffer = "";
        }

        private void EnsureStyles()
        {
            if (_panel != null) return;
            _panelTexture = SolidTexture(new Color(0.055f, 0.065f, 0.078f, 0.97f));
            _accentTexture = SolidTexture(new Color(0.12f, 0.46f, 0.82f, 1f));
            _buttonTexture = SolidTexture(new Color(0.12f, 0.14f, 0.17f, 1f));
            _panel = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _panelTexture },
                padding = new RectOffset(20, 20, 18, 18)
            };
            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _muted = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = new Color(0.72f, 0.76f, 0.82f) }
            };
            _providerButton = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                fixedHeight = 30f,
                normal = { background = _buttonTexture, textColor = new Color(0.84f, 0.87f, 0.91f) }
            };
            _primaryButton = new GUIStyle(_providerButton)
            {
                normal = { background = _accentTexture, textColor = Color.white }
            };
            _secondaryButton = new GUIStyle(_providerButton) { fixedHeight = 28f };
            _sectionTitle = new GUIStyle(_title) { fontSize = 14 };
            _warning = new GUIStyle(_muted)
            {
                normal = { textColor = new Color(1f, 0.72f, 0.38f) }
            };
        }

        private static Texture2D SolidTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private void OnDestroy()
        {
            if (_panelTexture != null) Destroy(_panelTexture);
            if (_accentTexture != null) Destroy(_accentTexture);
            if (_buttonTexture != null) Destroy(_buttonTexture);
        }

        private void OnGUI()
        {
            if (!IsMainMenu() || Plugin.Provider == null) return;
            EnsureInitialized();
            EnsureStyles();

            float x = Mathf.Max(18f, Screen.width - PanelWidth - 28f);
            float y = Mathf.Max(18f, Screen.height - PanelHeight - 28f);
            GUILayout.BeginArea(new Rect(x, y, PanelWidth, PanelHeight), _panel);

            GUILayout.Label("Buddy settings", _title);
            GUILayout.Label("Host-only AI and privacy controls. API keys stay on this PC.", _muted);
            GUILayout.Space(8f);

            GUILayout.Label("AI provider", _sectionTitle);
            GUILayout.BeginHorizontal();
            DrawProviderButton(BuddyAiArchitecture.OpenAiProvider, "OpenAI  |  Recommended", true);
            GUILayout.Space(6f);
            DrawProviderButton(BuddyAiArchitecture.GroqProvider, "Groq  |  Free / budget", false);
            GUILayout.EndHorizontal();

            bool openAi = GroqSecrets.IsOpenAi;
            GUILayout.Space(6f);
            GUILayout.Label(openAi
                ? "Best Buddy experience — live conversation, native voice and actions in one Realtime session."
                : "Separate low-cost pipeline — Qwen 3.6 intelligence, Whisper listening and Orpheus voice.", _muted);
            GUILayout.Space(6f);

            GUILayout.Label(GroqSecrets.ProviderName + " API key", _muted);
            _keyBuffer = GUILayout.PasswordField(_keyBuffer ?? "", '*', GUILayout.Height(27f));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save key", _primaryButton)) SaveKey(showStatus: true);
            GUI.enabled = !_testing;
            if (GUILayout.Button(_testing ? "Testing…" : "Test", _secondaryButton)) BeginTest();
            GUI.enabled = true;
            if (GUILayout.Button("Clear", _secondaryButton)) ClearKey();
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            GUILayout.Label(StatusLine(), _muted);

            GUILayout.Space(10f);
            GUILayout.Label("Response and prompt saving", _sectionTitle);
            GUILayout.Label("Both options are off by default. Only enable them after everyone in the lobby agrees.", _warning);

            bool saveResponses = Plugin.SaveResponses?.Value == true;
            bool selectedResponses = GUILayout.Toggle(saveResponses,
                " Save player messages, voice transcripts and Buddy responses", GUILayout.Height(24f));
            if (selectedResponses != saveResponses) SetResponseSaving(selectedResponses);

            GUI.enabled = selectedResponses;
            bool savePromptContext = selectedResponses && Plugin.SavePromptContext?.Value == true;
            bool selectedPromptContext = GUILayout.Toggle(savePromptContext,
                " Save system prompt and live sensor context", GUILayout.Height(24f));
            GUI.enabled = true;
            if (selectedResponses && selectedPromptContext != savePromptContext)
                SetPromptContextSaving(selectedPromptContext);

            GUILayout.Label(selectedResponses
                ? "Saving to: " + ResponseJournal.JournalPath
                : "No responses are saved. Any previous response journal has been removed.", _muted);
            GUILayout.EndArea();
        }

        private void SetResponseSaving(bool enabled)
        {
            if (Plugin.SaveResponses == null) return;
            Plugin.SaveResponses.Value = enabled;
            if (!enabled)
            {
                if (Plugin.SavePromptContext != null) Plugin.SavePromptContext.Value = false;
                ResponseJournal.DeleteExistingJournal();
                SetStatus("Response and prompt saving disabled; previous journal removed.");
            }
            else
            {
                SetStatus("Response saving enabled. Make sure every crewmate has agreed.");
            }
            Plugin.SaveConfiguration();
        }

        private void SetPromptContextSaving(bool enabled)
        {
            if (Plugin.SavePromptContext == null || Plugin.SaveResponses?.Value != true) return;
            Plugin.SavePromptContext.Value = enabled;
            Plugin.SaveConfiguration();
            SetStatus(enabled
                ? "Prompt and sensor context saving enabled."
                : "Prompt and sensor context saving disabled.");
        }

        private void DrawProviderButton(string provider, string label, bool recommended)
        {
            bool selected = string.Equals(Plugin.Provider.Value, provider, StringComparison.OrdinalIgnoreCase);
            GUIStyle style = selected ? _primaryButton : _providerButton;
            if (GUILayout.Button(label, style)) SelectProvider(provider, recommended);
        }

        private void SelectProvider(string provider, bool recommended)
        {
            string normalized = BuddyAiArchitecture.NormalizeProvider(provider);
            if (Plugin.Provider.Value == normalized) return;
            Plugin.Provider.Value = normalized;
            Plugin.SaveConfiguration();
            OpenAiRealtimeVoiceClient.ResetSession();
            LlmClient.ResetSession();
            BuddyTts.ResetSession();
            _keyBuffer = "";
            SetStatus(recommended ? "OpenAI selected — recommended for the full Buddy experience." : "Groq selected — the free / budget alternative.");
        }

        private string StatusLine()
        {
            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime <= _statusUntil) return _status;
            if (!string.IsNullOrEmpty(NetMessenger.CompatibilityWarning)) return NetMessenger.CompatibilityWarning;
            return GroqSecrets.HasKey ? "Ready on this host." : "Paste your key, save it, then test the connection.";
        }

        private string NormalizeBuffer() => (_keyBuffer ?? "").Trim();

        private bool SaveKey(bool showStatus)
        {
            string key = NormalizeBuffer();
            if (string.IsNullOrEmpty(key))
            {
                if (showStatus) SetStatus("Paste a " + GroqSecrets.ProviderName + " key first.");
                return false;
            }
            if (!GroqSecrets.SetFromMenu(key))
            {
                if (showStatus) SetStatus("That key is not valid.");
                return false;
            }
            _keyBuffer = key;
            if (showStatus) SetStatus(GroqSecrets.LastSavePersisted
                ? "Key saved securely for this Windows user."
                : "Key accepted for this session; secure saving was unavailable.");
            Plugin.Log?.LogInfo(GroqSecrets.ProviderName + " API key updated without logging its value.");
            return true;
        }

        private void ClearKey()
        {
            _keyBuffer = "";
            GroqSecrets.ClearMenuKey();
            SetStatus(GroqSecrets.ProviderName + " key cleared.");
        }

        private void BeginTest()
        {
            if (_testing) return;
            string key = NormalizeBuffer();
            if (string.IsNullOrEmpty(key))
            {
                SetStatus("Paste a " + GroqSecrets.ProviderName + " key first.");
                return;
            }
            if (Plugin.Host == null)
            {
                SetStatus("Could not start the connection test.");
                return;
            }
            _testing = true;
            SetStatus("Testing " + GroqSecrets.ProviderName + "…");
            Plugin.Host.StartCoroutine(TestKey(key));
        }

        private IEnumerator TestKey(string key)
        {
            using (var request = UnityWebRequest.Get(GroqSecrets.ModelsEndpoint))
            {
                request.SetRequestHeader("Authorization", "Bearer " + key);
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 10;
                yield return request.SendWebRequest();
                bool ok = string.IsNullOrEmpty(request.error) && request.responseCode >= 200 && request.responseCode < 300;
                if (ok) SetStatus(GroqSecrets.ProviderName + " is connected.");
                else if (request.responseCode == 401 || request.responseCode == 403) SetStatus(GroqSecrets.ProviderName + " rejected this key.");
                else if (request.responseCode > 0) SetStatus("Connection test failed (HTTP " + request.responseCode + ").");
                else SetStatus("Could not reach " + GroqSecrets.ProviderName + ". Check internet.");
            }
            _testing = false;
        }

        private void SetStatus(string text)
        {
            _status = text;
            _statusUntil = Time.unscaledTime + 5f;
        }
    }
}
