using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LethalAICrewmate
{
    internal static class OpenAiRealtimeTextClient
    {
        internal sealed class Result
        {
            public string Text;
            public string Error;
            public bool Success => string.IsNullOrEmpty(Error) && !string.IsNullOrWhiteSpace(Text);
        }

        internal static async Task<Result> SendAsync(string model, string apiKey, string responseCreateJson)
        {
            var result = new Result();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
            using (var socket = new ClientWebSocket())
            {
                try
                {
                    socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
                    var uri = new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(model));
                    await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);

                    var buffer = new byte[16 * 1024];
                    bool sessionReady = false;
                    while (!sessionReady && socket.State == WebSocketState.Open)
                    {
                        string initial = await ReceiveMessageAsync(socket, buffer, timeout.Token).ConfigureAwait(false);
                        if (initial == null) break;
                        string initialType = ReadJsonString(initial, "type");
                        if (string.Equals(initialType, "session.created", StringComparison.Ordinal))
                            sessionReady = true;
                        else if (string.Equals(initialType, "error", StringComparison.Ordinal))
                        {
                            result.Error = ReadJsonString(initial, "message") ?? initial;
                            return result;
                        }
                    }
                    if (!sessionReady)
                    {
                        result.Error = "Realtime socket closed before session.created.";
                        return result;
                    }

                    byte[] requestBytes = Encoding.UTF8.GetBytes(responseCreateJson);
                    await socket.SendAsync(new ArraySegment<byte>(requestBytes), WebSocketMessageType.Text, true, timeout.Token)
                        .ConfigureAwait(false);

                    var accumulated = new StringBuilder();
                    while (socket.State == WebSocketState.Open && !timeout.IsCancellationRequested)
                    {
                        string message = await ReceiveMessageAsync(socket, buffer, timeout.Token).ConfigureAwait(false);
                        if (message == null) break;

                        string type = ReadJsonString(message, "type");
                        if (string.Equals(type, "response.output_text.delta", StringComparison.Ordinal))
                        {
                            string delta = ReadJsonString(message, "delta");
                            if (!string.IsNullOrEmpty(delta)) accumulated.Append(delta);
                        }
                        else if (string.Equals(type, "response.output_text.done", StringComparison.Ordinal))
                        {
                            if (accumulated.Length == 0)
                            {
                                string text = ReadJsonString(message, "text");
                                if (!string.IsNullOrEmpty(text)) accumulated.Append(text);
                            }
                        }
                        else if (string.Equals(type, "error", StringComparison.Ordinal))
                        {
                            result.Error = ReadJsonString(message, "message") ?? message;
                            break;
                        }
                        else if (string.Equals(type, "response.done", StringComparison.Ordinal))
                        {
                            if (accumulated.Length == 0)
                            {
                                string nested = LlmClient.ParseResponsesContent(message);
                                if (!string.IsNullOrEmpty(nested)) accumulated.Append(nested);
                            }
                            break;
                        }
                    }

                    result.Text = accumulated.ToString().Trim();
                    if (string.IsNullOrEmpty(result.Text) && string.IsNullOrEmpty(result.Error))
                        result.Error = timeout.IsCancellationRequested
                            ? "Realtime request timed out."
                            : "Realtime response completed without output text.";
                }
                catch (OperationCanceledException)
                {
                    result.Error = "Realtime request timed out.";
                }
                catch (Exception ex)
                {
                    result.Error = ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    try
                    {
                        if (socket.State == WebSocketState.Open)
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                                .ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            return result;
        }

        private static async Task<string> ReceiveMessageAsync(ClientWebSocket socket, byte[] buffer, CancellationToken token)
        {
            using (var stream = new MemoryStream())
            {
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close) return null;
                    stream.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static string ReadJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            int keyIndex = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colon = json.IndexOf(':', keyIndex + key.Length + 2);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i++] != '"') return null;

            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') break;
                if (c != '\\' || i >= json.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char escaped = json[i++];
                switch (escaped)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 3 < json.Length && int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(escaped); break;
                }
            }
            return sb.ToString();
        }
    }
}
