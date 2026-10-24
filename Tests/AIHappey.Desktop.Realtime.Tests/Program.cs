using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

var count = 0;
void Check(bool success, string name) { if (!success) throw new Exception("FAILED: " + name); Console.WriteLine("PASS: " + name); count++; }
var settings = new RealtimePreferences();
var now = DateTimeOffset.Parse("2026-10-24T08:00:00Z");
var tool = JsonNode.Parse("""{"name":"lookup","description":"Lookup","parameters":{"type":"object","properties":{"value":{"type":["string","null"]}}}}""")!.AsObject();
var openai = RealtimeConfiguration.Session("openai/gpt-realtime-2", settings, "Be helpful", [tool], "auto");
Check(openai["model"]!.ToString() == "gpt-realtime-2" && openai["audio"]!["output"]!["voice"]!.ToString() == "marin", "OpenAI browser defaults and dynamic model IDs");
Check(openai["audio"]!["input"]!["turn_detection"]!["type"]!.ToString() == "semantic_vad", "OpenAI semantic VAD default");
var token = RealtimeConfiguration.TokenRequest("openai/gpt-realtime-2", settings, openai, now);
Check(token["model"]!.ToString() == "openai/gpt-realtime-2" && JsonNode.DeepEquals(token["providerOptions"]!["openai"]!["session"], openai), "backend token provisioning retains qualified model and exact OpenAI session");
var google = RealtimeConfiguration.Session("google/gemini-live", settings, "Be helpful", [tool], "auto");
google["temperature"] = .4;
var setup = RealtimeConfiguration.GoogleSetup("google/gemini-live", google);
Check(setup["model"]!.ToString() == "models/gemini-live" && setup["temperature"] is null && setup["generationConfig"]!["temperature"]!.GetValue<double>() == .4, "Google SDK configuration maps to wire generationConfig");
Check(setup["tools"]![0]!["functionDeclarations"]![0]!["parametersJsonSchema"]!["properties"]!["value"]!["type"] is JsonArray, "Google preserves full nullable MCP JSON Schema");
var googleToken = RealtimeConfiguration.TokenRequest("google/gemini-live", settings, google, now)["providerOptions"]!["google"]!;
Check(JsonNode.DeepEquals(googleToken["bidiGenerateContentSetup"], setup), "Google token constraints match connection setup exactly");
Check(DateTimeOffset.Parse(googleToken["expireTime"]!.ToString()) == now.AddMinutes(30) && DateTimeOffset.Parse(googleToken["newSessionExpireTime"]!.ToString()) == now.AddMinutes(1), "Google token lifetime defaults");
Check(googleToken["cameraFrameRate"] is null && googleToken["jpegQuality"] is null, "local media options never enter token constraints");
Check(RealtimeConfiguration.GoogleModel("google/models/gemini-live") == "models/gemini-live" && RealtimeConfiguration.GoogleModel("models/gemini-live") == "models/gemini-live", "Google model normalization is idempotent");
settings.ProviderOptions["google"] = JsonNode.Parse("""{"expireTime":"10m","newSessionExpireTime":"30s","lockAdditionalFields":["generationConfig"],"future":{"keep":true}}""");
var clone = settings.Clone(); clone.ProviderOptions["google"]!["expireTime"] = "15m";
Check(settings.ProviderOptions["google"]!["expireTime"]!.ToString() == "10m", "preferences clone isolates nested provider settings");
var customized = RealtimeConfiguration.TokenRequest("google/gemini-live", settings, google, now)["providerOptions"]!["google"]!;
Check(DateTimeOffset.Parse(customized["expireTime"]!.ToString()) == now.AddMinutes(10) && customized["fieldMask"] is JsonArray, "relative expiry and lock fields map to backend wire payload");
Check(RealtimeConfiguration.Session("google/gemini-live", settings, "", [tool], "none")["tools"] is null, "tool choice none suppresses Google function declarations");
try { _ = RealtimeConfiguration.Expiry("not-an-expiry", now, TimeSpan.Zero); Check(false, "invalid expiry rejected"); } catch (ArgumentException) { Check(true, "invalid expiry rejected"); }
try { new RealtimePreferences { JpegQuality = double.NaN }.Validate(); Check(false, "invalid media settings rejected"); } catch (ArgumentException) { Check(true, "invalid media settings rejected"); }
try { _ = RealtimeSession.Parse("[]"u8); Check(false, "nonobject event rejected"); } catch (JsonException) { Check(true, "nonobject event rejected"); }
using (var fragmented = new FixtureSocket(Encoding.UTF8.GetBytes("{\"serverContent\":{\"outputTranscription\":{\"text\":\"héllo\"}}}"), 3))
    Check((await GoogleRealtimeSession.ReceiveAsync(fragmented, CancellationToken.None))?["serverContent"]?["outputTranscription"]?["text"]?.ToString() == "héllo", "fragmented binary UTF-8 events reassemble without splitting multibyte characters");
using (var oversized = new FixtureSocket(new byte[RealtimeSession.MaximumMessageBytes + 1], 8192))
    try { await GoogleRealtimeSession.ReceiveAsync(oversized, CancellationToken.None); Check(false, "oversized websocket message rejected"); } catch (InvalidDataException) { Check(true, "oversized websocket message rejected"); }
