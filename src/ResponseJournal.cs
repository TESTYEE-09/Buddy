using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using BepInEx;

namespace LethalAICrewmate
{
    /// <summary>
    /// Appends every Buddy reply (chat, voice, Realtime tool results and danger callouts)
    /// to a human-readable journal next to the BepInEx log, so the player can review exactly
    /// what Buddy said, to whom, and which deterministic tool result produced it.
    /// Host only: replies are generated on the host and journaled there.
    /// </summary>
    internal static class ResponseJournal
    {
        private const string FileName = "LethalAICrewmate-responses.log";
        private const long MaxBytes = 8L * 1024 * 1024;
        private const int MaxPendingNotes = 32;
        private static readonly object Gate = new object();
        private static readonly Dictionary<long, InputNote> PendingInputs = new Dictionary<long, InputNote>();
        private static readonly Queue<long> PendingOrder = new Queue<long>();
        private static long _nextInputId = 1;
        private static string _resolvedPath;
        private static int _lastPromptHash;
        private static DateTime _lastPromptSnapshotAt = DateTime.MinValue;

        private sealed class InputNote
        {
            internal string Mode;
            internal string Speaker;
            internal string Input;
        }

        internal static string JournalPath => ResolvePath();

        /// <summary>Remove previously collected transcript data when journaling is disabled.</summary>
        internal static void DeleteExistingJournal()
        {
            lock (Gate)
            {
                PendingInputs.Clear();
                PendingOrder.Clear();
                _nextInputId = 1;
                _lastPromptHash = 0;
                _lastPromptSnapshotAt = DateTime.MinValue;
                try
                {
                    string path = ResolvePath();
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogDebug("Response journal cleanup: " + ex.Message);
                }
            }
        }

        /// <summary>Record an incoming player message and return its explicit reply-correlation id.</summary>
        internal static long NoteInput(string mode, string speaker, string input)
        {
            if (!IsEnabled()) return 0;
            lock (Gate)
            {
                while (PendingInputs.Count >= MaxPendingNotes && PendingOrder.Count > 0)
                    PendingInputs.Remove(PendingOrder.Dequeue());
                long id = _nextInputId++;
                if (_nextInputId <= 0) _nextInputId = 1;
                PendingInputs[id] = new InputNote
                {
                    Mode = string.IsNullOrWhiteSpace(mode) ? "system" : mode,
                    Speaker = string.IsNullOrWhiteSpace(speaker) ? "-" : speaker.Trim(),
                    Input = input
                };
                PendingOrder.Enqueue(id);
                return id;
            }
        }

        internal static void Discard(long inputId)
        {
            if (inputId == 0) return;
            lock (Gate) PendingInputs.Remove(inputId);
        }

