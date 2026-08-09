using System;
using System.Collections;
using LethalSettings.UI;
using LethalSettings.UI.Components;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

namespace LethalAICrewmate
{
    /// <summary>Buddy's native game-settings page, built with the same LethalSettings UI used by Mirage.</summary>
    internal static class BuddySettingsMenu
    {
        private static bool _registered;
        private static string _keyBuffer = "";
        private static LabelComponent _status;
        private static InputComponent _keyInput;

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;

            _keyInput = new InputComponent
            {
                Placeholder = "Paste an OpenAI API key",
                Value = "",
                OnValueChanged = (_, value) => _keyBuffer = (value ?? "").Trim(),
                OnInitialize = input =>
                {
                    TMP_InputField field = input.GetBackingObject();
                    if (field == null) return;
                    field.contentType = TMP_InputField.ContentType.Password;
                    field.characterLimit = 256;
                    field.ForceLabelUpdate();
                }
            };

            _status = new LabelComponent
            {
                Text = OpenAiSecrets.HasKey ? "Secure OpenAI key ready on this PC." : "No OpenAI key saved.",
                FontSize = 11f
            };

            var components = new MenuComponent[]
            {
                new LabelComponent { Text = "AI", FontSize = 19f },
                new LabelComponent { Text = "gpt-realtime-2.1-mini handles listening, reasoning, game tools and Buddy's voice.", FontSize = 12f },
                new ToggleComponent
                {
                    Text = "Final-stage hostile spawning",
                    Value = Plugin.FinalStageHostileSpawns?.Value == true,
                    OnValueChanged = (_, value) => Set(Plugin.FinalStageHostileSpawns, value)
                },
                new LabelComponent { Text = "API key (stored in Windows Credential Manager, never in the config file)", FontSize = 13f },
                _keyInput,
                new HorizontalComponent
                {
                    Children = new MenuComponent[]
                    {
                        new ButtonComponent { Text = "Save key", OnClick = _ => SaveKey() },
                        new ButtonComponent { Text = "Test key", OnClick = _ => BeginTest() },
                        new ButtonComponent { Text = "Clear key", OnClick = _ => ClearKey() }
                    }
                },
                _status,
                new LabelComponent { Text = "Voice", FontSize = 19f },
                new ToggleComponent
                {
                    Text = "Push-to-talk enabled",
                    Value = Plugin.VoiceEnabled?.Value == true,
                    OnValueChanged = (_, value) => Set(Plugin.VoiceEnabled, value)
                },
                new ToggleComponent
                {
                    Text = "Allow matching friends to speak to Buddy",
                    Value = Plugin.AllowRemoteVoice?.Value == true,
                    OnValueChanged = (_, value) => Set(Plugin.AllowRemoteVoice, value)
                },
                new ToggleComponent
                {
                    Text = "Spoken replies",
                    Value = Plugin.TtsEnabled?.Value == true,
                    OnValueChanged = (_, value) => Set(Plugin.TtsEnabled, value)
                },
                new SliderComponent
                {
                    Text = "Buddy voice loudness (%)",
                    MinValue = 25f,
                    MaxValue = 200f,
                    WholeNumbers = true,
                    Value = Mathf.Clamp((Plugin.TtsVolume?.Value ?? 1.25f) * 100f, 25f, 200f),
                    OnValueChanged = (_, value) =>
                    {
                        if (Plugin.TtsVolume == null) return;
                        Plugin.TtsVolume.Value = Mathf.Clamp(value / 100f, 0.25f, 2f);
                        Plugin.SaveConfiguration();
                    }
                },
                new InputComponent
                {
                    Placeholder = "Microphone name (blank = Windows default)",
                    Value = Plugin.VoiceInputDevice?.Value ?? "",
                    OnValueChanged = (_, value) =>
                    {
                        if (Plugin.VoiceInputDevice == null) return;
                        Plugin.VoiceInputDevice.Value = (value ?? "").Trim();
                        Plugin.SaveConfiguration();
                    }
                },
                new LabelComponent { Text = "Privacy", FontSize = 19f },
                new ToggleComponent
                {
                    Text = "Save typed messages and Buddy replies",
                    Value = Plugin.SaveResponses?.Value == true,
                    OnValueChanged = (_, value) => SetResponseSaving(value)
                },
                new ToggleComponent
                {
                    Text = "Also save system prompt and live sensor context",
                    Value = Plugin.SaveResponses?.Value == true && Plugin.SavePromptContext?.Value == true,
                    OnValueChanged = (_, value) => SetPromptSaving(value)
                },
                new LabelComponent
                {
                    Text = "Response saving is opt-in. Only enable it after everyone in the lobby agrees.",
                    FontSize = 11f
                }
            };

