using System.Reflection;
using System.Runtime.InteropServices;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckComposerKeyboardAsync(ElementTheme theme)
    {
        var images = new ImagesPage { RequestedTheme = theme };
        await CheckMediaKeyboardAsync(images, images.SetModelAvailable, value => images.SetBusy(value, true),
            callback => images.SendRequested = callback, "Images / " + theme);
        var videos = new VideosPage { RequestedTheme = theme };
        await CheckMediaKeyboardAsync(videos, videos.SetModelAvailable, videos.SetSubmitting,
            callback => videos.SendRequested = callback, "Videos / " + theme);
        var speech = new SpeechPage { RequestedTheme = theme };
        await CheckMediaKeyboardAsync(speech, speech.SetModelAvailable, value => speech.SetBusy(value, true),
            callback => speech.SendRequested = callback, "Speech / " + theme);
        foreach (var local in new[] { true, false }) await CheckChatKeyboardAsync(local, theme);
    }

    private async Task CheckMediaKeyboardAsync(UserControl page, Action<bool> setModel, Action<bool> setBusy,
        Action<Func<Task>?> setSend, string context)
    {
        var submissions = new List<string>();
        var prompt = (TextBox)page.FindName("Prompt");
        setSend(async () => { submissions.Add(prompt.Text); await Task.Yield(); });
        try
        {
            await ShowKeyboardFixtureAsync(page);
            setModel(true);
            prompt.Text = "First line";
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            Check(submissions.SequenceEqual(["First line"]) && prompt.Text == "First line",
                context + $": Enter sends exactly once without inserting a newline, including across an async callback (submissions={submissions.Count}, draft={NormalizeComposerLines(prompt.Text).Replace("\n", "\\n")})");

            await CheckShiftEnterAsync(prompt, () => submissions.Count, context);
            var multiline = prompt.Text;
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            Check(submissions.Count == 2 && submissions[1] == multiline && prompt.Text == multiline,
                context + ": Enter submits an existing multiline prompt without altering it");

            setModel(false);
            prompt.Text = "Keep this draft";
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            Check(submissions.Count == 2 && prompt.Text == "Keep this draft", context + ": no-model Enter preserves the draft");
            setModel(true);
            setBusy(true);
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            Check(submissions.Count == 2 && prompt.Text == "Keep this draft", context + ": busy Enter cannot submit or alter the draft");
            setBusy(false);
            prompt.Text = "  ";
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            Check(submissions.Count == 2 && prompt.Text == "  ", context + ": whitespace-only Enter cannot submit or insert a newline");
            prompt.Text = "No callback";
            setSend(null);
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            Check(submissions.Count == 2 && prompt.Text == "No callback", context + ": absent send callback does not insert a newline");
        }
        finally { window!.Content = null; }
    }

    private async Task CheckChatKeyboardAsync(bool local, ElementTheme theme)
    {
        var runtime = new UiRuntime();
        var session = new DesktopSession(new UiHost(local), runtime, local ? new DesktopSettings() : new DesktopSettings
        {
            Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://ui-test.invalid/ai/" },
            Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://ui-test.invalid/agents/" }
        });
        var shell = new ChatShell(session) { RequestedTheme = theme };
        var context = $"Chat keyboard / {(local ? "HeaderAuth" : "AzureAuth")} / {theme}";
        try
        {
            // Supply discovery locally, as in the provider UI fixtures. The real send path
            // commits the turn, then UiRuntime rejects gateway access without any network I/O.
            typeof(ChatShell).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, true);
            var root = (Grid)shell.Content;
            root.Children[0].Visibility = Visibility.Visible;
            root.Children[1].Visibility = Visibility.Collapsed;
            await ShowKeyboardFixtureAsync(shell);
            var prompt = Field<TextBox>(shell, "input");
            var conversation = Field<Conversation>(shell, "current");
            await CheckShiftEnterAsync(prompt, () => conversation.Messages.Count, context);

            prompt.Text = "No selected model";
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            Check(conversation.Messages.Count == 0 && prompt.Text == "No selected model"
                && Field<InfoBar>(shell, "notice").IsOpen, context + ": Enter invokes existing target validation without changing the draft");
            Call(shell, "SetBusy", true, false);
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            Check(conversation.Messages.Count == 0 && prompt.Text == "No selected model", context + ": busy Enter preserves the editable draft");
            Call(shell, "SetBusy", false, false);
            prompt.Text = "  ";
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            Check(conversation.Messages.Count == 0 && prompt.Text == "  ", context + ": whitespace-only Enter does not submit or insert a newline");

            var models = new ChatTarget[] { new("keyboard/model", "Keyboard model") { ModelType = "language" } };
            typeof(ChatShell).GetField("targets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, models);
            typeof(ChatShell).GetField("aiModelTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, models);
            Field<ChatTargetPicker>(shell, "target").SetSelection([models[0].Id], false);
            prompt.Text = "Send this turn";
            await PressComposerKeyAsync(prompt, VirtualKey.Enter);
            await HistoryIdleAsync(shell);
            Check(conversation.Messages.Count(message => message.Message.Role == Role.user) == 1
                && conversation.Messages.First().Message.Parts.OfType<TextUIPart>().Single().Text == "Send this turn"
                && prompt.Text == "" && runtime.Resolutions == 1,
                context + ": Enter commits exactly one unchanged user turn through the existing send path and clears the draft");
        }
        finally
        {
            await shell.ShutdownAsync();
            window!.Content = null;
            if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true);
        }
    }

    private async Task CheckShiftEnterAsync(TextBox prompt, Func<int> submissionCount, string context)
    {
        var before = submissionCount();
        prompt.Text = "Firstlast";
        prompt.Select(5, 0);
        await PressComposerKeyAsync(prompt, VirtualKey.Enter, shift: true);
        Check(NormalizeComposerLines(prompt.Text) == "First\nlast" && submissionCount() == before,
            context + ": native Shift+Enter inserts a newline at the caret without sending");
        prompt.Text = "First replace last";
        prompt.Select(6, 8);
        await PressComposerKeyAsync(prompt, VirtualKey.Enter, shift: true);
        Check(NormalizeComposerLines(prompt.Text) == "First \nlast" && submissionCount() == before,
            context + ": native Shift+Enter replaces the selection without sending");
    }

    private static string NormalizeComposerLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private async Task ShowKeyboardFixtureAsync(FrameworkElement element)
    {
        window!.Content = element;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        window.Activate();
        await Task.Delay(150);
        element.UpdateLayout();
    }

    private async Task PressComposerKeyAsync(TextBox prompt, VirtualKey key, bool shift = false)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window!);
        // Foreground activation can complete asynchronously after a runner starts the process.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            window!.Activate();
            ComposerSetForegroundWindow(handle);
            await Task.Delay(100);
            if (ComposerGetForegroundWindow() == handle) break;
        }
        Check(prompt.Focus(FocusState.Keyboard), "Keyboard fixture: native prompt accepts focus");
        await Task.Delay(60);
        // Never inject keys into another application's window or while a physical modifier is held.
        var foreground = ComposerGetForegroundWindow();
        var focused = FocusManager.GetFocusedElement(prompt.XamlRoot);
        Check(foreground == handle && focused == prompt,
            $"Keyboard fixture: test window and prompt own foreground keyboard focus (window={handle}, foreground={foreground}, focused={focused?.GetType().Name})");
        Check(new[] { VirtualKey.Shift, VirtualKey.Control, VirtualKey.Menu, VirtualKey.LeftWindows, VirtualKey.RightWindows }
            .All(modifier => (ComposerGetAsyncKeyState((int)modifier) & 0x8000) == 0), "Keyboard fixture: no external modifiers are held");
        var inputs = new List<ComposerNativeInput>();
        if (shift) inputs.Add(ComposerKey(VirtualKey.LeftShift));
        inputs.Add(ComposerKey(key)); inputs.Add(ComposerKey(key, up: true));
        if (shift) inputs.Add(ComposerKey(VirtualKey.LeftShift, up: true));
        if (ComposerGetForegroundWindow() != handle) throw new InvalidOperationException("Keyboard fixture lost foreground focus before injection.");
        var sent = ComposerSendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<ComposerNativeInput>());
        if (sent != inputs.Count)
        {
            ComposerSendInput(2, [ComposerKey(key, up: true), ComposerKey(VirtualKey.LeftShift, up: true)], Marshal.SizeOf<ComposerNativeInput>());
            throw new InvalidOperationException("Native keyboard injection failed: " + Marshal.GetLastWin32Error());
        }
        await Task.Delay(150);
        if (ComposerGetForegroundWindow() != handle) throw new InvalidOperationException("Keyboard fixture lost foreground focus during injection; keyboard behavior was not evaluated.");
    }

    private static ComposerNativeInput ComposerKey(VirtualKey key, bool up = false) => new()
    {
        Type = 1, Data = new ComposerInputData { Keyboard = new ComposerKeyInput { VirtualKey = (ushort)key, Flags = up ? 2u : 0u } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ComposerNativeInput { public uint Type; public ComposerInputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct ComposerInputData
    {
        [FieldOffset(0)] public ComposerKeyInput Keyboard;
        [FieldOffset(0)] public ComposerMouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ComposerKeyInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }
    // INPUT's native union includes MOUSEINPUT, even when injecting only keyboard input.
    [StructLayout(LayoutKind.Sequential)]
    private struct ComposerMouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint ComposerSendInput(uint count, [In] ComposerNativeInput[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr ComposerGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ComposerSetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short ComposerGetAsyncKeyState(int key);
}
