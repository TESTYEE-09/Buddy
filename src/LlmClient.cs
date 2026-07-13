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
        private const int MaxTokens = 220;

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
            public bool WantVision;
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

            // Live sensors so Buddy cannot invent monsters that are not there
            string sensors = GameSensors.BuildLiveContext();
            string content = sensors + "\n" + playerName + " says: " + message;
            if (isCommand)
                content += " (They issued a command — acknowledge briefly in character.)";
            content += "\nRULE: Only mention entities listed under Nearby entities. If NONE, do not invent threats.";

            Enqueue(content, isObservation: false, withVision: true);
        }

        public static void EnqueueObservation(string summary)
        {
            if (!HasApiKey) return;
            string sensors = GameSensors.BuildLiveContext();
            Enqueue(sensors + "\n[Observation] " + summary, isObservation: true, withVision: false);
        }

        private static void Enqueue(string userContent, bool isObservation, bool withVision = false)
        {
            if (Queue.Count >= MaxQueue)
            {
                Plugin.Log?.LogInfo("LLM queue full; dropping request.");
                return;
            }
            Queue.Enqueue(new PendingRequest
            {
                UserContent = userContent,
                IsObservation = isObservation,
                WantVision = withVision
            });
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

            string imageB64 = null;
            if (pending.WantVision)
                VisionCapture.TryCaptureJpegBase64(out imageB64);

            string body = BuildRequestJson(systemPrompt, History, imageB64);
            string model = Plugin.Model?.Value ?? "qwen/qwen3.6-27b";

            using (var uwr = new UnityWebRequest(GroqChatEndpoint, "POST"))
            {
                byte[] raw = Encoding.UTF8.GetBytes(body);
                uwr.uploadHandler = new UploadHandlerRaw(raw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.SetRequestHeader("Authorization", "Bearer " + Plugin.ApiKey.Value);

                Plugin.Log?.LogInfo($"Groq chat → model={model} vision={(imageB64 != null)}");
                yield return uwr.SendWebRequest();

                bool ok = string.IsNullOrEmpty(uwr.error)
                          && uwr.responseCode >= 200
                          && uwr.responseCode < 300;
                bool needRetryNoVision = false;

                if (!ok)
                {
                    Plugin.Log?.LogWarning($"Groq chat HTTP {uwr.responseCode}: {uwr.error} {uwr.downloadHandler?.text}");
                    needRetryNoVision = imageB64 != null;
                }
                else
                {
                    try
                    {
                        string responseText = uwr.downloadHandler?.text ?? "";
                        string content = ParseAssistantContent(responseText);
                        content = StripThinking(content);
                        if (!string.IsNullOrEmpty(content))
                            HandleAssistantReply(content);
                        else
                            Plugin.Log?.LogWarning("Groq chat: empty assistant content (after stripping thinking)");
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogError($"LLM response handling: {ex}");
                    }
                }

                if (needRetryNoVision)
                {
                    Plugin.Log?.LogInfo("Retrying chat without vision…");
                    yield return SendRequestNoVision(systemPrompt, model);
                }
            }

            _inFlight = false;
            _running = null;
        }

        private static IEnumerator SendRequestNoVision(string systemPrompt, string model)
        {
            string body = BuildRequestJson(systemPrompt, History, null);
            using (var uwr = new UnityWebRequest(GroqChatEndpoint, "POST"))
            {
                byte[] raw = Encoding.UTF8.GetBytes(body);
                uwr.uploadHandler = new UploadHandlerRaw(raw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.SetRequestHeader("Authorization", "Bearer " + Plugin.ApiKey.Value);
                yield return uwr.SendWebRequest();
                if (string.IsNullOrEmpty(uwr.error) && uwr.responseCode >= 200 && uwr.responseCode < 300)
                {
                    string content = StripThinking(ParseAssistantContent(uwr.downloadHandler?.text ?? ""));
                    if (!string.IsNullOrEmpty(content))
                        HandleAssistantReply(content);
                }
                else
                    Plugin.Log?.LogWarning($"Groq chat retry HTTP {uwr.responseCode}: {uwr.error}");
            }
        }

        private static string BuildSystemPrompt()
        {
            var name = Plugin.CrewmateName?.Value ?? "Buddy";
            var personality = Plugin.Personality?.Value
                              ?? "Jumpy LC employee. Short radio callouts. Only real game threats.";
            var sb = new StringBuilder(14000);
            sb.Append("You are ").Append(name).Append(", an employee on a Lethal Company crew. ");
            sb.Append(personality).Append(' ');
            sb.Append("TRUTH RULES (highest priority): ");
            sb.Append("Every user message includes a [SENSOR] block with REAL nearby entities. ");
            sb.Append("NEVER invent monsters, scrap, or hazards not in SENSOR or WIKI FACTS. ");
            sb.Append("If SENSOR says Nearby entities: NONE, you must not claim to see a Snare Flea, Coil-Head, or anything else. ");
            sb.Append("If an image is attached, describe only what is clearly visible — do not invent enemies. ");
            sb.Append("Never invent hull leaks, oxygen failure, or fake ship damage. ");
            sb.Append("If unsure, say you're not sure. Keep under 22 words. No thinking tags. No markdown. ");
            sb.Append("Movement tags: [FOLLOW] [STAY] [SHIP] [FETCH]. ");
            sb.Append("In orbit only, if player asks to route/buy: [ROUTE:moonname] [BUY:item] [TERMINAL:cmd]. ");
            sb.Append('\n').Append(WikiKnowledge.Body);
            return sb.ToString();
        }

        private static void TrimHistory()
        {
            while (History.Count > MaxHistory)
                History.RemoveAt(0);
        }

        private static string BuildRequestJson(string systemPrompt, List<ChatTurn> history, string imageJpegBase64)
        {
            string model = Plugin.Model?.Value ?? "qwen/qwen3.6-27b";
            var sb = new StringBuilder(Math.Max(8192, (imageJpegBase64?.Length ?? 0) + 4096));
            sb.Append("{\"model\":\"").Append(Escape(model)).Append("\",");
            sb.Append("\"max_tokens\":").Append(MaxTokens).Append(',');
            sb.Append("\"temperature\":0.6,");
            if (model.IndexOf("qwen", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sb.Append("\"reasoning_effort\":\"none\",");
                sb.Append("\"reasoning_format\":\"hidden\",");
            }
            sb.Append("\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"").Append(Escape(systemPrompt)).Append("\"}");

            for (int hi = 0; hi < history.Count; hi++)
            {
                var turn = history[hi];
                bool lastUserWithVision = imageJpegBase64 != null
                                          && hi == history.Count - 1
                                          && turn.Role == "user";

                sb.Append(',');
                if (lastUserWithVision)
                {
                    // OpenAI-compatible multimodal content array
                    sb.Append("{\"role\":\"user\",\"content\":[");
                    sb.Append("{\"type\":\"text\",\"text\":\"").Append(Escape(turn.Content)).Append("\"},");
                    sb.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,");
                    sb.Append(imageJpegBase64);
                    sb.Append("\"}}");
                    sb.Append("]}");
                }
                else
                {
                    sb.Append("{\"role\":\"").Append(Escape(turn.Role)).Append("\",\"content\":\"")
                      .Append(Escape(turn.Content)).Append("\"}");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Strip Qwen/OpenAI-style thinking blocks if the API still leaks them.</summary>
        internal static string StripThinking(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;

            // Remove <think>...</think> and variants (including unclosed)
            content = RemoveTaggedBlock(content, "think");
            content = RemoveTaggedBlock(content, "thinking");
            content = RemoveTaggedBlock(content, "reasoning");
            content = RemoveTaggedBlock(content, "thought");

            // Remove "Thinking:" ... until blank line / end
            int thinkIdx = content.IndexOf("Thinking:", StringComparison.OrdinalIgnoreCase);
            if (thinkIdx >= 0)
            {
                int end = content.IndexOf("\n\n", thinkIdx, StringComparison.Ordinal);
                if (end < 0) end = content.Length;
                content = content.Remove(thinkIdx, end - thinkIdx);
            }

            // If model still dumped analysis then final line, keep last non-empty short line
            var lines = content.Replace("\r\n", "\n").Split('\n');
            if (lines.Length > 3)
            {
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("<") || line.StartsWith("Thinking", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Prefer a crew-line under ~200 chars
                    if (line.Length <= 220)
                    {
                        content = line;
                        break;
                    }
                }
            }

            return content.Trim();
        }

        private static string RemoveTaggedBlock(string content, string tag)
        {
            // <tag>...</tag>
            string open = "<" + tag + ">";
            string close = "</" + tag + ">";
            while (true)
            {
                int a = content.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (a < 0) break;
                int b = content.IndexOf(close, a, StringComparison.OrdinalIgnoreCase);
                if (b < 0)
                {
                    content = content.Substring(0, a).Trim();
                    break;
                }
                content = content.Remove(a, (b + close.Length) - a);
            }

            // <|tag|> ... <|/tag|> style
            open = "<|" + tag + "|>";
            close = "<|/" + tag + "|>";
            while (true)
            {
                int a = content.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (a < 0) break;
                int b = content.IndexOf(close, a, StringComparison.OrdinalIgnoreCase);
                if (b < 0)
                {
                    content = content.Substring(0, a).Trim();
                    break;
                }
                content = content.Remove(a, (b + close.Length) - a);
            }

            return content;
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
            string display = StripThinking(content);
            string moveTag = null;
            ExtractMoveTag(ref display, ref moveTag);

            // Terminal tags while in orbit
            string cleaned = display;
            string termFb = TerminalBuddy.ApplyLlmTags(display, ref cleaned);
            display = cleaned;

            History.Add(new ChatTurn { Role = "assistant", Content = display });
            TrimHistory();

            if (!string.IsNullOrEmpty(moveTag))
            {
                var data = CrewmateRegistry.GetPrimary();
                if (data != null)
                    CrewmateAI.ApplyCommand(data, moveTag);
            }

            display = display.Trim();
            if (string.IsNullOrEmpty(display) && !string.IsNullOrEmpty(termFb))
                display = termFb;
            if (string.IsNullOrEmpty(display)) return;

            var primary = CrewmateRegistry.GetPrimary();
            Vector3 pos = primary?.Enemy != null ? primary.Enemy.transform.position : Vector3.zero;
            ulong netId = primary?.NetworkObjectId ?? 0;
            string name = Plugin.CrewmateName?.Value ?? "Buddy";

            NetMessenger.BroadcastCrewmateChat(name, display, pos, netId);
            ProximityChat.TryShowLocal(name, display, pos);
            BuddyTts.Speak(display, pos + Vector3.up * 1.6f);

            if (!string.IsNullOrEmpty(termFb) && termFb != display)
            {
                ProximityChat.TryShowLocal(name, termFb, pos);
                Plugin.Log?.LogInfo($"Terminal feedback: {termFb}");
            }
        }

        private static void ExtractMoveTag(ref string display, ref string tag)
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
