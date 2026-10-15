using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

internal static class WindowsVoiceTyping
{
    public static bool TryStart(TextBox composer)
    {
        if (!composer.IsEnabled || composer.IsReadOnly || !composer.Focus(FocusState.Programmatic)) return false;

        Input[] shortcut = [Key(0x5B), Key(0x48), Key(0x48, keyUp: true), Key(0x5B, keyUp: true)];
        if (SendInput((uint)shortcut.Length, shortcut, Marshal.SizeOf<Input>()) == shortcut.Length) return true;

        // Release the injected keys if Windows accepted only part of the shortcut.
        SendInput(2, [Key(0x48, keyUp: true), Key(0x5B, keyUp: true)], Marshal.SizeOf<Input>());
        return false;
    }

    private static Input Key(ushort key, bool keyUp = false) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = keyUp ? 2u : 0u } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    // INPUT's union must include MOUSEINPUT to have the native size on both x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
