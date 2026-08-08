using System;
using System.IO;
using System.Text;
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
        private static readonly System.Collections.Generic.Queue<InputNote> PendingInputs =
            new System.Collections.Generic.Queue<InputNote>();
        private static string _resolvedPath;

        private sealed class InputNote
        {
            internal string Mode;
            internal string Speaker;
            internal string Input;
        }

        /// <summary>Record an incoming player message so the next Buddy reply can be paired with it.</summary>
        internal static void NoteInput(string mode, string speaker, string input)
        {
            lock (Gate)
            {
                if (PendingInputs.Count >= MaxPendingNotes) PendingInputs.Dequeue();
                PendingInputs.Enqueue(new InputNote
                {
                    Mode = string.IsNullOrWhiteSpace(mode) ? "system" : mode,
                    Speaker = string.IsNullOrWhiteSpace(speaker) ? "-" : speaker.Trim(),
                    Input = Sanitize(input)
                });
            }
        }

        /// <summary>
        /// Write a self-contained journal line without consuming a pending input note.
        /// Used by deterministic callouts that have no paired player message.
        /// </summary>
        internal static void RecordDirect(string mode, string speaker, string input, string reply, string toolResult = null)
        {
            try
            {
                if (Plugin.SaveResponses != null && !Plugin.SaveResponses.Value) return;
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
            lock (Gate) PendingInputs.Clear();
        }

        /// <summary>Write a Buddy reply to the journal, paired with the oldest unpaired input note.</summary>
        internal static void RecordReply(string reply, string toolResult = null)
        {
            try
            {
                if (Plugin.SaveResponses != null && !Plugin.SaveResponses.Value) return;
                string mode = "system", speaker = "-", input = "-";
                lock (Gate)
                {
                    if (PendingInputs.Count > 0)
                    {
                        InputNote note = PendingInputs.Dequeue();
                        mode = note.Mode;
                        speaker = note.Speaker;
                        input = note.Input;
                    }
                }

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
                        "# LethalAICrewmate response journal — every Buddy reply with its paired input.\n" +
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
    }
}
