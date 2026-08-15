using System.Windows.Forms;
using Shadowsocks.Controller;
using Shadowsocks.Util;

namespace Shadowsocks.UI
{
    internal static class WinFormsLocalization
    {
        public static void TranslateForm(Form form)
        {
            if (form == null) return;
            form.Text = I18N.GetString(form.Text);
            foreach (Control item in ViewUtils.GetChildControls<Control>(form))
            {
                if (item != null) item.Text = I18N.GetString(item.Text);
            }
            TranslateMenu(form.MainMenuStrip);
        }

        public static void TranslateMenu(MenuStrip menu)
        {
            if (menu == null) return;
            foreach (ToolStripMenuItem item in ViewUtils.GetToolStripMenuItems(menu))
            {
                if (item != null) item.Text = I18N.GetString(item.Text);
            }
        }
    }
}