        /// <summary>
        /// Write a self-contained journal line without consuming a pending input note.
        /// Used by deterministic callouts that have no paired player message.
        /// </summary>
        internal static void RecordDirect(string mode, string speaker, string input, string reply, string toolResult = null)
        {
            try
            {
                if (!IsEnabled()) return;
                var sb = new StringBuilder(320);
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
                sb.Append(Sanitize(string.IsNullOrWhiteSpace(mode) ? "system" : mode)).Append(" | ");
                sb.Append(Sanitize(string.IsNullOrWhiteSpace(speaker) ? "-" : speaker.Trim())).Append(": ");
                sb.Append('"').Append(Sanitize(input)).Append('"');
                sb.Append(" -> ").Append(Sanitize(Plugin.CrewmateName?.Value ?? "Buddy")).Append(": ");
                sb.Append('"').Append(Sanitize(reply)).Append('"');
                if (!string.IsNullOrWhiteSpace(toolResult))
                    sb.Append(" [tool: ").Append(Sanitize(toolResult)).Append(']');
                sb.AppendLine();
                WriteLine(sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Response journal: " + ex.Message);
            }
        }

        /// <summary>Drop unpaired input notes when a session ends so stale pairings cannot leak across lobbies.</summary>
        internal static void ResetSession()
        {
            lock (Gate)
            {
                PendingInputs.Clear();
                PendingOrder.Clear();
                _lastPromptHash = 0;
                _lastPromptSnapshotAt = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Records the exact system prompt Buddy is running, but only when it differs from the last
        /// one written. This is what makes the journal usable for prompt iteration: every reply
        /// below a snapshot was produced by the prompt in that snapshot.
        /// </summary>
        internal static void RecordPromptSnapshot(string systemPrompt)
        {
            try
            {
                if (!IsEnabled() || !IsContextEnabled() || string.IsNullOrWhiteSpace(systemPrompt)) return;
                int hash = systemPrompt.GetHashCode();
                DateTime now = DateTime.UtcNow;
                lock (Gate)
                {
                    if (hash == _lastPromptHash) return;
                    // The prompt carries live pacing and social lines that flip often. Re-snapshot
                    // on change, but never more than once a minute, so the journal stays readable.
                    if (_lastPromptHash != 0 && (now - _lastPromptSnapshotAt).TotalSeconds < 60d) return;
                    _lastPromptHash = hash;
                    _lastPromptSnapshotAt = now;
                }

                var sb = new StringBuilder(systemPrompt.Length + 256);
                sb.Append("=== SYSTEM PROMPT @ ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append(" | Buddy v").Append(Plugin.ModVersion)
                  .Append(" | provider ").Append(OpenAiSecrets.ProviderName)
                  .AppendLine(" ===");
                sb.AppendLine(systemPrompt.TrimEnd());
                sb.AppendLine("=== END SYSTEM PROMPT ===");
                WriteLine(sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Response journal prompt snapshot: " + ex.Message);
            }
        }

        /// <summary>
        /// Records the live sensor block that shaped one specific turn, tagged with the same
        /// correlation id as the reply so a bad answer can be traced to what Buddy could see.
        /// </summary>
        internal static void RecordContext(long inputId, string context)
        {
            try
            {
                if (!IsEnabled() || !IsContextEnabled() || string.IsNullOrWhiteSpace(context)) return;
                var sb = new StringBuilder(context.Length + 128);
                sb.Append("--- CONTEXT #").Append(inputId).Append(" @ ")
                  .Append(DateTime.Now.ToString("HH:mm:ss")).AppendLine(" ---");
                sb.AppendLine(context.TrimEnd());
                sb.AppendLine("--- END CONTEXT ---");
                WriteLine(sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Response journal context: " + ex.Message);
            }
        }

        /// <summary>Write a Buddy reply paired only with its explicitly correlated input.</summary>
        internal static void RecordReply(long inputId, string reply, string toolResult = null)
        {
            try
            {
                string mode = "system", speaker = "-", input = "-";
                lock (Gate)
                {
                    if (inputId != 0 && PendingInputs.TryGetValue(inputId, out InputNote note))
                    {
                        PendingInputs.Remove(inputId);
                        mode = note.Mode;
                        speaker = note.Speaker;
                        input = note.Input;
                    }
                }
                if (!IsEnabled()) return;

                var sb = new StringBuilder(320);
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
                sb.Append(Sanitize(mode)).Append(" | ").Append(Sanitize(speaker)).Append(": ");
                input = Sanitize(input);
                sb.Append('"').Append(input).Append('"');
                sb.Append(" -> ").Append(Sanitize(Plugin.CrewmateName?.Value ?? "Buddy")).Append(": ");
                sb.Append('"').Append(Sanitize(reply)).Append('"');
                if (!string.IsNullOrWhiteSpace(toolResult))
                    sb.Append(" [tool: ").Append(Sanitize(toolResult)).Append(']');
                sb.AppendLine();

                WriteLine(sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Response journal: " + ex.Message);
            }
        }

        private static string ResolvePath()
        {
            if (_resolvedPath != null) return _resolvedPath;
            try
            {
                string root = Paths.BepInExRootPath;
                if (string.IsNullOrWhiteSpace(root)) root = Directory.GetCurrentDirectory();
                _resolvedPath = Path.Combine(root, FileName);
            }
            catch
            {
                _resolvedPath = Path.Combine(Directory.GetCurrentDirectory(), FileName);
            }
            return _resolvedPath;
        }

        private static void WriteLine(string line)
        {
            lock (Gate)
            {
                string path = ResolvePath();
                bool exists = File.Exists(path);
                if (!exists)
                    File.AppendAllText(path,
                        "# LethalAICrewmate response journal - every Buddy input and reply, for prompt tuning.\n" +
                        "# Format: [time] mode | speaker: \"input\" -> Buddy: \"reply\" [tool: result]\n" +
                        "# Blocks marked SYSTEM PROMPT and CONTEXT show exactly what produced the replies below them.\n",
                        Encoding.UTF8);
                File.AppendAllText(path, line, Encoding.UTF8);

                // Bound the journal: if it outgrows the cap, keep the newest half.
                var info = new FileInfo(path);
                if (info.Length > MaxBytes)
                {
                    try
                    {
                        string all = File.ReadAllText(path, Encoding.UTF8);
                        int keepChars = Math.Min(all.Length, (int)(MaxBytes / 4));
                        int start = all.Length - keepChars;
                        int newline = all.IndexOf('\n', start);
                        if (newline >= 0 && newline + 1 < all.Length) start = newline + 1;
                        File.WriteAllText(path, all.Substring(start), Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogDebug("Response journal trim: " + ex.Message);
                    }
                }
            }
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            var clean = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                if (char.IsControl(ch))
                {
                    clean.Append(' ');
                    continue;
                }
                if (ch == '\\' || ch == '"') clean.Append('\\');
                clean.Append(ch);
            }
            return clean.ToString().Trim();
        }

        private static bool IsEnabled() => Plugin.SaveResponses != null && Plugin.SaveResponses.Value;

        private static bool IsContextEnabled() => Plugin.SavePromptContext != null && Plugin.SavePromptContext.Value;
    }
}
