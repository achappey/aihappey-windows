using System.Text.Json.Nodes;
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
        var draft = agent.Clone(); draft.SelectAudioModel("transcription", "unsupported/stt");
        check(draft.Definition.ToJsonString() == original, "reselecting unsupported audio preserves every field");
        draft.SelectAudioModel("transcription", "unsupported/other");
        check(draft.TranscriptionModel!["providerHeaders"]!["X-Test"]!.ToString() == "keep", "same-provider audio changes preserve metadata and headers");
        draft.SelectAudioModel("transcription", "openai/stt");
        check(draft.TranscriptionModel!["providerMetadata"] is null && draft.TranscriptionModel["providerHeaders"] is null,
            "cross-provider audio changes never leak another provider's configuration");
        draft.SelectAudioModel("speech", "");
        check(draft.SpeechModel is null && agent.Definition.ToJsonString() == original, "clearing optional speech edits only the isolated draft");
        var store = new DesktopAgentStore(directory); await store.SaveAsync("partition", agent, null, CancellationToken.None);
        var restored = (await store.ListAsync("partition", [], CancellationToken.None)).Single();
        check(restored.Definition.ToJsonString() == original, "unsupported transcription and optional speech survive native storage round trips");
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
        var parts = new[] { """{"type":"text","text":"I will run a tool"}""", """{"type":"tool-example","input":{"secret":1},"output":{"secret":2}}""",
            """{"type":"step-start"}""", """{"type":"reasoning","text":"Private reasoning"}""", """{"type":"text","text":"Final answer"}""" }
            .Select(json => PortableConversations.Part(JsonNode.Parse(json)!.AsObject())).ToArray();
        check(AgentRealtimeResponse.FinalText(parts) == "Final answer", "speech includes only final answer, never tool-step narration, inputs, outputs or reasoning");
        check(AgentRealtimeResponse.FinalText(parts.Take(2)) == "", "tool-only completion produces no speech");
    }
}
