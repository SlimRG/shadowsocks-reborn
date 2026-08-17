using System;

namespace Shadowsocks.Core.Storage
{
    public interface ISettingsStore
    {
        bool TryGetString(string name, out string value);
        void SetString(string name, string value);
        bool TryGetInt32(string name, out int value);
        void SetInt32(string name, int value);
        void DeleteValue(string name);
    }
}
