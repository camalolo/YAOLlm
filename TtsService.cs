using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace YAOLlm;

/// <summary>
/// Text-to-speech service using Microsoft Edge TTS (free) via direct WebSocket.
/// Buffers the MP3 audio, then plays it once the turn completes.
/// </summary>
public class TtsService : IDisposable
{
    private const string DefaultVoice = "en-US-AriaNeural";
    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string SecMsGecVersion = "1-143.0.3650.75";
    private const double WinEpoch = 11644473600.0;
    private const int Mp3Bitrate = 48000;

    private readonly string _voice;
    private readonly Logger? _logger;
    private CancellationTokenSource? _currentCts;
    private WaveOutEvent? _waveOut;
    private volatile bool _isSpeaking;
    private volatile bool _disposed;
    private double _clockSkewSeconds;

    // Used for clock skew correction if 403 errors occur
    public void AdjustClockSkew(double skewSeconds) => _clockSkewSeconds += skewSeconds;

    public bool IsSpeaking => _isSpeaking;

    public TtsService(string? voice = null, Logger? logger = null)
    {
        _voice = string.IsNullOrEmpty(voice) ? DefaultVoice : voice;
        _logger = logger;
    }

    public async Task SpeakAsync(string text, CancellationToken externalToken)
    {
        if (string.IsNullOrWhiteSpace(text) || _disposed)
            return;

        Stop();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _currentCts = linkedCts;
        _isSpeaking = true;

        try
        {
            _logger?.Log($"[TTS] Speaking: {text}");

            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0");
            ws.Options.SetRequestHeader("Cookie", $"muid={GenerateMuid()};");

            var connectionId = Guid.NewGuid().ToString("N");
            var secMsGec = GenerateSecMsGec();
            var url = $"{WssUrl}?TrustedClientToken={TrustedClientToken}&ConnectionId={connectionId}&Sec-MS-GEC={secMsGec}&Sec-MS-GEC-Version={SecMsGecVersion}";

            await ws.ConnectAsync(new Uri(url), linkedCts.Token);

            // Send configuration
            var timestamp = FormatTimestamp();
            var configMsg =
                $"X-Timestamp:{timestamp}\r\n" +
                "Content-Type:application/json; charset=utf-8\r\n" +
                "Path:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
                "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";

            await ws.SendAsync(Encoding.UTF8.GetBytes(configMsg), WebSocketMessageType.Text, true, linkedCts.Token);

            // Send SSML
            var requestId = Guid.NewGuid().ToString("N");
            var escapedText = EscapeXml(text);
            var ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                        $"<voice name='{_voice}'><prosody pitch='+0Hz' rate='+0%' volume='+0%'>{escapedText}</prosody></voice></speak>";

            var ssmlMsg =
                $"X-RequestId:{requestId}\r\n" +
                "Content-Type:application/ssml+xml\r\n" +
                $"X-Timestamp:{timestamp}Z\r\n" +
                "Path:ssml\r\n\r\n" +
                ssml;

            await ws.SendAsync(Encoding.UTF8.GetBytes(ssmlMsg), WebSocketMessageType.Text, true, linkedCts.Token);

            // Receive audio chunks and accumulate; a single audio message may be
            // split across multiple WebSocket frames, so buffer until EndOfMessage.
            var ms = new MemoryStream();
            var buffer = new byte[16384];
            var messageBuffer = new MemoryStream();
            var receiving = true;

            while (receiving && ws.State == WebSocketState.Open)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), linkedCts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                messageBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var message = messageBuffer.ToArray();
                messageBuffer.SetLength(0);

                if (result.MessageType == WebSocketMessageType.Binary && message.Length > 2)
                {
                    // Binary message: first 2 bytes = header length (big-endian uint16)
                    var headerLength = (message[0] << 8) | message[1];

                    if (headerLength > 0 && message.Length > headerLength + 2)
                    {
                        // Parse headers to find Path
                        var headerStr = Encoding.UTF8.GetString(message, 2, headerLength);

                        if (!headerStr.Contains("Path:audio"))
                            continue;

                        // Audio data follows headers
                        ms.Write(message, 2 + headerLength, message.Length - 2 - headerLength);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Text && message.Length > 0)
                {
                    var textMsg = Encoding.UTF8.GetString(message);
                    if (textMsg.Contains("Path:turn.end"))
                        receiving = false;
                }
            }

            linkedCts.Token.ThrowIfCancellationRequested();

            if (ms.Length == 0)
            {
                _logger?.Log("[TTS] No audio data received.");
                return;
            }

            // Play the audio
            ms.Position = 0;
            _waveOut = new WaveOutEvent();

            using (ms)
            using (var reader = new Mp3FileReader(ms))
            {
                _waveOut.Init(reader);
                _waveOut.Play();

                while (_waveOut.PlaybackState == PlaybackState.Playing)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                    await Task.Delay(50, linkedCts.Token);
                }
            }

            _logger?.Log("[TTS] Speech completed.");
        }
        catch (OperationCanceledException)
        {
            _logger?.Log("[TTS] Speech cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.Log($"[TTS] Error: {ex.Message}");
        }
        finally
        {
            StopWaveOut();
            _isSpeaking = false;
            if (_currentCts == linkedCts)
                _currentCts = null;
            linkedCts.Dispose();
        }
    }

    public void Stop()
    {
        if (_currentCts != null && !_currentCts.IsCancellationRequested)
        {
            _currentCts.Cancel();
            _currentCts = null;
        }
        StopWaveOut();
        _isSpeaking = false;
    }

    private void StopWaveOut()
    {
        var wo = _waveOut;
        _waveOut = null;
        if (wo != null)
        {
            if (wo.PlaybackState == PlaybackState.Playing)
                wo.Stop();
            wo.Dispose();
        }
    }

    private string GenerateSecMsGec()
    {
        var ticks = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _clockSkewSeconds;
        ticks += WinEpoch;
        ticks -= ticks % 300;
        ticks *= 1e7;

        var strToHash = $"{ticks:F0}{TrustedClientToken}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(strToHash));
        return BitConverter.ToString(hash).Replace("-", "").ToUpper();
    }

    private static string GenerateMuid()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[16];
        rng.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "").ToLower();
    }

    private static string FormatTimestamp()
    {
        return DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            + " GMT+0000 (Coordinated Universal Time)";
    }

    private static string EscapeXml(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                   .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
