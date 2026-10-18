#!/usr/bin/env bash
# 在 wine 下自检 yagl-parent-bridge：
#   1. 编译 bridge 与夹具；
#   2. 以挂起诱饵（Games.exe 副本）为父进程运行夹具，断言夹具看到的父进程 = 诱饵路径；
#   3. 断言桥退出后诱饵进程已被清理。
# 环境变量：WINE 覆盖 wine 可执行文件；WINEPREFIX 覆盖 prefix（默认 ~/.cache/yagl-parent-bridge-verify/prefix）。
set -euo pipefail

here=$(cd -- "$(dirname -- "$0")" && pwd)
work="${TMPDIR:-/tmp}/yagl-parent-bridge-verify"
mkdir -p "$work"

echo "== build =="
# 经 build.sh 构建（编译选项单一来源；含 --no-insert-timestamp 可复现标记）
"$here/build.sh" "$work/yagl-parent-bridge.exe"
x86_64-w64-mingw32-gcc -Os -s -o "$work/verify-fixture.exe" "$here/verify-fixture.c" -lkernel32
cp -f "$work/verify-fixture.exe" "$work/Games.exe"

if [[ -z "${WINE:-}" ]]; then
    WINE=$(find "$HOME/.local/share/Steam/compatibilitytools.d" -maxdepth 4 -path "*/files/bin/wine" -type f 2>/dev/null | sort | tail -1)
    WINE=${WINE:-wine}
fi
export WINEPREFIX="${WINEPREFIX:-$HOME/.cache/yagl-parent-bridge-verify/prefix}"
mkdir -p "$WINEPREFIX"

to_win_path() { printf 'Z:%s' "$(printf '%s' "$1" | tr '/' '\\')"; }
result_file="$work/parent-check.txt"
rm -f "$result_file"

echo "== run (wine: $WINE, prefix: $WINEPREFIX) =="
set +e
YAGL_BRIDGE_DECOY="$(to_win_path "$work/Games.exe")" \
YAGL_BRIDGE_TARGET="$(to_win_path "$work/verify-fixture.exe")" \
YAGL_BRIDGE_CMD="\"$(to_win_path "$work/verify-fixture.exe")\" \"$(to_win_path "$result_file")\"" \
YAGL_BRIDGE_CWD="$(to_win_path "$work")" \
"$WINE" "$work/yagl-parent-bridge.exe" >/dev/null 2>&1
bridge_exit=$?
set -e
echo "bridge exit=$bridge_exit"

echo "== assert =="
if [[ ! -f "$result_file" ]]; then
    echo "FAIL: fixture result file missing"
    exit 1
fi
got=$(tr -d '\r' < "$result_file")
echo "fixture says: $got"

expected="PARENT=$(to_win_path "$work/Games.exe")"
if [[ "$got" == "$expected" ]]; then
    echo "PASS: fixture parent = decoy Games.exe"
else
    echo "FAIL: expected '$expected'"
    exit 1
fi

leftover=$("$WINE" tasklist 2>/dev/null | grep -ci "Games.exe" || true)
if [[ "$leftover" == "0" ]]; then
    echo "PASS: decoy process cleaned up"
else
    echo "FAIL: decoy process still running"
    exit 1
fi
