using System;

namespace Shadowsocks.Model
{
    /*
     * Format:
     *  <modifiers-combination>+<key>
     *
     */

    [Serializable]
    public class HotkeyConfig
    {
        public string SwitchSystemProxy { get; set; }
        public string SwitchSystemProxyMode { get; set; }
        public string SwitchAllowLan { get; set; }
        public string ShowLogs { get; set; }
        public string ServerMoveUp { get; set; }
        public string ServerMoveDown { get; set; }
        public bool RegHotkeysAtStartup { get; set; }

        public HotkeyConfig()
        {
            SwitchSystemProxy = "";
            SwitchSystemProxyMode = "";
            SwitchAllowLan = "";
            ShowLogs = "";
            ServerMoveUp = "";
            ServerMoveDown = "";
            RegHotkeysAtStartup = false;
        }
    }
}