using (var closed = new FixtureSocket([], 1)) Check(await GoogleRealtimeSession.ReceiveAsync(closed, CancellationToken.None) is null, "websocket close is terminal, not an empty JSON event");
using (var encoder = new AudioEncoder(includeOpus: true))
using (var decoder = new AudioEncoder(includeOpus: true))
{
    var opus = encoder.SupportedFormats.First(f => f.Codec == AudioCodecsEnum.OPUS);
    var samples = Enumerable.Range(0, 960).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 4000)).ToArray();
    var encoded = encoder.EncodeAudio(samples, opus); var decoded = decoder.DecodeAudio(encoded, opus);
    Check(opus.ClockRate == 48000 && encoded.Length > 0 && decoded.Length == samples.Length, $"native Opus roundtrip preserves 20ms mono 48kHz frames ({decoded.Length} samples)");
    using var peer = new RTCPeerConnection(); peer.addTrack(new MediaStreamTrack(opus));
    var data = await peer.createDataChannel("oai-events"); var offer = peer.createOffer(); await peer.setLocalDescription(offer);
    Check(offer.sdp.Contains("opus/48000", StringComparison.OrdinalIgnoreCase) && offer.sdp.Contains("m=application") && data.label == "oai-events", "native SDP offers Opus audio and OpenAI data channel without a browser");
    peer.close();
}
await using (var session = new OpenAiRealtimeSession(_ => throw new Exception("Must not mint a token before startup"))) { await session.DisposeAsync(); await session.DisposeAsync(); Check(true, "native WebRTC disposal is safe before startup and idempotent"); }
foreach (var rate in new[] { 16000, 48000 })
{
    var framing = new RealtimePcmInput(rate); var output = new List<byte[]>();
    void Save(byte[] pcm) => output.Add(pcm.ToArray());
    var silent = new byte[rate * 2];
    // Arbitrary, odd byte packet boundaries must produce aligned, exactly 20ms frames.
    for (var offset = 0; offset < silent.Length; offset += 137)
        framing.Push(silent.AsSpan(offset, Math.Min(137, silent.Length - offset)), false, Save);
    Check(output.Count == 44 && output.All(frame => frame.Length == rate / 50 * 2 && frame.All(b => b == 0)), $"{rate}Hz silence stays digital silence with fixed 20ms cadence and pre-roll");
    framing.Reset(); output.Clear();
    var random = new Random(37); var noise = new byte[rate * 2];
    for (var i = 0; i < noise.Length; i += 2) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(noise.AsSpan(i), (short)random.Next(-30, 31));
    framing.Push(noise, false, Save);
    Check(output.All(frame => frame.All(b => b == 0)), $"{rate}Hz low background noise cannot open the gate");
    framing.Reset(); output.Clear();
    var voice = new byte[rate * 2];
    for (var i = 0; i < voice.Length / 2; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(voice.AsSpan(i * 2), (short)(Math.Sin(i * 2 * Math.PI * 220 / rate) * 3000));
    framing.Push(voice, false, Save);
    Check(output.Count == 44 && output[0].Any(b => b != 0), $"{rate}Hz sustained voiced input opens the gate without losing onset or bursting frames");
    framing.Push(new byte[rate * 2], false, Save);
    Check(output.TakeLast(10).All(frame => frame.All(b => b == 0)), $"{rate}Hz hangover ends and the gate closes after speech");
    framing.Reset(); output.Clear();
    framing.Push(voice, true, Save);
    Check(output.All(frame => frame.All(b => b == 0)), $"{rate}Hz WASAPI Silent flag overrides untrusted buffer contents");
}
var queue = new RealtimePcmPlayback(24000);
var burst = new byte[24000 * 2 * 15]; burst[0] = 42; queue.Add(burst);
Check(queue.BufferedBytes == burst.Length, "Google 15-second generated audio burst fits independently of device latency");
var rendered = new byte[960]; queue.Read(rendered, 0, rendered.Length);
Check(rendered[0] == 42 && queue.BufferedBytes == burst.Length - rendered.Length, "render queue consumes exact bytes on the playback clock");
queue.Clear(); queue.Read(rendered, 0, rendered.Length);
Check(queue.BufferedBytes == 0 && rendered.All(b => b == 0), "interruption discards all queued audio and underrun is digital silence");
try { queue.Add(new byte[queue.MaximumBytes + 2]); Check(false, "playback safety cap"); }
catch (RealtimeFailureException error) { Check(error.Code == "playback-backlog", "playback safety cap has a distinct token-safe failure reason"); }
var navigation = new NavigationPreferences(); navigation.SetHidden("realtime", true);
Check(navigation.IsHidden("realtime") && NavigationPreferences.PrimaryKeys.Take(3).SequenceEqual(["new", "realtime", "search-conversations"]), "Realtime primary navigation matches browser position and supports hide/show");
Console.WriteLine($"All {count} realtime protocol/media checks passed. No microphones or provider credentials were used.");

sealed class FixtureSocket(byte[] bytes, int fragment) : WebSocket
{
    private int offset;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override void Dispose() { }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => Task.CompletedTask;
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) => Task.CompletedTask;
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (bytes.Length == 0) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        var length = Math.Min(Math.Min(fragment, buffer.Count), bytes.Length - offset);
        bytes.AsSpan(offset, length).CopyTo(buffer.AsSpan()); offset += length;
        return Task.FromResult(new WebSocketReceiveResult(length, WebSocketMessageType.Binary, offset == bytes.Length));
    }
}
