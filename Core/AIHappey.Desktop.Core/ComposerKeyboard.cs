using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Windows.UI.Core;

namespace AIHappey.Desktop.Core;

internal static class ComposerKeyboard
{
    public static void Attach(TextBox input, Func<bool> canSend, Func<Task> send)
    {
        var composing = false;
        input.TextCompositionStarted += (_, _) => composing = true;
        input.TextCompositionEnded += (_, _) => composing = false;
        input.LostFocus += (_, _) => composing = false;
        // A multiline TextBox consumes Enter before the bubbling KeyDown event.
        // Leave Shift+Enter and IME confirmation to the native text editor.
        input.PreviewKeyDown += async (_, args) =>
        {
            if (args.Handled || args.Key != VirtualKey.Enter || composing
                || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)) return;
            // Consume Enter even when sending is unavailable, and before any asynchronous work.
            args.Handled = true;
            if (canSend()) await send();
        };
    }
}
