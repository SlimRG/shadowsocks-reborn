#nullable enable

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Shadowsocks.Core;

namespace Shadowsocks.Windows.Shell;

/// <summary>
/// Synchronous Windows user-interaction boundary for controller code.
/// Uses Win32 primitives so the controller can preserve its original synchronous
/// confirm/error semantics without reintroducing WinForms/WPF or blocking WinUI
/// ContentDialog asynchronous operations.
/// </summary>
public sealed partial class NativeUserInteractionService : IUserInteractionService
{
    private const uint MbOk = 0x00000000;
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconQuestion = 0x00000020;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbSetForeground = 0x00010000;
    private const int IdYes = 6;

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    public nint OwnerWindowHandle { get; set; }

    public bool Confirm(string message, string? title)
        => ShowMessage(message, title, MbYesNo | MbIconQuestion) == IdYes;

    public void ShowInfo(string message, string? title)
        => _ = ShowMessage(message, title, MbOk | MbIconInformation);

    public void ShowWarning(string message, string? title)
        => _ = ShowMessage(message, title, MbOk | MbIconWarning);

    public void ShowError(string message, string? title)
        => _ = ShowMessage(message, title, MbOk | MbIconError);

    public void SetClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
        nint memoryHandle = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)bytes.Length);
        if (memoryHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        bool clipboardOpened = false;
        bool ownershipTransferred = false;
        try
        {
            nint memory = GlobalLock(memoryHandle);
            if (memory == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                Marshal.Copy(bytes, 0, memory, bytes.Length);
            }
            finally
            {
                _ = GlobalUnlock(memoryHandle);
            }

            for (int attempt = 0; attempt < 10 && !clipboardOpened; attempt++)
            {
                clipboardOpened = OpenClipboard(OwnerWindowHandle) != 0;
                if (!clipboardOpened)
                {
                    Thread.Sleep(10);
                }
            }

            if (!clipboardOpened)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (EmptyClipboard() == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (SetClipboardData(CfUnicodeText, memoryHandle) == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            ownershipTransferred = true;
        }
        finally
        {
            if (clipboardOpened)
            {
                _ = CloseClipboard();
            }

            if (!ownershipTransferred)
            {
                _ = GlobalFree(memoryHandle);
            }
        }
    }

    private int ShowMessage(string message, string? title, uint flags)
    {
        string caption = string.IsNullOrWhiteSpace(title) ? "shadowsocks-reborn" : title;
        return MessageBox(OwnerWindowHandle, message ?? string.Empty, caption, flags | MbSetForeground);
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint windowHandle, string text, string caption, uint type);

    [LibraryImport("user32.dll", EntryPoint = "OpenClipboard", SetLastError = true)]
    private static partial int OpenClipboard(nint newOwner);

    [LibraryImport("user32.dll", EntryPoint = "CloseClipboard", SetLastError = true)]
    private static partial int CloseClipboard();

    [LibraryImport("user32.dll", EntryPoint = "EmptyClipboard", SetLastError = true)]
    private static partial int EmptyClipboard();

    [LibraryImport("user32.dll", EntryPoint = "SetClipboardData", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memoryHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalAlloc", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalLock", SetLastError = true)]
    private static partial nint GlobalLock(nint memoryHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalUnlock", SetLastError = true)]
    private static partial int GlobalUnlock(nint memoryHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalFree", SetLastError = true)]
    private static partial nint GlobalFree(nint memoryHandle);
}
