using System.Globalization;
using System.Resources;
using System.Collections.Generic;

namespace PaperTodo;

public static class Strings
{
    private static readonly ResourceManager Manager = new("PaperTodo.Resources.Strings", typeof(Strings).Assembly);

    private static readonly IReadOnlyDictionary<string, string[]> Supplemental =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["LabsAdvancedShortcuts"] = ["高级快捷键", "Advanced shortcuts", "高度なショートカット", "고급 바로 가기"],
            ["LabsInteractionLock"] = ["交互锁定", "Interaction lock", "操作ロック", "상호 작용 잠금"],
            ["LabsLockAllPapers"] = ["锁定全部便签", "Lock all papers", "すべての紙をロック", "모든 메모 잠금"],
            ["TipLabsLockAllPapers"] = ["切换全部普通与插件便签的交互锁定。", "Toggle interaction lock for all regular and plugin papers.", "通常およびプラグインの紙をすべてロックします。", "일반 및 플러그인 메모를 모두 잠급니다."],
            ["LabsAllowLockIconUnlock"] = ["允许点击锁头解锁", "Allow lock icon to unlock", "ロックアイコンで解除を許可", "잠금 아이콘으로 해제 허용"],
            ["TipLabsAllowLockIconUnlock"] = ["关闭后锁头仅作提示，只能通过快捷键解锁。", "When off, the lock is only an indicator and the shortcut is required to unlock.", "オフの場合、ロックは表示のみで解除にはショートカットが必要です。", "끄면 잠금은 표시만 하며 단축키로만 해제할 수 있습니다."],
            ["LabsUnlockAllPapers"] = ["解锁全部便签", "Unlock all papers", "すべての紙のロックを解除", "모든 메모 잠금 해제"],
            ["LabsShortcutTransparency"] = ["快捷透明度", "Shortcut transparency", "ショートカット透明度", "단축키 투명도"],
            ["LabsShortcutOpacityLevel"] = ["透明度值", "Opacity level", "透明度", "투명도 값"],
            ["LabsAllPapersTransparent"] = ["切换全部纸片透明", "Toggle all papers transparent", "すべての紙の透明を切替", "모든 메모 투명 전환"],
            ["TipLabsAllPapersTransparent"] = ["部分透明时会先统一设为透明；全部已透明时再次按下才取消。", "If only some are transparent, all become transparent; press again only when all are transparent to cancel.", "一部だけ透明な場合はすべて透明にし、全て透明な場合のみ再度押すと解除します。", "일부만 투명하면 모두 투명하게 만들고, 모두 투명할 때 다시 눌러 해제합니다."],
            ["LabsAllCapsulesTransparent"] = ["切换全部胶囊透明", "Toggle all capsules transparent", "すべてのカプセルの透明を切替", "모든 캡슐 투명 전환"],
            ["TipLabsAllCapsulesTransparent"] = ["显式透明优先于空闲半透明，并统一作用于全部胶囊。", "Explicit transparency overrides idle transparency and applies to all capsules.", "明示的な透明度はアイドル透明度より優先され、全カプセルに適用されます。", "명시적 투명도는 유휴 투명도보다 우선하며 모든 캡슐에 적용됩니다."],
            ["LabsCurrentPaperTransparent"] = ["切换当前焦点纸片透明", "Toggle focused paper transparent", "フォーカス中の紙の透明を切替", "현재 포커스 메모 투명 전환"],
            ["TipLabsCurrentPaperTransparent"] = ["只作用于快捷键触发时拥有焦点的普通或插件纸片。", "Affects only the regular or plugin paper focused when the shortcut fires.", "ショートカット実行時にフォーカス中の通常またはプラグインの紙だけに作用します。", "단축키 실행 시 포커스된 일반 또는 플러그인 메모에만 적용됩니다."]
        };

    public static string Get(string key)
    {
        var resource = Manager.GetString(key, CultureInfo.CurrentUICulture);
        if (resource != null)
        {
            return resource;
        }

        if (!Supplemental.TryGetValue(key, out var values))
        {
            return key;
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "en" => values[1],
            "ja" => values[2],
            "ko" => values[3],
            _ => values[0]
        };
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }
}
