#!/usr/bin/env python3
"""Idempotently wire the Todo hot-reload hook and the Dida365 settings UI.

Upstream DeskBox ships as a fast-moving 1.5.x line. We keep every upstream
file byte-for-byte untouched in the fork; CI runs this script before each build
and injects the wiring into the checked-out working tree only. That makes
"Sync fork" from upstream conflict-free and impossible to silently undo.

Handles LF and CRLF (Windows Actions checkouts convert line endings), BOM, and
small upstream formatting changes via anchored lookups.

Usage:
    python scripts/apply_sync_patch.py            # apply if missing
    python scripts/apply_sync_patch.py --check   # verify only, exit 1 if missing
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

VIEW_MODEL = pathlib.Path("src/DeskBox/ViewModels/TodoWidgetViewModel.cs")
APPEARANCE_VM = pathlib.Path("src/DeskBox/ViewModels/TodoWidgetViewModel.FilteringAndAppearance.cs")
HOT_RELOAD = pathlib.Path("src/DeskBox/ViewModels/TodoSyncHotReload.cs")
SETTINGS_XAML = pathlib.Path("src/DeskBox/Views/SettingsWindow.xaml")
SETTINGS_VM = pathlib.Path("src/DeskBox/ViewModels/SettingsViewModel.cs")
UPDATE_SERVICE = pathlib.Path("src/DeskBox/Services/AppUpdateService.cs")

MARKER_CTOR = "AttachSync(_store);"
MARKER_DISPOSE = "DetachSync();"
MARKER_XAML = 'x:Name="DidaSyncEnabledSwitch"'
MARKER_TODO_UI = 'x:Name="TodoColorFilterSwitch"'
MARKER_BRANDING = "WangMCweijia"

# --- Todo appearance switch (colour filter strip on/off) ------------------
COLOR_FILTER_OLD = "    public Visibility ColorFilterVisibility => Visibility.Visible;"
COLOR_FILTER_NEW = (
    "    public Visibility ColorFilterVisibility => TodoUiOptions.ShowColorFilter\n"
    "        ? Visibility.Visible\n"
    "        : Visibility.Collapsed;"
)
REFRESH_ANCHOR = (
    "    private void RefreshVisibleStateProperties()\n"
    "    {\n"
    "        OnPropertyChanged(nameof(HasVisibleItems));\n"
)
REFRESH_PATCHED = (
    "    private void RefreshVisibleStateProperties()\n"
    "    {\n"
    "        EnsureColorFilterAllowedIfHidden();\n"
    "        OnPropertyChanged(nameof(HasVisibleItems));\n"
)
MARKER_REFRESH = "EnsureColorFilterAllowedIfHidden();"

# Rebranding: this fork keeps the app pointing at its own repository instead of
# the upstream site. Every entry is (file, old, new) and is skipped when the
# new text is already present, so re-running is a no-op.
BRAND_REPO_URL = "https://github.com/WangMCweijia/DeskBox"
BRAND_RELEASES_URL = "https://github.com/WangMCweijia/DeskBox/releases"
BRAND_MANIFEST_URL = (
    "https://github.com/WangMCweijia/DeskBox/releases/latest/download/stable.json"
)
BRAND_RELEASE_API = "https://api.github.com/repos/WangMCweijia/DeskBox/releases/latest"
BRAND_DOWNLOAD_PREFIX = "/WangMCweijia/DeskBox/releases/download/"

BRAND_REPLACEMENTS = (
    (SETTINGS_VM, 'private const string RepositoryUrl = "https://github.com/Tianyu199509/DeskBox";',
     f'private const string RepositoryUrl = "{BRAND_REPO_URL}";'),
    (SETTINGS_VM, 'private const string OfficialWebsiteUrl = "https://deskbox.fun";',
     f'private const string OfficialWebsiteUrl = "{BRAND_REPO_URL}";'),
    (UPDATE_SERVICE, 'public const string DefaultManifestUrl = "https://deskbox.fun/update/stable.json";',
     f'public const string DefaultManifestUrl = "{BRAND_MANIFEST_URL}";'),
    (UPDATE_SERVICE,
     'public const string DefaultGitHubLatestReleaseApiUrl = "https://api.github.com/repos/Tianyu199509/DeskBox/releases/latest";',
     f'public const string DefaultGitHubLatestReleaseApiUrl = "{BRAND_RELEASE_API}";'),
    (UPDATE_SERVICE, 'public const string DefaultManualDownloadUrl = "https://deskbox.fun/download";',
     f'public const string DefaultManualDownloadUrl = "{BRAND_RELEASES_URL}";'),
    (UPDATE_SERVICE, '"/Tianyu199509/DeskBox/releases/download/"',
     f'"{BRAND_DOWNLOAD_PREFIX}"'),
    (UPDATE_SERVICE, '"https://github.com/Tianyu199509/DeskBox/releases/tag/v{version.Trim()}"',
     '"https://github.com/WangMCweijia/DeskBox/releases/tag/v{version.Trim()}"'),
)

ABOUT_ME_BUTTON_RE = re.compile(
    r'[ \t]*<Button\s+x:Name="AboutMeButton"[\s\S]*?</Button>\r?\n'
)
ABOUT_SITE_BUTTON_RE = re.compile(
    r'[ \t]*<Button\s+x:Name="AboutWebsiteButton"[\s\S]*?</Button>\r?\n'
)

TEST_CONTRACT = pathlib.Path("tests/DeskBox.Tests/ModuleBoundaryContractTests.cs")
MARKER_BUDGET = '["src/DeskBox/Services/DidaSyncService.cs"]'
MARKER_BUDGET_UI = '["src/DeskBox/Services/TodoUiOptions.cs"]'
BUDGET_ANCHOR = '        ["src/DeskBox/Services/AotShellMoveFixture.cs"] = 1,\n'
BUDGET_UI_LINE = '        ["src/DeskBox/Services/TodoUiOptions.cs"] = 1,\n'
BUDGET_ENTRIES = (
    "        // Dida365 sync writes JSON atomically (temp file + File.Move): the\n"
    "        // hot-reload signal, the sync state, and dida-sync.json itself.\n"
    '        ["src/DeskBox/Services/DidaSyncConfig.cs"] = 1,\n'
    '        ["src/DeskBox/Services/DidaSyncService.cs"] = 2,\n'
    + BUDGET_UI_LINE
)

CTOR_RE = re.compile(
    r"(?m)^(?P<body>\s*)_localizationService\.LanguageChanged \+= OnLanguageChanged;\n"
    r"(?P<closing>\s*)\}\n"
)
DISPOSE_RE = re.compile(
    r"(?m)^(?P<body>\s*)_localizationService\.LanguageChanged -= OnLanguageChanged;\n"
    r"(?P<closing>\s*)\}\n"
)

# Inserted after the last SettingsExpander of the Todo settings section and
# before the Music section template. Every control is looked up at runtime via
# FindCreatedSectionElement, so no upstream C# file has to change.
DIDA_XAML = """                            <toolkit:SettingsExpander
                                HorizontalContentAlignment="Stretch"
                                IsEnabled="{Binding Enabled}"
                                Loaded="DidaSyncSection_Loaded"
                                Description="把待办与滴答清单双向同步；手机上的改动约 10 秒后出现在这里"
                                Header="滴答清单同步">
                                <toolkit:SettingsExpander.HeaderIcon>
                                    <FontIcon Glyph="&#xE895;" />
                                </toolkit:SettingsExpander.HeaderIcon>
                                <ToggleSwitch x:Name="DidaSyncEnabledSwitch" MinWidth="0" OffContent="" OnContent="" Toggled="DidaSyncEnabled_Toggled" />
                                <toolkit:SettingsExpander.Items>
                                    <toolkit:SettingsCard HorizontalContentAlignment="Stretch" Description="滴答清单网页版 → 设置 → 账户与安全 → API 口令">
                                        <StackPanel Spacing="8">
                                            <TextBox x:Name="DidaTokenBox" PlaceholderText="粘贴 dp_ 开头的 API 口令" />
                                            <StackPanel Orientation="Horizontal" Spacing="8">
                                                <Button x:Name="DidaConnectButton" Content="测试并载入清单" Click="DidaConnect_Click" />
                                                <Button Content="保存" Click="DidaSave_Click" />
                                            </StackPanel>
                                        </StackPanel>
                                    </toolkit:SettingsCard>
                                    <toolkit:SettingsCard HorizontalContentAlignment="Stretch" Description="勾选要汇总到 DeskBox 的清单，可多选；收集箱也在里面">
                                        <StackPanel Spacing="8">
                                            <ListView x:Name="DidaProjectList"
                                                      HorizontalAlignment="Stretch"
                                                      MaxHeight="220"
                                                      SelectionMode="Multiple"
                                                      SelectionChanged="DidaProject_SelectionChanged" />
                                            <StackPanel Orientation="Horizontal" Spacing="8">
                                                <Button Content="全选" Click="DidaSelectAll_Click" />
                                                <Button Content="全不选" Click="DidaSelectNone_Click" />
                                            </StackPanel>
                                        </StackPanel>
                                    </toolkit:SettingsCard>
                                    <toolkit:SettingsCard HorizontalContentAlignment="Stretch">
                                        <TextBlock x:Name="DidaStatusText" TextWrapping="Wrap" Text="未配置" />
                                    </toolkit:SettingsCard>
                                </toolkit:SettingsExpander.Items>
                            </toolkit:SettingsExpander>
                            <toolkit:SettingsCard
                                HorizontalContentAlignment="Stretch"
                                Loaded="TodoUiSection_Loaded"
                                Description="待办格子顶部的颜色筛选条，连续 8 个色点；关掉后只显示任务列表"
                                Header="显示颜色筛选条">
                                <ToggleSwitch x:Name="TodoColorFilterSwitch" MinWidth="0" OffContent="" OnContent="" Toggled="TodoColorFilter_Toggled" />
                            </toolkit:SettingsCard>
