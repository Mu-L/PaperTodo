<div align="center">

# PaperTodo · A Sheet of Paper

**A few quiet, useful, unobtrusive pieces of paper on your desktop.**

A minimal Windows desktop sticky-note app built with native WPF. No main window, no account, no manager.

![version](https://img.shields.io/badge/version-v3.1-3b82f6) ![platform](https://img.shields.io/badge/platform-Windows%20x64-555) ![.NET](https://img.shields.io/badge/.NET-10-512bd4) ![UI](https://img.shields.io/badge/UI-WPF-0078d4)

[QQ Group: 551612664 — Strange Magic Research Base](https://qm.qq.com/q/Mp7spYLrig)

**Language: [中文](README.md) | English**

</div>

---

## Preview

| Papers |
| :---: |
| <img src="screenshots/Home.jpg" alt="Desktop papers" width="100%"> |

| Markdown Preview |
| :---: |
| <img src="screenshots/Md.jpg" alt="Markdown preview" width="100%"> |

| Capsule Mode | Advanced Capsules |
| :---: | :---: |
| ![Capsule mode](screenshots/Pill_Mode.gif) | ![Auto-docked capsules](screenshots/Pill_Plus.gif) |
| Papers can collapse into small capsules to save desktop space. | Collapsed capsules dock to screen edges and slide out on hover. |

---

## Philosophy

- **Paper first** — Each paper is an independent borderless window that lives directly on your desktop. There is no central dashboard.
- **Ready immediately** — Write when you need to, check things off when done. Position, size, pin state, and content are saved automatically.
- **Not a manager** — No categories, tags, search, archive, sync, accounts, statistics, or reminders.
- **Native implementation** — Built with WPF. No WebView shell, and no MSIX / complex store packaging.
- **Interaction first** — Lightweight is not only low resource use; it means short workflows, low cognitive load, and little visual noise.

> No unnecessary interaction layers. No unnecessary visual focus.

---

## Features

- **Multiple independent papers** — Each paper is its own window.
- **One app, two paper types**:
  - **Todo paper**: one item per line. Check, edit, delete, and clear completed items.
  - **Note paper**: plain text with lightweight Markdown-style highlighting and three Markdown rendering modes.
- **Capsule mode** (on by default) — Collapse a paper into a pinned mini capsule from the top-right control; open it again when needed.
- **Auto-docked capsules** (on by default) — Collapsed capsules dock along screen edges; multi-monitor layouts are supported, and you can drag a capsule to another side or monitor.
- **Minimal interaction layers** — Common actions usually take one or two steps.
- **Script capsules** — Start a note with `!p` / `!power` to run PowerShell from the note and use the capsule system fully.
- **Link notes to todos** — Drag a note onto a todo item to link it, then open the linked note from that item.
- **Theme switching** — Follow system, light, or dark.
- **Four color schemes** — Warm Paper, Ink, Forest, and Rosy.
- **Multi-language UI** — Chinese, English, Japanese, and Korean, following the system UI language.
- **Startup at login** — Run PaperTodo when Windows starts.
- **Custom tray icon** — If `PaperTodo.ico` exists next to the executable, it is used instead of the embedded icon.
- **Data safety** — Papers auto-save to `data.json` with a backup; note images are stored transactionally in a single `note-assets.lmdb` file.
- **Native paper experience** — Native WPF controls for a smooth desktop feel.
- **Command-line friendly** — Show, hide, toggle, and create papers from startup arguments for hotkey tools or scripts.
- **Note images** — Paste, drop, or pick local images; stored in `note-assets.lmdb`.
- **Fonts and sizes** — System default / Microsoft YaHei / DengXian, optional custom font files; overall and per-area size, bold, and text rendering profiles.
- **Global hotkeys** — Bind show, hide, create, and more in settings, plus side-queue capsule shortcuts.
- **Desktop integration** — Windows Snap layouts; hide expanded papers from Alt+Tab / Task View or the taskbar.

---

## Paper Features And Manual

## Paper Window

**Basic actions**

- **Move and resize**
- **Pin on top** — The pin control in the top-left toggles always-on-top.
- **Create** — Create todo and note papers from the top-right buttons.
- **Open with external editor** — Click `MD` to open the current note externally; the file suffix can be customized in settings.
- **Set title** — Paper titles can be customized.
- **Windows Snap** — Supports Snap layouts; shadows and margins collapse while snapped and restore when you leave the snap region.

**Capsules And Edge Docking**

- **Collapsed capsules** — Papers can collapse into pinned mini capsules to save space and reopen quickly.
- **Auto docking** — Collapsed capsules can dock to screen edges and slide out on hover.
- **Multi-screen queues** — Drag an edge capsule to the left, right, or another monitor; it joins that edge queue on release.
- **Collapse all** — The master capsule collapses or expands the whole edge queue; dragging it adjusts the queue start height.
- **Master capsule menu** — Right-click the master capsule for the same menu as the tray icon.
- **Expand and close area** — Options include keeping the edge capsule while expanded, remembering expand position, hiding the edge close button on hover, and limiting capsule title display length.

---

### Todo

Good for today's tasks, temporary items, and small desktop checklists.

**Basic actions**

- **Check as done**
- **Add item**
- **Drag to reorder** — Hold the `≡` handle on the right and drag up or down.
- **Drag to delete** — Drag an item to the bottom delete area.
- **Paste multiple lines** — Lines become separate items; common list prefixes are cleaned up.
- **Visual size** — Small / Medium / Large / Extra Large in settings.
- **Undo / redo** — `Ctrl+Z` / `Ctrl+Y`
- **Double-click to select** — Double-click todo text to select the whole line for copy or replace.
- **Auto-clear completed** — Optional in settings; checking an item can remove it automatically.

**Linked notes**: Drag a note from its title bar onto a todo item to link it. The item then shows an entry to open the note. When “show linked note names” is on, the note title appears next to the item.

---

### Note Paper

Note paper is not a full Markdown editor. It only helps a sheet of paper stay a little clearer.

**Formatting shortcuts**

- `Ctrl+B` — Bold.
- `Ctrl+I` — Italic.
- `Ctrl+K` — Insert link.
- `Ctrl+Z` / `Ctrl+Y` — Undo / redo.
- `Ctrl + mouse wheel` — Zoom note text in 10% steps. Click the percentage in the bottom-right to reset to 100%.

**Supported Markdown**: headings `#` to `######`, bold `**text**`, italic `*text*`, strikethrough `~~text~~`, unordered lists `-`, ordered lists `1.`, block quotes `>`, horizontal rules `---` / `***` / `___`, inline code `` `code` ``, fenced code blocks, links `[label](URL)`, and a small set of single-line inline HTML tags (`b/strong/i/em/s/del/u/code/a href`).

**Local images**

- Paste from the clipboard, drop image files, paste copied image files, or insert from the menu (including WebP; fails with a message if the system cannot decode it)
- Stored as exclusive-line internal `i:` references in local `note-assets.lmdb`, not a remote host
- **Auto-compress large images** is on by default: oversized files or long edges are compressed before import when possible
- Removing the Markdown reference hides the image; undo can still restore it

**Not supported**: tables, attachments, network images, other embeds, block-level HTML, or a full block editor.

**External editing**: The title-bar `MD` button opens a temporary `.md` file with the system default editor.

**Custom suffixes**: The `MD` button can use associated suffixes such as `.txt`, `.html`, or `.bat`; Windows opens the temp file with the linked app.

**Script capsules**: Put `!p` / `!power` on the first line; the rest runs as PowerShell. Collapsed notes show a lightning capsule — left-click runs, right-click opens the paper. Use `!pf` / `!powerf` for a persistent PowerShell process.

> Only run local scripts you trust. Do not run script content from unknown sources.

---

## Settings

The settings window has three pages: **Behavior / Visual / Shortcuts**. Turn on **Advanced mode** for items marked **(Advanced)** below. Each page can **restore page defaults**.

**Behavior**

- **Start with Windows**, normal tooltips, animations
- **Markdown rendering** — three intensity levels for note MD display
- **Fullscreen topmost policy** (Advanced) — step back or stay above external fullscreen windows
- **Hide papers from window switching** (Advanced) — expanded papers leave Alt+Tab and Task View (and the taskbar icon)
- **Hide taskbar icons** (Advanced) — expanded papers do not appear on the taskbar
- **Title bar buttons** — hide new todo, new note, or external open separately
- **External open** — temporary file suffix for the system editor
- **Script capsules** (Advanced) — prefer PowerShell 7, hide run window, persistent process
- **Capsules** — capsule mode, auto-dock, keep edge capsule when expanded, remember expand position, master capsule (collapse all), click edge capsule again to collapse; Advanced also: hide close button on hover, **title character limit**, **edge title measure length** (width only)
- **Todos and notes** — auto-clear completed, note links, show names, long linked titles, hide linked notes from capsules, run linked script on click; Advanced also: **auto-compress large images**

**Visual**

- Theme and color scheme
- **Font** — System default / Microsoft YaHei / DengXian; place `papertodo.ttf` or `papertodo.otf` next to the exe for a custom face; optional enhanced bold with a bold file such as `papertodo_bold.ttf`
- Text rendering: Standard / Soft / Sharp
- Overall scale about 80%–120%
- Note / todo / title / capsule: size (todo uses Small–Extra Large) and bold
- **Image marker display** (Advanced) — always / edit only / always hidden

**Shortcuts**

- Bind global hotkeys for show all, hide all, toggle visibility, new todo, new note, and exit
- **Quick-launch side capsules** (off by default): left/right queue keys 1–9 open edge capsules
- Queue shortcuts can open **at the cursor**; hotkey-created papers also appear near the cursor
- In capsule mode, **Esc** collapses the capsule quickly

---

## Tray Entry

PaperTodo has no main window. The tray icon is the only global entry point.

### Tray Actions

- **Double-click tray icon** — Show and bring back all papers.
- **Right-click tray icon** — Open the menu (version at the top).
- **Settings** — Open the settings window.
- **List toolbar** — Toggle show/hide all papers; create a todo or note paper.
- **Master capsule** — Right-click the master capsule at the top of an edge queue for the same menu as the tray.
- **Delete paper** — Click `×` on a row, then Confirm or Cancel.

### Startup Arguments

Use from hotkey tools, scripts, or Windows shortcuts:

```text
PaperTodo.exe --show       Show and bring back all papers
PaperTodo.exe --hide       Hide all papers while keeping the app running in the tray
PaperTodo.exe --toggle     Hide all if any paper is visible; otherwise show all
PaperTodo.exe --new-todo   Create a new todo paper
PaperTodo.exe --new-note   Create a new note paper
PaperTodo.exe --exit       Save state and exit
PaperTodo.exe --language en-US  Start with the specified default UI language
```

The `--` prefix is optional; aliases include `open` = `show` and `quit` = `exit`.

`--language` accepts `zh-CN`, `en-US`, `ja-JP`, `ko-KR`, and regional variants, or `--language=en-US`; aliases `--lang` and `--default-language`. It only sets the UI language when the primary instance starts; it does not switch a running instance or write to `data.json`.

If PaperTodo is already running, a second start with arguments forwards the command and exits. A second start with no arguments shows and brings back all papers.

---

## Data And Files

Data lives next to the executable:

```text
PaperTodo/
├─ PaperTodo.exe
├─ data.json          Main data file
├─ data.backup.json   Backup written before each save; used if the main file is damaged
├─ note-assets.lmdb   Note image assets
└─ PaperTodo.ico      Optional custom tray icon (used before the embedded icon)
```

Edits auto-save; each normal save updates `data.backup.json` first.  
On a crash, `PaperTodo.crash.log` may be written for diagnostics; state still comes from `data.json` and the backup.  
You may also place custom fonts: `papertodo.ttf` / `papertodo.otf` (optional bold files such as `papertodo_bold`).

> Warning: Do not put the app in a read-only folder, or it may fail to save.  
> Exit from the tray before copying these files for backup or migration.

---

## Download And Verification

GitHub Actions builds two Windows x64 single-file executables as Release assets:

- **`...-self-contained.exe`** — Self-contained with the .NET Runtime.
- **`...-no-runtime.exe`** — No bundled runtime (requires a local .NET Desktop Runtime).

Each build includes Sigstore signatures (`.sig` / `.crt`). Use the asset hashes shown on the GitHub Release page for checksums.

Release notes are taken from the matching section in [`CHANGELOG.md`](CHANGELOG.md).

---

## Build And Dependencies

```powershell
dotnet build -c Release
```

Local packaging only builds the no-runtime single file; cloud Releases publish both self-contained and no-runtime builds.

- **Windows / .NET 10 / WPF** — Runtime and UI framework.
- **CMake / Visual Studio C++ toolchain** — Builds the native LMDB library shipped with PaperTodo.
- **[LMDB](https://github.com/LMDB/lmdb)** — Single-file transactional note image storage.
- **[AvalonEdit](https://github.com/icsharpcode/AvalonEdit)** — Note editing and light Markdown highlighting.
- **[Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon)** — Tray icon and menu.

## Thanks

Thanks to the [linux.do](https://linux.do/) community.

---

## Star History

[![PaperTodo Star History Chart](https://api.star-history.com/svg?repos=snownico0722/PaperTodo&type=Date)](https://star-history.com/#snownico0722/PaperTodo&Date)
