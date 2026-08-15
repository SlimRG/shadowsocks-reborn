namespace Shadowsocks.Engine
{
    /// <summary>
    /// UI boundary used by the engine for user-facing operations. WinForms/WPF/WinUI
    /// implementations live in the UI project; the engine itself has no desktop UI reference.
    /// </summary>
    public interface IUserInteractionService
    {
        bool Confirm(string message, string title);
        void ShowInfo(string message, string title = null);
        void ShowWarning(string message, string title = null);
        void ShowError(string message, string title = null);
        void SetClipboardText(string text);
    }

    internal sealed class NullUserInteractionService : IUserInteractionService
    {
        public static readonly NullUserInteractionService Instance = new();
        private NullUserInteractionService() { }
        public bool Confirm(string message, string title) => false;
        public void ShowInfo(string message, string title = null) { }
        public void ShowWarning(string message, string title = null) { }
        public void ShowError(string message, string title = null) { }
        public void SetClipboardText(string text) { }
    }
}