"""


def read_text(path: pathlib.Path) -> tuple[str, bool, str]:
    raw = path.read_bytes()
    has_bom = raw.startswith(b"\xef\xbb\xbf")
    decoded = raw.decode("utf-8-sig")
    newline = "\r\n" if "\r\n" in decoded else "\n"
    return decoded.replace("\r\n", "\n"), has_bom, newline


def write_text(path: pathlib.Path, text: str, has_bom: bool, newline: str) -> None:
    if newline == "\r\n":
        text = text.replace("\n", "\r\n")
    data = text.encode("utf-8")
    if has_bom:
        data = b"\xef\xbb\xbf" + data
    path.write_bytes(data)


def inject(match: re.Match[str], call: str) -> str:
    body = match.group("body")
    return (
        f"{body}_localizationService.LanguageChanged "
        f"{'+=' if call == MARKER_CTOR else '-='} OnLanguageChanged;\n"
        f"{body}{call}\n"
        f"{match.group('closing')}}}\n"
    )


def patch_view_model(check: bool) -> int:
    if not VIEW_MODEL.is_file():
        print(f"MISSING: {VIEW_MODEL}", file=sys.stderr)
        return 2

    text, has_bom, newline = read_text(VIEW_MODEL)
    need_ctor = MARKER_CTOR not in text
    need_dispose = MARKER_DISPOSE not in text

    if not need_ctor and not need_dispose:
        print("OK: Todo sync wiring already present.")
        return 0

    if check:
        print("MISSING: Todo sync wiring is not present.", file=sys.stderr)
        return 1

    if need_ctor:
        text, count = CTOR_RE.subn(lambda m: inject(m, MARKER_CTOR), text, count=1)
        if count == 0:
            print("ERROR: constructor anchor changed upstream; patch by hand.", file=sys.stderr)
            return 3
        print("patched: AttachSync(_store) in constructor")

    if need_dispose:
        text, count = DISPOSE_RE.subn(lambda m: inject(m, MARKER_DISPOSE), text, count=1)
        if count == 0:
            print("ERROR: Dispose anchor changed upstream; patch by hand.", file=sys.stderr)
            return 3
        print("patched: DetachSync() in Dispose")

    write_text(VIEW_MODEL, text, has_bom, newline)
    print("OK: Todo sync wiring applied.")
    return 0


def patch_settings_xaml(check: bool) -> int:
    if not SETTINGS_XAML.is_file():
        print(f"MISSING: {SETTINGS_XAML}", file=sys.stderr)
        return 2

    text, has_bom, newline = read_text(SETTINGS_XAML)
    if MARKER_XAML in text:
        print("OK: Dida365 settings section already present.")
        return 0

    if check:
        print("MISSING: Dida365 settings section is not present.", file=sys.stderr)
        return 1

    marker = '<DataTemplate x:Key="MusicSettingsSectionTemplate"'
    marker_index = text.find(marker)
    if marker_index == -1:
        print("ERROR: MusicSettingsSectionTemplate anchor not found.", file=sys.stderr)
        return 3

    anchor = "</toolkit:SettingsExpander>"
    anchor_index = text.rfind(anchor, 0, marker_index)
    if anchor_index == -1:
        print("ERROR: no Todo SettingsExpander to anchor on.", file=sys.stderr)
        return 3

    insert_at = anchor_index + len(anchor)
    # Keep the newline that already followed the anchor.
    text = text[:insert_at] + "\n" + DIDA_XAML + text[insert_at:]
    write_text(SETTINGS_XAML, text, has_bom, newline)
    print("patched: Dida365 sync section in SettingsWindow.xaml")
    return 0


def strip_about_buttons(check: bool) -> int:
    """Drop the upstream "about me" and "official website" buttons."""
    if not SETTINGS_XAML.is_file():
        print(f"MISSING: {SETTINGS_XAML}", file=sys.stderr)
        return 2

    text, has_bom, newline = read_text(SETTINGS_XAML)
    removed = 0
    for pattern in (ABOUT_ME_BUTTON_RE, ABOUT_SITE_BUTTON_RE):
        text, count = pattern.subn("", text)
        removed += count

    if removed == 0:
        print("OK: about buttons already removed.")
        return 0

    if check:
        print("MISSING: about buttons are still in the XAML.", file=sys.stderr)
        return 1

    write_text(SETTINGS_XAML, text, has_bom, newline)
    print(f"removed: {removed} about button(s) from SettingsWindow.xaml")
    return 0


def patch_branding(check: bool) -> int:
    """Point the About page and the update feed at this fork."""
    per_file: dict[pathlib.Path, list[tuple[str, str]]] = {}
    for path, old, new in BRAND_REPLACEMENTS:
        per_file.setdefault(path, []).append((old, new))

    pending: dict[pathlib.Path, tuple[str, bool, str]] = {}
    for path, pairs in per_file.items():
        if not path.is_file():
            print(f"MISSING: {path}", file=sys.stderr)
            return 2

        text, has_bom, newline = read_text(path)
        changed = False
        for old, new in pairs:
            if new in text:
                continue
            if old not in text:
                print(
                    f"ERROR: branding anchor missing in {path}: {old[:70]}",
                    file=sys.stderr,
                )
                return 3
            text = text.replace(old, new)
            changed = True

        if changed:
            pending[path] = (text, has_bom, newline)

    if not pending:
        print("OK: branding already points at the fork.")
        return 0

    if check:
        print("MISSING: branding replacements are not applied.", file=sys.stderr)
        return 1

    for path, (text, has_bom, newline) in pending.items():
        write_text(path, text, has_bom, newline)
        print(f"patched: branding in {path}")
    return 0


def patch_todo_ui(check: bool) -> int:
    """Wire the "show colour filter strip" switch into the widget."""
    pairs = (
        (VIEW_MODEL, COLOR_FILTER_OLD, COLOR_FILTER_NEW, "colour filter visibility"),
        (APPEARANCE_VM, REFRESH_ANCHOR, REFRESH_PATCHED, "colour filter fallback"),
    )

    pending: dict[pathlib.Path, tuple[str, bool, str]] = {}
    for path, old, new, label in pairs:
        if not path.is_file():
            print(f"MISSING: {path}", file=sys.stderr)
            return 2

        text, has_bom, newline = read_text(path)
        if new in text:
            continue
        if old not in text:
            print(f"ERROR: {label} anchor missing in {path}", file=sys.stderr)
            return 3

        pending[path] = (text.replace(old, new, 1), has_bom, newline)

    if not pending:
        print("OK: todo appearance switch already wired.")
        return 0

    if check:
        print("MISSING: todo appearance switch is not wired.", file=sys.stderr)
        return 1

    for path, (text, has_bom, newline) in pending.items():
        write_text(path, text, has_bom, newline)
        print(f"patched: todo appearance switch in {path}")
    return 0


def patch_test_budget(check: bool) -> int:
    """Register the sync engine's atomic writes in the file-mutation ratchet.

    ModuleBoundaryContractTests caps File.Move call sites per file; our JSON
    writes (signal, state, config) use temp-file + move, so the budget has to
    name them. Skipped when the test file is absent (minimal checkouts).
    """
    if not TEST_CONTRACT.is_file():
        print("SKIP: boundary-contract test not present.")
        return 0

    text, has_bom, newline = read_text(TEST_CONTRACT)
    if MARKER_BUDGET in text:
        if MARKER_BUDGET_UI in text:
            print("OK: sync file-mutation budget already registered.")
            return 0

        # Older injection only listed the sync files; append the UI options one.
        line = '        ["src/DeskBox/Services/DidaSyncService.cs"] = 2,\n'
        if line not in text:
            print("ERROR: sync budget line not found for the UI top-up.", file=sys.stderr)
            return 3

        if check:
            print("MISSING: TodoUiOptions is not in the mutation budget.", file=sys.stderr)
            return 1

        text = text.replace(line, line + BUDGET_UI_LINE, 1)
        write_text(TEST_CONTRACT, text, has_bom, newline)
        print("patched: TodoUiOptions added to the mutation budget")
        return 0

    if check:
        print("MISSING: sync file-mutation budget is not registered.", file=sys.stderr)
        return 1

    if BUDGET_ANCHOR not in text:
        print("ERROR: destructive-operation allowlist anchor changed upstream.", file=sys.stderr)
        return 3

    text = text.replace(BUDGET_ANCHOR, BUDGET_ANCHOR + BUDGET_ENTRIES, 1)
    write_text(TEST_CONTRACT, text, has_bom, newline)
    print(f"patched: sync file-mutation budget in {TEST_CONTRACT}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="verify only")
    args = parser.parse_args()

    if not HOT_RELOAD.is_file():
        print(f"MISSING: {HOT_RELOAD}", file=sys.stderr)
        return 2

    for step in (
        patch_view_model,
        patch_settings_xaml,
        patch_branding,
        strip_about_buttons,
        patch_todo_ui,
        patch_test_budget,
    ):
        result = step(args.check)
        if result != 0:
            return result

    print("OK: all sync patches applied.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
