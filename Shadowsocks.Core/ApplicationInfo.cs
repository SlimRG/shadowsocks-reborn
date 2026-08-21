namespace Shadowsocks.Core
{
    /// <summary>
    /// Product metadata shared by Core, Windows infrastructure and UI.
    /// Keep release versioning in one platform-neutral location.
    /// </summary>
    public static class ApplicationInfo
    {
        public const string Version = "5.2.22.0";
        public const string RepositoryUrl = "https://github.com/SlimRG/shadowsocks-reborn";
        public const string IssuesUrl = RepositoryUrl + "/issues";
        public const string ReleasesApiUrl = "https://api.github.com/repos/SlimRG/shadowsocks-reborn/releases";
    }
}
