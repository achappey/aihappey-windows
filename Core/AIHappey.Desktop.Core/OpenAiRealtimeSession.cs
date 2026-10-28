using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace AIHappey.Desktop.Core;

/// <summary>Native WebRTC. Authentication and JSON protocol remain owned by the application.</summary>
public sealed class OpenAiRealtimeSession(Func<CancellationToken, Task<string>> tokenFactory,
    Func<string, CancellationToken, Task<string>>? sessionFactory = null, bool live = false,
    ITranscriptionAudioInput? transcriptionInput = null) : RealtimeSession
{
    private readonly RTCPeerConnection peer = new();
    private readonly AudioEncoder encoder = new(includeOpus: true);
    private readonly AudioEncoder decoder = new(includeOpus: true);
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly object mediaGate = new();
    private RTCDataChannel? data;
    private RealtimeAudio? audio;
    private IDisposable? transcriptionCapture;
    private bool speechActive;
    private AudioFormat format;
    private int disposed;
    private bool receiving = true;
    private readonly TaskCompletionSource finalized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async Task StartAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        format = encoder.SupportedFormats.First(f => f.Codec == AudioCodecsEnum.OPUS);
        peer.addTrack(new MediaStreamTrack(format));
        peer.OnAudioFormatsNegotiated += formats =>
        {
            var opus = formats.FirstOrDefault(f => f.Codec == AudioCodecsEnum.OPUS);
            if (opus.IsEmpty()) { Fail(new InvalidOperationException("OpenAI did not negotiate Opus audio.")); return; }
            lock (mediaGate) format = opus;
        };
        peer.OnRtpPacketReceived += (_, type, packet) =>
        {
            if (type != SDPMediaTypesEnum.audio || Lifetime.IsCancellationRequested) return;
            try
            {
                lock (mediaGate)
                {
                    if (!receiving || audio is null) return;
                    var samples = decoder.DecodeAudio(packet.Payload, format);
                    var pcm = new byte[samples.Length * 2]; Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
                    audio.Play(pcm);
                }
            }
            catch (Exception error) { Fail(RealtimeFailureException.Safe(error, "openai-decode", "OpenAI audio decoding failed.")); }
        };
        peer.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed && !Lifetime.IsCancellationRequested && Volatile.Read(ref disposed) == 0 && !finalized.Task.IsCompleted)
            { opened.TrySetException(new InvalidOperationException("OpenAI WebRTC disconnected.")); Fail(new InvalidOperationException("OpenAI WebRTC disconnected.")); }
        };
        data = await peer.createDataChannel("oai-events");
        data.onopen += () => opened.TrySetResult();
        data.onerror += _ => { if (Volatile.Read(ref disposed) != 0 || finalized.Task.IsCompleted) return; opened.TrySetException(new InvalidOperationException("OpenAI data channel failed.")); Fail(new InvalidOperationException("OpenAI data channel failed.")); };
        data.onclose += () => { if (!Lifetime.IsCancellationRequested && Volatile.Read(ref disposed) == 0 && !finalized.Task.IsCompleted) Fail(new InvalidOperationException("OpenAI data channel closed.")); };
        data.onmessage += (_, _, bytes) =>
        {
            try
            {
                var message = Parse(bytes);
                var type = message["type"]?.ToString();
                if (type == "input_audio_buffer.speech_started") speechActive = true;
                if (type == "input_audio_buffer.committed") speechActive = false;
                if (type == "session.closed") finalized.TrySetResult();
                if (type == "input_audio_buffer.speech_started") { lock (mediaGate) { receiving = false; audio?.Interrupt(); } }
                if (type == "response.created") { lock (mediaGate) receiving = true; }
                Emit(message);
            }
            catch { Fail(new InvalidOperationException("Invalid OpenAI realtime event.")); }
        };
        var offer = peer.createOffer();
        await peer.setLocalDescription(offer);
        var answer = sessionFactory is not null
            ? await sessionFactory(peer.localDescription.sdp.ToString(), token).ConfigureAwait(false)
            : await ExchangeLegacyAsync(peer.localDescription.sdp.ToString(), token).ConfigureAwait(false);
        if (answer.Length > 256 * 1024 || peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer }) != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException("OpenAI returned an invalid SDP answer.");
        await opened.Task.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (transcriptionInput is not null)
        {
            if (transcriptionInput.SampleRate != 48000) throw new ArgumentException("OpenAI WebRTC requires 48-kHz PCM input.");
            transcriptionCapture = transcriptionInput.Subscribe(SendPcm, Fail);
            return;
        }
        audio = new RealtimeAudio(48000, 48000, pcm =>
        {
            lock (mediaGate)
            {
                if (Lifetime.IsCancellationRequested) return;
                var samples = new short[pcm.Length / 2]; Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
                peer.SendAudio((uint)samples.Length, encoder.EncodeAudio(samples, format));
            }
        }, Fail, Emit);
        audio.Start();
    }
    private void SendPcm(byte[] pcm)
    {
        lock (mediaGate)
        {
            if (Lifetime.IsCancellationRequested) return;
            var samples = new short[pcm.Length / 2]; Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
            peer.SendAudio((uint)samples.Length, encoder.EncodeAudio(samples, format));
        }
    }
    public async Task FinishTranscriptionAsync()
    {
        Interlocked.Exchange(ref transcriptionCapture, null)?.Dispose();
        if (Lifetime.IsCancellationRequested || data?.readyState != RTCDataChannelState.open) return;
        // VAD may not have seen the final silence when the user presses Stop mid-utterance.
        if (speechActive) await SendAsync(new() { ["type"] = "input_audio_buffer.commit" }, Lifetime.Token).ConfigureAwait(false);
        await Task.Delay(1500, Lifetime.Token).ConfigureAwait(false);
    }
    private async Task<string> ExchangeLegacyAsync(string sdp, CancellationToken token)
    {
        var credential = await tokenFactory(token).ConfigureAwait(false);
        // A dedicated, redirect-disabled client prevents backend authentication leakage.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/realtime/calls");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Content = new StringContent(sdp, Encoding.UTF8, "application/sdp");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenAI WebRTC negotiation failed (HTTP {(int)response.StatusCode}).");
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }
    public override async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        await sendGate.WaitAsync(ct);
        try
        {
            if (Lifetime.IsCancellationRequested || data?.readyState != RTCDataChannelState.open) throw new InvalidOperationException("OpenAI is not connected.");
            var json = message.ToJsonString();
            if (Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes || data.bufferedAmount > MaximumMessageBytes) throw new InvalidOperationException("OpenAI data channel buffer limit exceeded.");
            data.send(json);
        }
        finally { sendGate.Release(); }
    }
    public override async Task SetMutedAsync(bool muted)
    {
        if (audio is not null) audio.Muted = muted;
        if (live && data?.readyState == RTCDataChannelState.open) await SendAsync(new() { ["type"] = muted ? "session.input_audio.mute" : "session.input_audio.unmute", ["event_id"] = Guid.NewGuid().ToString("N") }, Lifetime.Token);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (live && data?.readyState == RTCDataChannelState.open && !finalized.Task.IsCompleted)
        {
            try { await SendAsync(new() { ["type"] = "session.close", ["event_id"] = Guid.NewGuid().ToString("N") }, Lifetime.Token); await finalized.Task.WaitAsync(TimeSpan.FromMilliseconds(1500)); }
            catch { /* Finalization is unconfirmed, but device teardown must still complete. */ }
        }
        Lifetime.Cancel();
        Interlocked.Exchange(ref transcriptionCapture, null)?.Dispose();
        var devices = audio; audio = null;
        devices?.Dispose();
        lock (mediaGate) { peer.close(); peer.Dispose(); encoder.Dispose(); decoder.Dispose(); }
        Complete();
    }
}
