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
            ["SettingsDistinguishNumpadShortcutDigits"] = ["区分小键盘数字键", "Distinguish numpad digits", "テンキー数字を区別", "숫자 키패드 숫자 구분"],
            ["TipSettingsDistinguishNumpadShortcutDigits"] = ["开启后数字键与小键盘数字键可分别注册；关闭后两者混合响应，但不会修改已保存的快捷键。快速启动侧边胶囊不受影响。", "When enabled, number-row and numpad digits can be registered separately. When disabled, either key triggers the stored binding without rewriting it. Edge quick-launch sequences are unchanged.", "オンでは数字列とテンキーを別々に登録できます。オフでは保存値を書き換えず両方で反応します。端のクイック起動シーケンスには影響しません。", "켜면 숫자열과 숫자 키패드를 따로 등록할 수 있습니다. 끄면 저장된 값을 바꾸지 않고 둘 다 반응합니다. 가장자리 빠른 실행 시퀀스에는 영향을 주지 않습니다."],
            ["ShortcutNumpadModeConflictTitle"] = ["小键盘快捷键冲突", "Numpad shortcut conflict", "テンキーショートカットの競合", "숫자 키패드 단축키 충돌"],
            ["ShortcutNumpadModeConflictMessage"] = ["无法切换小键盘模式：现有快捷键存在数字键/小键盘冲突，或混合响应所需的组合已被其他程序占用。现有快捷键不会被修改。", "The numpad mode could not be changed because existing bindings conflict across number-row/numpad digits, or a required mixed-mode combination is already owned by another app. Existing bindings were not changed.", "既存の数字列/テンキー割り当てが競合しているか、混合応答に必要な組み合わせを他のアプリが使用しているため切り替えできません。既存の割り当ては変更されません。", "기존 숫자열/숫자 키패드 바인딩이 충돌하거나 혼합 응답에 필요한 조합을 다른 앱이 사용 중이라 모드를 변경할 수 없습니다. 기존 바인딩은 변경되지 않습니다."],
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
            ["TipLabsCurrentPaperTransparent"] = ["只作用于快捷键触发时拥有焦点的普通或插件纸片。", "Affects only the regular or plugin paper focused when the shortcut fires.", "ショートカット実行時にフォーカス中の通常またはプラグインの紙だけに作用します。", "단축키 실행 시 포커스된 일반 또는 플러그인 메모에만 적용됩니다."],
            ["LabsStrictCollapsePaperAfterShow"] = ["严格收起", "Strict collapse", "厳格な自動折りたたみ", "엄격한 자동 접기"],
            ["TipLabsStrictCollapsePaperAfterShow"] = ["新建或显示纸片后，若未使用它便进行了其他操作，立即收起。无需全局键鼠 Hook。", "After a paper is created or shown, collapse it when another action happens before the paper is used. No global input hook is used.", "紙を作成または表示した後、使用せず別の操作をすると直ちに折りたたみます。グローバル入力フックは使用しません。", "메모를 만들거나 표시한 뒤 사용하지 않고 다른 작업을 하면 즉시 접습니다. 전역 입력 훅은 사용하지 않습니다."],
            ["LabsDockedCapsulesNonTopmost"] = ["允许贴边胶囊非置顶", "Allow docked capsules below topmost", "端に固定したカプセルの非最前面を許可", "가장자리 캡슐 비고정 허용"],
            ["TipLabsDockedCapsulesNonTopmost"] = ["开启后贴边胶囊和主胶囊不再保持置顶；展开纸片仍按自身置顶设置。", "When enabled, docked and master capsules no longer stay topmost; expanded papers keep their own topmost setting.", "有効にすると端のカプセルとマスターカプセルは最前面を維持せず、展開した紙は個別設定に従います。", "켜면 가장자리 및 마스터 캡슐이 항상 위를 유지하지 않으며 펼친 메모는 자체 설정을 따릅니다."],
            ["LabsFocusOpacity"] = ["失焦与静止透明", "Inactive and resting transparency", "非アクティブ・静止時の透明度", "비활성·정지 투명도"],
            ["LabsRestingCapsuleOpacityIncludeMaster"] = ["覆盖主胶囊", "Include master capsule", "マスターカプセルにも適用", "마스터 캡슐에도 적용"],
            ["LabsRestingCapsuleOpacityAlways"] = ["无论是否激活都透明", "Keep transparent while active", "操作中も透明を維持", "활성 상태에서도 투명 유지"]
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