            ModMenu.RegisterMod(new ModMenu.ModSettingsConfig
            {
                Name = "Buddy",
                Id = Plugin.ModGuid,
                Version = Plugin.ModVersion,
                Description = "OpenAI Realtime, secure key, voice, story and privacy controls.",
                MenuComponents = components
            }, true, true);
            Plugin.Log?.LogInfo("Registered Buddy in the game's Mod Settings menu.");
        }

        private static void Set(BepInEx.Configuration.ConfigEntry<bool> entry, bool value)
        {
            if (entry == null) return;
            entry.Value = value;
            Plugin.SaveConfiguration();
        }

        private static void SaveKey()
        {
            if (string.IsNullOrWhiteSpace(_keyBuffer))
            {
                SetStatus("Paste an OpenAI key first.");
                return;
            }
            if (!OpenAiSecrets.SetFromMenu(_keyBuffer))
            {
                SetStatus("That OpenAI key is empty or invalid.");
                return;
            }
            SetStatus(OpenAiSecrets.LastSavePersisted ? "Key saved securely." : "Key active for this session only.");
            _keyBuffer = "";
            if (_keyInput != null) _keyInput.Value = "";
        }

        private static void ClearKey()
        {
            _keyBuffer = "";
            if (_keyInput != null) _keyInput.Value = "";
            OpenAiSecrets.ClearMenuKey();
            SetStatus("OpenAI key cleared.");
        }

        private static void BeginTest()
        {
            if (string.IsNullOrWhiteSpace(_keyBuffer))
            {
                SetStatus("Paste a key first. Testing does not save it.");
                return;
            }
            if (Plugin.Host == null) return;
            SetStatus("Testing OpenAI...");
            Plugin.Host.StartCoroutine(TestKey(_keyBuffer));
        }

        private static IEnumerator TestKey(string key)
        {
            using (var request = UnityWebRequest.Get(OpenAiSecrets.ModelsEndpoint))
            {
                request.SetRequestHeader("Authorization", "Bearer " + key);
                request.timeout = 10;
                yield return request.SendWebRequest();
                bool ok = string.IsNullOrEmpty(request.error) && request.responseCode >= 200 && request.responseCode < 300;
                SetStatus(ok ? "OpenAI connection works. Press Save key to keep it."
                    : request.responseCode == 401 || request.responseCode == 403 ? "OpenAI rejected that key."
                    : "Connection test failed.");
            }
        }

        private static void SetResponseSaving(bool enabled)
        {
            if (Plugin.SaveResponses == null) return;
            Plugin.SaveResponses.Value = enabled;
            if (!enabled)
            {
                if (Plugin.SavePromptContext != null) Plugin.SavePromptContext.Value = false;
                ResponseJournal.DeleteExistingJournal();
            }
            Plugin.SaveConfiguration();
        }

        private static void SetPromptSaving(bool enabled)
        {
            if (Plugin.SavePromptContext == null) return;
            Plugin.SavePromptContext.Value = enabled && Plugin.SaveResponses?.Value == true;
            Plugin.SaveConfiguration();
        }

        private static void SetStatus(string value)
        {
            if (_status != null) _status.Text = value ?? "";
        }
    }
}
