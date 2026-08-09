using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using BepInEx;

namespace LethalAICrewmate
{
    /// <summary>
    /// Appends every Buddy reply (chat, voice, deterministic commands and danger callouts)
    /// to a human-readable journal next to the BepInEx log, so the player can review exactly
    /// what Buddy said, to whom, and which deterministic tool result produced it.
    /// Host only: replies are generated on the host and journaled there.
    /// </summary>
    internal static class ResponseJournal
    {
        private const string FileName = "LethalAICrewmate-responses.log";
        private const long MaxBytes = 2L * 1024 * 1024;
        private const int MaxPendingNotes = 32;
        private static readonly object Gate = new object();
        private static readonly Dictionary<long, InputNote> PendingInputs = new Dictionary<long, InputNote>();
        private static readonly Queue<long> PendingOrder = new Queue<long>();
        private static long _nextInputId = 1;
        private static string _resolvedPath;

        private sealed class InputNote
        {
            internal string Mode;
            internal string Speaker;
            internal string Input;
        }

        internal static string JournalPath => ResolvePath();

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
                    Input = Sanitize(input)
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
                sb.Append(string.IsNullOrWhiteSpace(mode) ? "system" : mode).Append(" | ");
                sb.Append(string.IsNullOrWhiteSpace(speaker) ? "-" : speaker.Trim()).Append(": ");
                sb.Append('"').Append(Sanitize(input)).Append('"');
                sb.Append(" -> ").Append(Plugin.CrewmateName?.Value ?? "Buddy").Append(": ");
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
                sb.Append(mode).Append(" | ").Append(speaker).Append(": ");
                sb.Append('"').Append(input).Append('"');
                sb.Append(" -> ").Append(Plugin.CrewmateName?.Value ?? "Buddy").Append(": ");
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
                        "# LethalAICrewmate response journal - every Buddy reply with its paired input.\n" +
                        "# Format: [time] mode | speaker: \"input\" -> Buddy: \"reply\" [tool: result]\n",
                        Encoding.UTF8);
                File.AppendAllText(path, line, Encoding.UTF8);

                // Bound the journal: if it outgrows the cap, keep the newest half.
                var info = new FileInfo(path);
                if (info.Length > MaxBytes)
                {
                    try
                    {
                        byte[] keep = new byte[MaxBytes / 2];
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            fs.Seek(-keep.Length, SeekOrigin.End);
                            fs.Read(keep, 0, keep.Length);
                        }
                        File.WriteAllBytes(path, keep);
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
            return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static bool IsEnabled() => Plugin.SaveResponses != null && Plugin.SaveResponses.Value;
    }
}
