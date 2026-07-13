using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace LethalAICrewmate
{
    public static class LlmClient
    {
        private const string GroqChatEndpoint = "https://api.groq.com/openai/v1/chat/completions";
        private const int MaxHistory = 12;
        private const int MaxQueue = 3;
        private const float MinInterval = 1.5f; // Groq is fast
        private const int MaxTokens = 150;

        private static readonly Queue<PendingRequest> Queue = new Queue<PendingRequest>();
        private static readonly List<ChatTurn> History = new List<ChatTurn>();
        private static bool _inFlight;
        private static float _lastCallTime = -999f;
        private static Coroutine _running;

        public static void ResetSession()
        {
            try
            {
                Queue.Clear();
                History.Clear();
                _inFlight = false;
                _lastCallTime = -999f;
                if (_running != null && Plugin.Host != null)
                {
                    try { Plugin.Host.StopCoroutine(_running); } catch { /* ignore */ }
                }
                _running = null;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"LlmClient.ResetSession: {ex.Message}");
            }
        }

        private struct PendingRequest
        {
            public string UserContent;
            public bool IsObservation;
        }

        private struct ChatTurn
        {
            public string Role;
            public string Content;
        }

        public static bool HasApiKey => !string.IsNullOrEmpty(Plugin.ApiKey?.Value);

        public static void EnqueuePlayerMessage(string playerName, string message, bool isCommand)
        {
            if (!HasApiKey)
                return;

            string content = $"{playerName} says: {message}";
            if (isCommand)
                content += " (They issued a movement/scrap command — acknowledge briefly in character.)";

            Enqueue(content, isObservation: false);
        }

        public static void EnqueueObservation(string summary)
        {
            if (!HasApiKey) return;
            Enqueue($"[Observation] {summary}", isObservation: true);
        }

        private static void Enqueue(string userContent, bool isObservation)
        {
            if (Queue.Count >= MaxQueue)
            {
                Plugin.Log?.LogInfo("LLM queue full; dropping request.");
                return;
            }
            Queue.Enqueue(new PendingRequest { UserContent = userContent, IsObservation = isObservation });
        }

        public static void Tick()
        {
            try
            {
                if (_inFlight) return;
                if (Queue.Count == 0) return;
                if (Plugin.Host == null) return;
                if (!HasApiKey)
                {
                    Queue.Clear();
                    return;
                }
                if (Time.time - _lastCallTime < MinInterval) return;
                if (!CrewmateSpawner.IsHost()) return;

                var req = Queue.Dequeue();
                _running = Plugin.Host.StartCoroutine(SendRequest(req));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"LlmClient.Tick: {ex}");
                _inFlight = false;
            }
        }

        private static IEnumerator SendRequest(PendingRequest pending)
        {
            _inFlight = true;
            _lastCallTime = Time.time;

            string systemPrompt = BuildSystemPrompt();
            History.Add(new ChatTurn { Role = "user", Content = pending.UserContent });
            TrimHistory();

            string body = BuildRequestJson(systemPrompt, History);
            string model = Plugin.Model?.Value ?? "meta-llama/llama-4-scout-17b-16e-instruct";

            using (var uwr = new UnityWebRequest(GroqChatEndpoint, "POST"))
            {
                byte[] raw = Encoding.UTF8.GetBytes(body);
                uwr.uploadHandler = new UploadHandlerRaw(raw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.SetRequestHeader("Authorization", "Bearer " + Plugin.ApiKey.Value);

                Plugin.Log?.LogInfo($"Groq chat → model={model}");
                yield return uwr.SendWebRequest();

                try
                {
                    bool ok = string.IsNullOrEmpty(uwr.error)
                              && uwr.responseCode >= 200
                              && uwr.responseCode < 300;

                    if (!ok)
                    {
                        Plugin.Log?.LogWarning($"Groq chat HTTP {uwr.responseCode}: {uwr.error} {uwr.downloadHandler?.text}");
                    }
                    else
                    {
                        string responseText = uwr.downloadHandler?.text ?? "";
                        string content = ParseAssistantContent(responseText);
                        if (!string.IsNullOrEmpty(content))
                            HandleAssistantReply(content);
                        else
                            Plugin.Log?.LogWarning("Groq chat: empty assistant content");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"LLM response handling: {ex}");
                }
            }

            _inFlight = false;
            _running = null;
        }

        private static string BuildSystemPrompt()
        {
            var name = Plugin.CrewmateName?.Value ?? "Buddy";
            var personality = Plugin.Personality?.Value ?? "Helpful crewmate.";
            var sb = new StringBuilder();
            sb.Append("You are ").Append(name).Append(", an AI crewmate in Lethal Company. ");
            sb.Append(personality).Append(' ');
            sb.Append("Reply in character in under 20 words (spoken aloud, keep it short). No markdown. ");
            sb.Append("If the player asked for an action, include exactly one tag at the end: ");
            sb.Append("[FOLLOW], [STAY], [SHIP], or [FETCH]. Otherwise do not include tags.");
            return sb.ToString();
        }

        private static void TrimHistory()
        {
            while (History.Count > MaxHistory)
                History.RemoveAt(0);
        }

        private static string BuildRequestJson(string systemPrompt, List<ChatTurn> history)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"model\":\"").Append(Escape(Plugin.Model?.Value ?? "meta-llama/llama-4-scout-17b-16e-instruct")).Append("\",");
            sb.Append("\"max_tokens\":").Append(MaxTokens).Append(',');
            sb.Append("\"temperature\":0.7,");
            sb.Append("\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"").Append(Escape(systemPrompt)).Append("\"}");
            foreach (var turn in history)
            {
                sb.Append(',');
                sb.Append("{\"role\":\"").Append(Escape(turn.Role)).Append("\",\"content\":\"")
                  .Append(Escape(turn.Content)).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        internal static string ParseAssistantContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            int msgIdx = json.IndexOf("\"message\"", StringComparison.Ordinal);
            int searchFrom = msgIdx >= 0 ? msgIdx : 0;
            int contentKey = json.IndexOf("\"content\"", searchFrom, StringComparison.Ordinal);
            if (contentKey < 0)
                contentKey = json.IndexOf("\"content\"", StringComparison.Ordinal);
            if (contentKey < 0) return null;

            int colon = json.IndexOf(':', contentKey + 9);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;

            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '\\' && i < json.Length)
                {
                    char n = json[i++];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 3 < json.Length &&
                                int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else if (c == '"')
                {
                    break;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Trim();
        }

        private static void HandleAssistantReply(string content)
        {
            string display = content;
            string tag = null;

            ExtractTag(ref display, ref tag);

            History.Add(new ChatTurn { Role = "assistant", Content = content });
            TrimHistory();

            if (!string.IsNullOrEmpty(tag))
            {
                var data = CrewmateRegistry.GetPrimary();
                if (data != null)
                    CrewmateAI.ApplyCommand(data, tag);
            }

            display = display.Trim();
            if (string.IsNullOrEmpty(display)) return;

            var primary = CrewmateRegistry.GetPrimary();
            Vector3 pos = primary?.Enemy != null ? primary.Enemy.transform.position : Vector3.zero;
            ulong netId = primary?.NetworkObjectId ?? 0;
            string name = Plugin.CrewmateName?.Value ?? "Buddy";

            NetMessenger.BroadcastCrewmateChat(name, display, pos, netId);
            ProximityChat.TryShowLocal(name, display, pos);

            // Spoken reply via Groq Orpheus (host, 3D at Buddy)
            BuddyTts.Speak(display, pos + Vector3.up * 1.6f);
        }

        private static void ExtractTag(ref string display, ref string tag)
        {
            string[] tags = { "[FOLLOW]", "[STAY]", "[SHIP]", "[FETCH]" };
            foreach (var t in tags)
            {
                int idx = display.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    tag = t.Substring(1, t.Length - 2);
                    display = (display.Substring(0, idx) + display.Substring(idx + t.Length)).Trim();
                    return;
                }
            }
        }
    }
}
