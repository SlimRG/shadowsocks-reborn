using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Shadowsocks.Controller.Hotkeys
{
    public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey, string KeyName)
    {
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private const uint ModNoRepeat = 0x4000;

        public static bool TryParse(string value, out HotkeyGesture gesture)
        {
            gesture = default;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string[] parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false;

            uint modifiers = ModNoRepeat;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToUpperInvariant())
                {
                    case "CTRL":
                    case "CONTROL": modifiers |= ModControl; break;
                    case "ALT": modifiers |= ModAlt; break;
                    case "SHIFT": modifiers |= ModShift; break;
                    case "WIN":
                    case "WINDOWS": modifiers |= ModWin; break;
                    default: return false;
                }
            }

            string keyName = parts[^1];
            if (!TryGetVirtualKey(keyName, out uint virtualKey)) return false;
            gesture = new HotkeyGesture(modifiers, virtualKey, NormalizeKeyName(keyName));
            return true;
        }

        public override string ToString()
        {
            List<string> parts = new();
            if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & ModShift) != 0) parts.Add("Shift");
            if ((Modifiers & ModWin) != 0) parts.Add("Win");
            parts.Add(KeyName);
            return string.Join("+", parts);
        }

        private static string NormalizeKeyName(string keyName)
        {
            if (keyName.Length == 1) return keyName.ToUpperInvariant();
            if (keyName.StartsWith("d", StringComparison.OrdinalIgnoreCase) && keyName.Length == 2 && char.IsDigit(keyName[1]))
                return "D" + keyName[1];
            if (keyName.StartsWith("numpad", StringComparison.OrdinalIgnoreCase))
                return "NumPad" + keyName[6..];
            if (keyName.StartsWith("f", StringComparison.OrdinalIgnoreCase) && int.TryParse(keyName[1..], out _))
                return "F" + keyName[1..];
            return keyName;
        }

        private static bool TryGetVirtualKey(string keyName, out uint key)
        {
            key = 0;
            if (keyName.Length == 1)
            {
                char c = char.ToUpperInvariant(keyName[0]);
                if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { key = c; return true; }
            }

            if (keyName.Length == 2 && (keyName[0] == 'D' || keyName[0] == 'd') && char.IsDigit(keyName[1]))
            { key = keyName[1]; return true; }

            if (keyName.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && keyName.Length == 7 && char.IsDigit(keyName[6]))
            { key = 0x60u + (uint)(keyName[6] - '0'); return true; }

            if (keyName.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(keyName[1..], out int functionKey) && functionKey is >= 1 and <= 24)
            { key = 0x70u + (uint)(functionKey - 1); return true; }

            return NamedKeys.TryGetValue(keyName, out key);
        }

        private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Back"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D,
            ["Escape"] = 0x1B, ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
            ["End"] = 0x23, ["Home"] = 0x24, ["Left"] = 0x25, ["Up"] = 0x26,
            ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
            ["Oem1"] = 0xBA, ["OemPlus"] = 0xBB, ["OemComma"] = 0xBC, ["OemMinus"] = 0xBD,
            ["OemPeriod"] = 0xBE, ["Oem2"] = 0xBF, ["Oem3"] = 0xC0, ["Oem4"] = 0xDB,
            ["Oem5"] = 0xDC, ["Oem6"] = 0xDD, ["Oem7"] = 0xDE,
        };
    }

    public sealed partial class GlobalHotkeyService : IDisposable
    {
        private const uint WmHotkey = 0x0312;
        private const uint WmAppRegister = 0x8001;
        private const uint WmAppUnregister = 0x8002;
        private const uint WmAppStop = 0x8003;
        private const uint PmNoRemove = 0x0000;

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ConcurrentDictionary<int, Command> _commands = new();
        private readonly Dictionary<string, Registration> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Registration> _byId = new();
        private int _nextCommandId;
        private int _nextHotkeyId = 100;
        private uint _threadId;
        private bool _disposed;

        public GlobalHotkeyService()
        {
            _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Shadowsocks.GlobalHotkeys" };
            _thread.Start();
            _ready.Wait();
        }

        public bool Register(string name, string gestureText, Action callback)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GlobalHotkeyService));
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(callback);
            if (!HotkeyGesture.TryParse(gestureText, out HotkeyGesture gesture)) return false;

            Unregister(name);
            int hotkeyId = Interlocked.Increment(ref _nextHotkeyId);
            var registration = new Registration(name, hotkeyId, gesture, callback);
            var command = new Command(registration);
            return Execute(WmAppRegister, command);
        }

        public void Unregister(string name)
        {
            if (_disposed || string.IsNullOrWhiteSpace(name)) return;
            Execute(WmAppUnregister, new Command(name));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ = PostThreadMessage(_threadId, WmAppStop, UIntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
            _ready.Dispose();
        }

        private bool Execute(uint message, Command command)
        {
            int commandId = Interlocked.Increment(ref _nextCommandId);
            _commands[commandId] = command;
            if (PostThreadMessage(_threadId, message, (UIntPtr)(uint)commandId, IntPtr.Zero) == 0)
            {
                _commands.TryRemove(commandId, out _);
                return false;
            }
            command.Completed.Wait();
            command.Completed.Dispose();
            return command.Result;
        }

        private void MessageLoop()
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
            _ready.Set();

            while (GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.MessageId == WmHotkey)
                {
                    int id = unchecked((int)message.WParam.ToUInt64());
                    if (_byId.TryGetValue(id, out Registration registration))
                    {
                        try { registration.Callback(); } catch { }
                    }
                    continue;
                }

                if (message.MessageId == WmAppStop)
                {
                    foreach (Registration registration in _byId.Values)
                        _ = UnregisterHotKey(IntPtr.Zero, registration.Id);
                    _byId.Clear();
                    _byName.Clear();
                    PostQuitMessage(0);
                    continue;
                }

                int commandId = unchecked((int)message.WParam.ToUInt64());
                if (!_commands.TryRemove(commandId, out Command command)) continue;
                try
                {
                    if (message.MessageId == WmAppRegister)
                    {
                        Registration registration = command.Registration;
                        command.Result = RegisterHotKey(IntPtr.Zero, registration.Id, registration.Gesture.Modifiers, registration.Gesture.VirtualKey) != 0;
                        if (command.Result)
                        {
                            _byName[registration.Name] = registration;
                            _byId[registration.Id] = registration;
                        }
                    }
                    else if (message.MessageId == WmAppUnregister)
                    {
                        command.Result = true;
                        if (_byName.Remove(command.Name, out Registration registration))
                        {
                            _byId.Remove(registration.Id);
                            command.Result = UnregisterHotKey(IntPtr.Zero, registration.Id) != 0;
                        }
                    }
                }
                finally { command.Completed.Set(); }
            }
        }

        private sealed record Registration(string Name, int Id, HotkeyGesture Gesture, Action Callback);

        private sealed class Command
        {
            public Command(Registration registration) => Registration = registration;
            public Command(string name) => Name = name;
            public Registration Registration { get; }
            public string Name { get; }
            public ManualResetEventSlim Completed { get; } = new(false);
            public bool Result { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr HWnd; public uint MessageId; public UIntPtr WParam; public IntPtr LParam;
            public uint Time; public Point Pt; public uint LPrivate;
        }

        [LibraryImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
        private static partial int RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [LibraryImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
        private static partial int UnregisterHotKey(IntPtr hWnd, int id);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        private static partial int GetMessage(out NativeMessage msg, IntPtr hWnd, uint min, uint max);

        [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
        private static partial int PeekMessage(out NativeMessage msg, IntPtr hWnd, uint min, uint max, uint removeMsg);

        [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        private static partial int PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
        private static partial void PostQuitMessage(int exitCode);

        [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static partial uint GetCurrentThreadId();
    }
}
