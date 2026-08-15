using System;
using System.Reactive;
using Newtonsoft.Json.Linq;
using ReactiveUI;
using Shadowsocks.Controller;
using Shadowsocks.Localization;

namespace Shadowsocks.ViewModels
{
    public class VersionUpdatePromptViewModel : ReactiveObject
    {
        public VersionUpdatePromptViewModel(JToken releaseObject, UpdateChecker updateChecker, Action close)
        {
            _updateChecker = updateChecker ?? throw new ArgumentNullException(nameof(updateChecker));
            _releaseObject = releaseObject;
            string releaseKind = (bool)_releaseObject["prerelease"]
                ? "⚠ " + LocalizationProvider.GetLocalizedValue<string>("ReleaseKindPreRelease")
                : "ℹ " + LocalizationProvider.GetLocalizedValue<string>("ReleaseKindRelease");
            string tagName = (string)_releaseObject["tag_name"] ?? LocalizationProvider.GetLocalizedValue<string>("ReleaseTagUnavailable");
            string releaseNotes = (string)_releaseObject["body"] ?? LocalizationProvider.GetLocalizedValue<string>("ReleaseNotesUnavailable");

            ReleaseNotes = $"# {releaseKind} {tagName}\r\n{releaseNotes}";

            Update = ReactiveCommand.CreateFromTask(async () => { await _updateChecker.DoUpdate(); close?.Invoke(); });
            SkipVersion = ReactiveCommand.Create(() => { _updateChecker.SkipUpdate(); close?.Invoke(); });
            NotNow = ReactiveCommand.Create(close ?? (() => { }));
        }

        private readonly UpdateChecker _updateChecker;
        private readonly JToken _releaseObject;

        public string ReleaseNotes { get; }

        public ReactiveCommand<Unit, Unit> Update { get; }

        public ReactiveCommand<Unit, Unit> SkipVersion { get; }

        public ReactiveCommand<Unit, Unit> NotNow { get; }
    }
}
