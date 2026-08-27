from __future__ import annotations

from pathlib import Path
import re
import sys

V33_ANCHOR = '- **已完成自动置底**：待办新增“已完成自动置底”选项，勾选完成后自动移到已完成区域末尾，取消完成时移回未完成区域末尾（开启自动清除时暂不触发）。'
FIX_COMPLETED = '- 修复开启“已完成待办自动置底”后，通过底部 ＋、Enter 或多行粘贴新增待办时，已完成事项可能不再保持置底的问题。'
FIX_SHORTCUT = '- 修复部分输入法状态下录制全局快捷键时，按键可能被错误识别、导致快捷键无法正常保存或触发的问题。'
TELEMETRY_LOG_RE = re.compile(r'\n- \*\*匿名使用统计\*\*：[^\n]*\n')


def update_v33_changelog(text: str) -> str:
    marker = '### v3.3'
    start = text.find(marker)
    if start < 0:
        raise RuntimeError('CHANGELOG: v3.3 section not found')
    prefix, section = text[:start], text[start:]
    if V33_ANCHOR not in section:
        raise RuntimeError('CHANGELOG: v3.3 completed-ordering anchor not found')

    additions: list[str] = []
    if FIX_COMPLETED not in section:
        additions.append(FIX_COMPLETED)
    if FIX_SHORTCUT not in section:
        additions.append(FIX_SHORTCUT)
    if additions:
        section = section.replace(V33_ANCHOR, V33_ANCHOR + '\n' + '\n'.join(additions), 1)
    return prefix + section


def update_main_changelog() -> None:
    path = Path('CHANGELOG.md')
    text = path.read_text(encoding='utf-8')
    text = TELEMETRY_LOG_RE.sub('\n', text)
    text = update_v33_changelog(text)

    unreleased_end = text.find('### v0.1')
    if unreleased_end < 0:
        raise RuntimeError('CHANGELOG: v0.1 boundary not found')
    unreleased, history = text[:unreleased_end], text[unreleased_end:]

    unreleased = unreleased.replace(
        '**插件与正文扩展（协议 2.0）**',
        '**插件与正文扩展（协议 2.1）**',
        1,
    )
    unreleased = unreleased.replace(
        '宿主使用 2.0 协议；',
        '宿主以 2.1 为最新协议并继续兼容加载 2.0 插件；',
        1,
    )

    protocol21 = (
        '  - **协议 2.1 宿主原生扩展**：插件可为待办项发布由 PaperTodo 统一渲染的行内/右键操作，'
        '并可向任意现有纸片发布不可点击的顶栏标签；触发待办操作时会收到该事项的当前状态快照。'
    )
    if protocol21 not in unreleased:
        base_line = next(
            (line for line in unreleased.splitlines() if line.startswith('  - **协议 2.0 全面能力**：')),
            None,
        )
        if not base_line:
            raise RuntimeError('CHANGELOG: Protocol 2.0 capability anchor not found')
        unreleased = unreleased.replace(base_line, base_line + '\n' + protocol21, 1)

    path.write_text(unreleased + history, encoding='utf-8')


def update_todo_rules() -> None:
    path = Path('TodoRules.cs')
    text = path.read_text(encoding='utf-8')
    if 'ApplyCompletedOrdering(' in text:
        return

    insert = '''\n    public static bool ApplyCompletedOrdering(List<PaperItem> items, bool enabled)\n    {\n        if (!enabled || items.Count < 2)\n        {\n            return false;\n        }\n\n        var reordered = items\n            .OrderBy(item => item.Order)\n            .Where(item => !item.Done)\n            .Concat(items.OrderBy(item => item.Order).Where(item => item.Done))\n            .ToList();\n        if (items.Select(item => item.Id).SequenceEqual(reordered.Select(item => item.Id)))\n        {\n            return false;\n        }\n\n        items.Clear();\n        items.AddRange(reordered);\n        for (var index = 0; index < items.Count; index++)\n        {\n            items[index].Order = index;\n        }\n        return true;\n    }\n'''
    closing = '\n}\n'
    if not text.endswith(closing):
        raise RuntimeError('TodoRules.cs: unexpected file ending')
    path.write_text(text[:-len(closing)] + insert + closing, encoding='utf-8')


def update_todo_insert_paths() -> None:
    path = Path('PaperWindow.Todo.cs')
    text = path.read_text(encoding='utf-8')
    call = '''        TodoRules.ApplyCompletedOrdering(\n            _paper.Items,\n            _controller.State.AutoMoveCompletedTodosToBottom);\n'''

    paste_old = '''        ordered.InsertRange(insertIndex, newItems);\n        _paper.Items = ordered;\n        NormalizeTodoItems();\n        NormalizeOrders();\n        _controller.MarkDirty();\n'''
    paste_new = '''        ordered.InsertRange(insertIndex, newItems);\n        _paper.Items = ordered;\n        NormalizeTodoItems();\n        NormalizeOrders();\n''' + call + '''        _controller.MarkDirty();\n'''

    add_old = '''        ordered.Insert(index, newItem);\n        _paper.Items = ordered;\n        NormalizeTodoItems();\n        NormalizeOrders();\n        _controller.MarkDirty();\n'''
    add_new = '''        ordered.Insert(index, newItem);\n        _paper.Items = ordered;\n        NormalizeTodoItems();\n        NormalizeOrders();\n''' + call + '''        _controller.MarkDirty();\n'''

    if call not in text:
        if text.count(paste_old) != 1:
            raise RuntimeError(f'PaperWindow.Todo.cs: expected one paste anchor, found {text.count(paste_old)}')
        if text.count(add_old) != 1:
            raise RuntimeError(f'PaperWindow.Todo.cs: expected one add anchor, found {text.count(add_old)}')
        text = text.replace(paste_old, paste_new, 1)
        text = text.replace(add_old, add_new, 1)
        path.write_text(text, encoding='utf-8')


def update_3x() -> None:
    changelog = Path('CHANGELOG.md')
    text = changelog.read_text(encoding='utf-8')
    text = TELEMETRY_LOG_RE.sub('\n', text)
    text = update_v33_changelog(text)
    changelog.write_text(text, encoding='utf-8')
    update_todo_rules()
    update_todo_insert_paths()


def main() -> None:
    target = sys.argv[1] if len(sys.argv) > 1 else ''
    if target == 'main':
        update_main_changelog()
    elif target == '3.x':
        update_3x()
    else:
        raise SystemExit('usage: maintenance_patch.py <main|3.x>')


if __name__ == '__main__':
    main()
