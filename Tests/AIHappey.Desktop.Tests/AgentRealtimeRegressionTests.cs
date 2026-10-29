using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using AIHappey.Desktop.Core;

internal static class AgentRealtimeRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string directory)
    {
        var agent = DesktopAgent.Parse("""
            {"name":"Voice","description":"Voice agent","instructions":"Help","models":[{"id":"openai/llm","type":"language"}],
            "transcriptionModel":{"id":"unsupported/stt","type":"transcription","future":42,"providerMetadata":{"unsupported":{"custom":true}},"providerHeaders":{"X-Test":"keep"}},
            "speechModel":{"id":"openai/tts","type":"speech","providerMetadata":{"openai":{"voice":"coral"}}}}
            """);
        var original = agent.Definition.ToJsonString();
        check(!agent.Definition.ContainsKey("transcriptionModel") && !agent.Definition.ContainsKey("speechModel")
            && DesktopAgent.Text(agent.SpeechModel?["id"]) == "openai/tts", "legacy audio migrates to canonical fields and nested speech model");
        var mixed = new DesktopAgent(new() { ["transcription"] = null, ["speech"] = null,
            ["transcriptionModel"] = new JsonObject { ["id"] = "old/stt" }, ["speechModel"] = new JsonObject { ["id"] = "old/tts" } });
        check(mixed.Transcription is null && mixed.Speech is null && !mixed.Definition.ContainsKey("speechModel"), "explicit null new audio fields win over legacy fields");
        check(new DesktopAgent(agent.Definition).Definition.ToJsonString() == original, "audio migration is idempotent");
        var draft = agent.Clone(); draft.SelectAudioModel("transcription", "unsupported/stt");
        check(draft.Definition.ToJsonString() == original, "reselecting unsupported audio preserves every field");
        draft.SelectAudioModel("transcription", "unsupported/other");
        check(draft.Transcription!["providerHeaders"]!["X-Test"]!.ToString() == "keep", "same-provider audio changes preserve metadata and headers");
        draft.SelectAudioModel("transcription", "openai/stt");
        check(draft.Transcription!["providerMetadata"] is null && draft.Transcription["providerHeaders"] is null,
            "cross-provider audio changes never leak another provider's configuration");
        draft.SelectAudioModel("speech", "");
        check(draft.SpeechModel is null && agent.Definition.ToJsonString() == original, "clearing optional speech edits only the isolated draft");
        var store = new DesktopAgentStore(directory); await store.SaveAsync("partition", agent, null, CancellationToken.None);
        var restored = (await store.ListAsync("partition", [], CancellationToken.None)).Single();
        check(restored.Definition.ToJsonString() == original, "unsupported transcription and optional speech survive native storage round trips");
        var configured = agent.Clone();
        configured.Speech!["voice"] = "custom"; configured.Speech["speed"] = 1.2; configured.Speech["instructions"] = "Warmly"; configured.Speech["language"] = "nl";
        configured.Speech["outputFormat"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 16000 };
        configured.SpeechModel!["providerMetadata"] = new JsonObject { ["openai"] = new JsonObject
            { ["voice"] = "old", ["speed"] = .8, ["response_format"] = "mp3", ["opaque"] = new JsonArray(1, 2) } };
        var configuredJson = configured.Definition.ToJsonString();
        var request = AgentSpeechConfiguration.Request(configured.Speech, " Hello ");
        check(request.Model == "openai/tts" && request.Voice == "custom" && request.Instructions == "Warmly" && request.Language == "nl"
            && Math.Abs(request.Speed!.Value - 1.2) < .0001 && request.OutputFormat == "pcm" && request.Text == " Hello ", "generic agent speech fields map to HTTP without changing text");
        check(request.ProviderOptions!["openai"].GetProperty("voice").GetString() == "custom"
            && request.ProviderOptions["openai"].GetProperty("response_format").GetString() == "pcm"
            && request.ProviderOptions["openai"].GetProperty("opaque").GetArrayLength() == 2
            && configured.Definition.ToJsonString() == configuredJson, "generic settings override request-local metadata and retain opaque stored options");
        check(DesktopAgent.Parse(configuredJson).Definition.ToJsonString() == configuredJson, "new speech settings survive portable agent round trips");
        configured.SelectAudioModel("speech", "smallestai/lightning_v3.1");
        check(DesktopAgent.Text(configured.Speech!["voice"]) == "custom" && configured.SpeechModel!["providerMetadata"] is null,
            "cross-provider speech model changes retain generic options but not incompatible metadata");
        var socketOptions = AgentSpeechConfiguration.SocketOptions(configured.Speech);
        var socket = StreamingSpeechProtocol.Create("smallestai/lightning_v3.1", socketOptions).Append("Hello");
        check(DesktopAgent.Text(socket["voice_id"]) == "custom" && socket["sample_rate"]!.GetValue<int>() == 16000
            && DesktopAgent.Text(socket["language"]) == "nl" && DesktopAgent.Text(socket["output_format"]) == "pcm", "generic voice, rate and language reach direct WebSocket speech with raw PCM");
        configured.SelectAudioModel("speech", "");
        check(configured.SpeechModel is null && DesktopAgent.Text(configured.Speech!["voice"]) == "custom", "clearing a speech model retains model-independent speech preferences");
        var defaults = AgentSpeechConfiguration.Request(new() { ["model"] = new JsonObject { ["id"] = "openai/tts" } }, "Hello");
        check(defaults.Speed is null && defaults.Voice is null && defaults.OutputFormat is null && defaults.ProviderOptions is null, "unset agent settings do not inherit standalone defaults");
        foreach (var speed in new[] { 0d, -1d, double.PositiveInfinity })
            check(!AgentSpeechConfiguration.IsValid(new() { ["speed"] = speed }), "invalid agent speech speed is rejected");
        check(!AgentSpeechConfiguration.IsValid(new() { ["outputFormat"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 1.5 } }), "fractional sample rates are rejected");
        var openaiConfig = OpenAiRealtimeSpeech.Session("openai/gpt-realtime-2.1-mini", new() { ["voice"] = "cedar", ["instructions"] = "ignored metadata" }, "Speak warmly");
        check(DesktopAgent.Text(openaiConfig["instructions"]).Contains("Speak warmly")
            && DesktopAgent.Text(openaiConfig["instructions"]).EndsWith(OpenAiRealtimeSpeech.Instructions)
            && !DesktopAgent.Text(openaiConfig["instructions"]).Contains("ignored metadata"), "generic OpenAI speech guidance retains verbatim-reading safeguards");
        var starts = 0; var turns = new List<string>(); var previews = new List<string>();
        using (var openai = new AgentRealtimeTurns("openai", () => starts++, previews.Add, turns.Add))
        {
            void Event(string json) => openai.Apply(JsonNode.Parse(json)!.AsObject());
            Event("""{"type":"input_audio_buffer.speech_started","item_id":"one"}""");
            Event("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"one","delta":" "}""");
            check(starts == 0 && turns.Count == 0, "VAD and whitespace do not interrupt or send a user turn");
            Event("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"one","delta":"Hello"}""");
            check(starts == 1 && turns.Count == 0, "confirmed transcription interrupts once while previews remain transient");
            Event("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"one","transcript":"Hello world"}""");
            Event("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"one","transcript":"Hello world"}""");
            check(turns.SequenceEqual(["Hello world"]), "OpenAI final replaces partial and duplicate finals send exactly once");
            Event("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"muted","delta":"Discard"}""");
            openai.Reset();
            Event("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"muted","transcript":"Discard"}""");
            check(turns.Count == 1, "mute/reset rejects late finals from discarded drafts");
        }
        turns.Clear(); starts = 0;
        using (var google = new AgentRealtimeTurns("google", () => starts++, previews.Add, turns.Add))
        {
            google.Apply(JsonNode.Parse("""{"serverContent":{"inputTranscription":{"text":"First"}}}""")!.AsObject());
            google.Apply(JsonNode.Parse("""{"serverContent":{"inputTranscription":{"text":"second"}}}""")!.AsObject());
            check(turns.Count == 0, "Google finalized fragments are not separate user turns");
            google.Apply(JsonNode.Parse("""{"serverContent":{"turnComplete":true}}""")!.AsObject());
            check(starts == 1 && turns.SequenceEqual(["First second"]), "Google endpoint commits one combined complete utterance");
            google.Apply(JsonNode.Parse("""{"serverContent":{"turnComplete":true}}""")!.AsObject());
            check(turns.Count == 1, "repeated Google turn boundary does not duplicate messages");
        }
        turns.Clear(); starts = 0;
        using (var gladia = new AgentRealtimeTurns("gladia", () => starts++, previews.Add, turns.Add))
        {
            var final = JsonNode.Parse("""{"type":"transcript","data":{"is_final":true,"utterance":{"start":1,"text":"Gladia final"}}}""")!.AsObject();
            gladia.Apply(final); gladia.Apply(final);
            check(starts == 1 && turns.SequenceEqual(["Gladia final"]), "Gladia utterance identity prevents repeated execution");
            gladia.Dispose(); gladia.Apply(JsonNode.Parse("""{"type":"transcript","data":{"is_final":true,"utterance":{"start":2,"text":"Late"}}}""")!.AsObject());
            check(turns.Count == 1, "disposed voice session rejects stale transcription callbacks");
        }
        await MistralTurnsAsync(check);
        var parts = new[] { """{"type":"text","text":"I will run a tool"}""", """{"type":"tool-example","input":{"secret":1},"output":{"secret":2}}""",
            """{"type":"step-start"}""", """{"type":"reasoning","text":"Private reasoning"}""", """{"type":"text","text":"Final answer"}""" }
            .Select(json => PortableConversations.Part(JsonNode.Parse(json)!.AsObject())).ToArray();
        check(AgentRealtimeResponse.FinalText(parts) == "Final answer", "speech includes only final answer, never tool-step narration, inputs, outputs or reasoning");
        check(AgentRealtimeResponse.FinalText(parts.Take(2)) == "", "tool-only completion produces no speech");
    }
    private static async Task MistralTurnsAsync(Action<bool, string> check)
    {
        var starts = 0;
        var previews = new ConcurrentQueue<string>(); var turns = new ConcurrentQueue<string>();
        using var handler = new AgentRealtimeTurns("mistral", () => Interlocked.Increment(ref starts), previews.Enqueue, turns.Enqueue);
        void Delta(string text) => handler.Apply(new() { ["type"] = "transcription.text.delta", ["text"] = text });
        void Final(string text) => handler.Apply(new() { ["type"] = "transcription.done", ["text"] = text });
        async Task AwaitTurns(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (turns.Count < count) await Task.Delay(10, timeout.Token);
        }
        handler.Activity(true); Delta(" \n");
        check(starts == 0 && turns.IsEmpty, "Mistral activity and whitespace alone never interrupt an LLM run");
        foreach (var text in new[] { "Hel", "lo", ",", " world", "!" }) Delta(text);
        check(starts == 1 && previews.Last() == "Hello, world!", "Mistral token previews preserve spacing without inventing word separators");
        await Task.Delay(1000);
        check(turns.IsEmpty, "Mistral does not submit a turn while the microphone still detects speech");
        handler.Activity(false); await AwaitTurns(1);
        check(turns.SequenceEqual(["Hello, world!"]), "Mistral submits one combined utterance after silence");
        foreach (var _ in new[] { 1, 2 })
        {
            handler.Activity(true); Delta(" Yes."); handler.Activity(false); await AwaitTurns(turns.Count + 1);
        }
        check(turns.SequenceEqual(["Hello, world!", "Yes.", "Yes."]), "Mistral accepts identical successive utterances as distinct turns");
        Delta(" Discard."); handler.SetMuted(true); Delta(" Muted tail.");
        handler.SetMuted(false); handler.Activity(true); Delta(" Keep.");
        Final(" \nHello, world! Yes. Yes. Discard. Muted tail. Keep corrected.");
        Final(" \nHello, world! Yes. Yes. Discard. Muted tail. Keep corrected."); Delta("late");
        await Task.Delay(1000);
        check(turns.SequenceEqual(["Hello, world!", "Yes.", "Yes.", "Keep corrected."]), "Mistral cumulative final emits only the unsubmitted tail and excludes muted/reset content and duplicate terminal events");

        var resetTurns = new ConcurrentQueue<string>();
        using var reset = new AgentRealtimeTurns("mistral", () => { }, _ => { }, resetTurns.Enqueue);
        reset.Apply(new() { ["type"] = "transcription.text.delta", ["text"] = "Discard." }); reset.Reset();
        reset.Apply(new() { ["type"] = "transcription.done", ["text"] = "Discard." });
        check(resetTurns.IsEmpty, "Mistral reset prevents a cumulative final from reviving a discarded draft");
        using var disposed = new AgentRealtimeTurns("mistral", () => { }, _ => { }, resetTurns.Enqueue);
        disposed.Apply(new() { ["type"] = "transcription.text.delta", ["text"] = "Never send" }); disposed.Dispose();
        await Task.Delay(1000);
        check(resetTurns.IsEmpty, "Mistral disposal cancels pending silence timers");
        using var corrected = new AgentRealtimeTurns("mistral", () => { }, _ => { }, resetTurns.Enqueue);
        corrected.Apply(new() { ["type"] = "transcription.text.delta", ["text"] = "draft" });
        corrected.Apply(new() { ["type"] = "transcription.done", ["text"] = "Corrected." });
        check(resetTurns.SequenceEqual(["Corrected."]), "Mistral final is authoritative before any text has been submitted");
    }
}
