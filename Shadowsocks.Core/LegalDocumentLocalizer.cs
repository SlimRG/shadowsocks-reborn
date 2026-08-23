#nullable enable

using System;
using System.Globalization;

namespace Shadowsocks.Core;

public static class LegalDocumentLocalizer
{
    public static string CreateProductLicense(string originalLicense, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalLicense);
        ArgumentNullException.ThrowIfNull(culture);

        LegalIntro intro = GetIntro(culture);
        string originalTerms = SliceFrom(originalLicense, "GNU GENERAL PUBLIC LICENSE");
        return string.Join(Environment.NewLine,
            intro.ProductTitle,
            string.Empty,
            intro.ProductSummary,
            string.Empty,
            "SPDX-License-Identifier: GPL-3.0-or-later",
            "Project source: https://github.com/SlimRG/shadowsocks-reborn",
            string.Empty,
            intro.OriginalTermsNotice,
            string.Empty,
            originalTerms);
    }

    public static string CreateThirdPartyNotices(string originalNotices, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalNotices);
        ArgumentNullException.ThrowIfNull(culture);

        LegalIntro intro = GetIntro(culture);
        string details = SliceFrom(originalNotices, "## Runtime components included in the application");
        return string.Join(Environment.NewLine,
            intro.NoticesTitle,
            string.Empty,
            intro.NoticesSummary,
            string.Empty,
            intro.ComponentLicenseNotice,
            string.Empty,
            intro.OriginalNoticesHeading,
            string.Empty,
            details);
    }

    private static string SliceFrom(string text, string marker)
    {
        int index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return text.TrimStart('\uFEFF', '\r', '\n', ' ');
        }

        return text[index..].TrimStart();
    }

    private static LegalIntro GetIntro(CultureInfo culture)
    {
        string name = culture.Name;
        string language = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        return language switch
        {
            "ru" => new(
                "Лицензия Shadowsocks Reborn",
                "Shadowsocks Reborn распространяется по лицензии GNU General Public License версии 3 или любой более поздней версии (GPL-3.0-or-later). Вы можете использовать, изучать, изменять и распространять программу при соблюдении условий GPL.",
                "Ниже приведён оригинальный юридически значимый текст GPL v3 на английском языке. Этот локализованный раздел предназначен для удобства и не заменяет оригинал лицензии.",
                "Уведомления сторонних компонентов",
                "Ниже перечислены сторонние библиотеки, исходные фрагменты и загружаемые компоненты, используемые Shadowsocks Reborn 5.2.31. Каждый компонент сохраняет собственную лицензию и не перелицензируется как часть Shadowsocks Reborn.",
                "Названия лицензий, copyright-уведомления, ограничения ответственности и иные юридические условия сохраняются на языке оригинала, чтобы не искажать их смысл неофициальным переводом.",
                "Оригинальные уведомления и тексты лицензий"),
            "zh" when name.Contains("TW", StringComparison.OrdinalIgnoreCase) || name.Contains("HK", StringComparison.OrdinalIgnoreCase) || name.Contains("Hant", StringComparison.OrdinalIgnoreCase) => new(
                "Shadowsocks Reborn 授權條款",
                "Shadowsocks Reborn 依 GNU General Public License 第 3 版或其任何後續版本（GPL-3.0-or-later）散佈。您可在遵守 GPL 條款的前提下使用、研究、修改與重新散佈本程式。",
                "下方保留具法律效力的 GPL v3 英文原文。本地化說明僅供閱讀便利，不取代原始授權條款。",
                "第三方元件聲明",
                "下方列出 Shadowsocks Reborn 5.2.31 使用的第三方程式庫、來源衍生程式碼與按需下載元件。每個元件均保留其自身授權，不因整合至 Shadowsocks Reborn 而變更授權。",
                "授權名稱、著作權聲明、免責條款及其他法律文字保留原文，以避免非官方翻譯改變其法律含義。",
                "原始第三方聲明與授權文字"),
            "zh" => new(
                "Shadowsocks Reborn 许可证",
                "Shadowsocks Reborn 按 GNU General Public License 第 3 版或任何后续版本（GPL-3.0-or-later）发布。您可以在遵守 GPL 条款的前提下使用、研究、修改和重新分发本程序。",
                "下方保留具有法律意义的 GPL v3 英文原文。本地化说明仅为阅读方便，不替代原始许可证条款。",
                "第三方组件声明",
                "下方列出 Shadowsocks Reborn 5.2.31 使用的第三方库、源代码衍生部分和按需下载组件。每个组件均保留其自身许可证，不会因集成到 Shadowsocks Reborn 而被重新许可。",
                "许可证名称、版权声明、免责声明和其他法律文本保留原文，以避免非官方翻译改变其法律含义。",
                "原始第三方声明和许可证文本"),
            "ja" => new(
                "Shadowsocks Reborn ライセンス",
                "Shadowsocks Reborn は GNU General Public License バージョン 3 またはそれ以降（GPL-3.0-or-later）の条件で配布されます。GPL の条件に従う限り、利用、調査、変更、再配布ができます。",
                "以下には法的に基準となる GPL v3 の英語原文を収録しています。この翻訳説明は読みやすさのためのもので、原文のライセンス条項を置き換えるものではありません。",
                "サードパーティコンポーネント通知",
                "以下は Shadowsocks Reborn 5.2.31 が使用するサードパーティライブラリ、派生ソースコード、オンデマンドで取得するコンポーネントを示します。各コンポーネントはそれぞれのライセンスに従います。",
                "ライセンス名、著作権表示、免責事項などの法的文面は、非公式翻訳による意味の変更を避けるため原文のまま掲載します。",
                "サードパーティ通知とライセンスの原文"),
            "ko" => new(
                "Shadowsocks Reborn 라이선스",
                "Shadowsocks Reborn은 GNU General Public License 버전 3 또는 그 이후 버전(GPL-3.0-or-later)에 따라 배포됩니다. GPL 조건을 준수하는 범위에서 프로그램을 사용, 연구, 수정 및 재배포할 수 있습니다.",
                "아래에는 법적 기준이 되는 GPL v3 영문 원문을 그대로 제공합니다. 이 현지화 설명은 편의를 위한 것이며 원본 라이선스 조건을 대체하지 않습니다.",
                "서드파티 구성 요소 고지",
                "아래에는 Shadowsocks Reborn 5.2.31에서 사용하는 서드파티 라이브러리, 파생 소스 코드 및 필요 시 다운로드되는 구성 요소가 나열됩니다. 각 구성 요소는 자체 라이선스를 유지합니다.",
                "라이선스 이름, 저작권 고지, 면책 조항 및 기타 법적 문구는 비공식 번역으로 의미가 달라지는 것을 방지하기 위해 원문을 유지합니다.",
                "원본 서드파티 고지 및 라이선스 문구"),
            "fr" => new(
                "Licence de Shadowsocks Reborn",
                "Shadowsocks Reborn est distribué selon les termes de la GNU General Public License version 3 ou toute version ultérieure (GPL-3.0-or-later). Vous pouvez utiliser, étudier, modifier et redistribuer le programme dans le respect de la GPL.",
                "Le texte anglais original de la GPL v3, qui fait foi juridiquement, est reproduit ci-dessous. Cette présentation localisée est fournie pour faciliter la lecture et ne remplace pas les termes originaux de la licence.",
                "Mentions relatives aux composants tiers",
                "La section ci-dessous recense les bibliothèques tierces, le code source dérivé et les composants téléchargés à la demande utilisés par Shadowsocks Reborn 5.2.31. Chaque composant conserve sa propre licence.",
                "Les noms de licences, mentions de copyright, exclusions de garantie et autres textes juridiques restent dans leur langue d'origine afin qu'une traduction non officielle n'en modifie pas le sens.",
                "Mentions tierces et textes de licence originaux"),
            _ => new(
                "Shadowsocks Reborn License",
                "Shadowsocks Reborn is distributed under the GNU General Public License version 3 or any later version (GPL-3.0-or-later). You may use, study, modify and redistribute the program subject to the GPL terms.",
                "The legally authoritative GPL v3 text follows in its original English form.",
                "Third-party component notices",
                "The section below records third-party libraries, source-derived code and on-demand components used by Shadowsocks Reborn 5.2.31. Each component remains subject to its own license.",
                "License names, copyright notices, warranty disclaimers and other legal terms are preserved in their original wording.",
                "Original third-party notices and license text"),
        };
    }

    private sealed record LegalIntro(
        string ProductTitle,
        string ProductSummary,
        string OriginalTermsNotice,
        string NoticesTitle,
        string NoticesSummary,
        string ComponentLicenseNotice,
        string OriginalNoticesHeading);
}
