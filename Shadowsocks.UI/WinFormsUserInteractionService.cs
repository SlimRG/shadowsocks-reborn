using System.Windows.Forms;
using Shadowsocks.Engine;

namespace Shadowsocks.UI
{
    internal sealed class WinFormsUserInteractionService : IUserInteractionService
    {
        public bool Confirm(string message, string title) =>
            MessageBox.Show(message, title ?? "shadowsocks-reborn", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        public void ShowInfo(string message, string title = null) =>
            MessageBox.Show(message, title ?? "shadowsocks-reborn", MessageBoxButtons.OK, MessageBoxIcon.Information);

        public void ShowWarning(string message, string title = null) =>
            MessageBox.Show(message, title ?? "shadowsocks-reborn", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        public void ShowError(string message, string title = null) =>
            MessageBox.Show(message, title ?? "shadowsocks-reborn", MessageBoxButtons.OK, MessageBoxIcon.Error);

        public void SetClipboardText(string text)
        {
            if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
        }
    }
}
