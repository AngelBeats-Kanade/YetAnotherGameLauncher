#!/bin/sh
# 构建 yagl-parent-bridge.exe（x86_64-w64-mingw32-gcc 交叉编译，-nostdlib 精简 PE）。
# -Wl,--no-insert-timestamp 让构建可复现（PE 时间戳归零；同一源码两次构建逐字节相同），
# 嵌入资源因此可核对来源（2026-10-18 复审加固）。
# 产物默认写入 src/YetAnotherGameLauncher.Core/Resources/yagl-parent-bridge.exe（嵌入式资源），
# 也可传输出路径作第一个参数。构建后请运行 verify.sh 在 wine 下自检。
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
out=${1:-"$here/../../src/YetAnotherGameLauncher.Core/Resources/yagl-parent-bridge.exe"}

x86_64-w64-mingw32-gcc \
    -Os -nostdlib -fno-builtin -fno-stack-protector -s \
    -Wl,--entry=entry -Wl,--subsystem,console \
    -Wl,--no-insert-timestamp \
    -o "$out" \
    "$here/bridge.c" \
    -lkernel32

echo "built: $out"
ls -l "$out"
sha256sum "$out"
