from __future__ import annotations

import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

OBSOLETE_FILES = [
    ROOT / "src/AppController.VirtualDesktops.cs",
    ROOT / "src/PaperWindow.VirtualDesktop.cs",
    ROOT / "src/PaperWindow.StrictAutoCollapse.cs",
    ROOT / "src/VirtualDesktopAdapter.cs",
]

STATE_FIELDS = [
    "ExperimentalStrictCollapsePaperAfterShow",
    "ExperimentalVirtualDesktopIntegration",
    "ExperimentalVirtualDesktopMoveOnShow",
    "ExperimentalVirtualDesktopMoveOnCapsuleActivation",
]

SIMPLE_CALLS = [
    "InitializeStrictAutoCollapseTracking();",
    "CancelStrictAutoCollapse();",
    "ArmStrictAutoCollapseAfterShow();",
    "RefreshExperimentalVirtualDesktopRuntime();",
    "DisposeExperimentalVirtualDesktopRuntime();",
]

RESOURCE_TOKENS = (
    "VirtualDesktop",
    "StrictCollapsePaperAfterShow",
)

RESIDUAL_TOKENS = (
    "VirtualDesktopAdapter",
    "ExperimentalVirtualDesktop",
    "PreparePaperForCurrentVirtualDesktop",
    "TryMoveToVirtualDesktop",
    "VirtualDesktopWakeReason",
    "StrictAutoCollapse",
    "ExperimentalStrictCollapsePaperAfterShow",
    "StrictCollapsePaperAfterShow",
)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8", newline="")


def remove_method_containing(text: str, marker: str) -> str:
    while marker in text:
        marker_index = text.index(marker)
        line_start = text.rfind("\n", 0, marker_index) + 1
        brace_start = text.find("{", marker_index)
        if brace_start < 0:
            raise RuntimeError(f"Could not find method body for {marker}")
        depth = 0
        i = brace_start
        in_string = False
        verbatim = False
        escape = False
        while i < len(text):
            ch = text[i]
            nxt = text[i + 1] if i + 1 < len(text) else ""
            if in_string:
                if verbatim:
                    if ch == '"' and nxt == '"':
                        i += 2
                        continue
                    if ch == '"':
                        in_string = False
                        verbatim = False
                else:
                    if escape:
                        escape = False
                    elif ch == "\\":
                        escape = True
                    elif ch == '"':
                        in_string = False
            else:
                if ch == '@' and nxt == '"':
                    in_string = True
                    verbatim = True
                    i += 2
                    continue
                if ch == '"':
                    in_string = True
                elif ch == "{":
                    depth += 1
                elif ch == "}":
                    depth -= 1
                    if depth == 0:
                        end = i + 1
                        if end < len(text) and text[end] == "\r":
                            end += 1
                        if end < len(text) and text[end] == "\n":
                            end += 1
                        if end < len(text) and text[end] == "\n":
                            end += 1
                        text = text[:line_start] + text[end:]
                        break
            i += 1
        else:
            raise RuntimeError(f"Unbalanced method body for {marker}")
    return text


def remove_call_statement(text: str, marker: str) -> str:
    lines = text.splitlines(keepends=True)
    out: list[str] = []
    i = 0
    while i < len(lines):
        if marker not in lines[i]:
            out.append(lines[i])
            i += 1
            continue
        paren = 0
        seen = False
        j = i
        while j < len(lines):
            line = lines[j]
            paren += line.count("(") - line.count(")")
            seen = seen or "(" in line
            if seen and paren <= 0 and ";" in line:
                j += 1
                break
            j += 1
        i = j
    return "".join(out)


def clean_cs(path: Path) -> None:
    text = read(path)
    original = text

    if path.name in {"EdgeCapsuleHost.cs", "MasterCapsuleWindow.cs", "ExperimentalTetherCapsuleWindow.cs"}:
        text = remove_method_containing(text, "TryMoveToVirtualDesktop(")

    # Remove the paired Strict collapse branch as one unit so no empty if/else shell survives.
    text = re.sub(
        r"\n[ \t]*if \(collapsed\)\s*\{\s*CancelStrictAutoCollapse\(\);\s*\}\s*else\s*\{\s*ArmStrictAutoCollapseAfterShow\(\);\s*\}\s*",
        "\n",
        text,
        flags=re.MULTILINE,
    )

    text = remove_call_statement(text, "PreparePaperForCurrentVirtualDesktop(")

    lines = []
    for line in text.splitlines(keepends=True):
        if any(field in line for field in STATE_FIELDS):
            continue
        if any(call in line for call in SIMPLE_CALLS):
            continue
        if any(token in line for token in RESOURCE_TOKENS) and path.name == "Strings.cs":
            continue
        lines.append(line)
    text = "".join(lines)

    if text != original:
        write(path, text)


def clean_resx(path: Path) -> None:
    text = read(path)
    original = text
    pattern = re.compile(
        r"\n?[ \t]*<data\s+name=\"[^\"]*(?:VirtualDesktop|StrictCollapsePaperAfterShow)[^\"]*\"[^>]*>.*?</data>\s*",
        flags=re.DOTALL,
    )
    text = pattern.sub("\n", text)
    if text != original:
        write(path, text)


def scan_residuals() -> list[str]:
    roots = [ROOT / "src", ROOT / "Resources", ROOT / "tests"]
    residuals: list[str] = []
    for base in roots:
        if not base.exists():
            continue
        for path in base.rglob("*"):
            if not path.is_file() or path.suffix.lower() not in {".cs", ".resx", ".csproj", ".json", ".md"}:
                continue
            try:
                text = read(path)
            except UnicodeDecodeError:
                continue
            for token in RESIDUAL_TOKENS:
                if token in text:
                    for n, line in enumerate(text.splitlines(), 1):
                        if token in line:
                            residuals.append(f"{path.relative_to(ROOT)}:{n}: {token}: {line.strip()}")
    return residuals


def run(*args: str) -> None:
    print("+", " ".join(args), flush=True)
    subprocess.run(args, cwd=ROOT, check=True)


def main() -> None:
    for path in OBSOLETE_FILES:
        if path.exists():
            path.unlink()

    for path in (ROOT / "src").rglob("*.cs"):
        clean_cs(path)

    for path in (ROOT / "Resources").glob("Strings*.resx"):
        clean_resx(path)

    # Remove temporary trigger artifacts from the final tree.
    for temp in [
        ROOT / ".ci-cleanup-trigger",
        ROOT / ".github/workflows/cleanup-obsolete-experiments.yml",
        ROOT / "tools/cleanup_obsolete_experiments.py",
    ]:
        if temp.exists():
            temp.unlink()

    residuals = scan_residuals()
    if residuals:
        print("Obsolete experiment residuals remain:")
        print("\n".join(residuals))
        raise SystemExit(2)

    run("dotnet", "restore", ".\\PaperTodo.csproj")
    run("dotnet", "build", ".\\PaperTodo.csproj", "-c", "Release", "--no-restore")

    run("git", "config", "user.name", "github-actions[bot]")
    run("git", "config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com")
    run("git", "add", "-A")
    status = subprocess.run(
        ["git", "status", "--porcelain"], cwd=ROOT, check=True, text=True, capture_output=True
    ).stdout
    if not status.strip():
        print("No cleanup changes to commit.")
        return
    run("git", "commit", "-m", "cleanup: purge obsolete experiments")
    run("git", "push", "origin", "HEAD")


if __name__ == "__main__":
    main()
