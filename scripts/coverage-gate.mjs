#!/usr/bin/env node
// CI 覆盖率门禁：口径=仅本仓库 src/ 的 .cs（排除 obj/ 与 .axaml 伪行），
// 合并语义 = 同一行取各套件最大命中。用法：
//   node coverage-gate.mjs <threshold 如 0.83> <xml...>
// 输出仓库行覆盖率；低于阈值 exit 1。
import { readFileSync } from 'node:fs';

const threshold = Number(process.argv[2]);
const xmlPaths = process.argv.slice(3);
const repoSrc = process.cwd() + '/src/';
if (!(threshold > 0 && threshold <= 1) || xmlPaths.length === 0) {
  console.error('用法: node coverage-gate.mjs <threshold> <xml...>');
  process.exit(2);
}

const perFile = new Map();
for (const p of xmlPaths) {
  const xml = readFileSync(p, 'utf8');
  let currentFile = null;
  for (const m of xml.matchAll(/<class\b[^>]*\bfilename="([^"]+)"[^>]*>|<line\b([^>]*)\/>/g)) {
    if (m[1] !== undefined) {
      // cobertura 的 filename 在 Windows 腿是反斜杠绝对路径：归一成 '/' 再做前缀/包含判定，
      // 否则 repoSrc('/src/') 永不匹配（当前 CI 只在 ubuntu 跑此步，属前瞻性归一）
      currentFile = m[1].replaceAll('\\', '/');
      if (!perFile.has(currentFile)) perFile.set(currentFile, new Map());
    } else if (currentFile) {
      const num = /number="(\d+)"/.exec(m[2]);
      const hits = /hits="(\d+)"/.exec(m[2]);
      if (num && hits) {
        const map = perFile.get(currentFile);
        map.set(Number(num[1]), Math.max(map.get(Number(num[1])) ?? 0, Number(hits[1])));
      }
    }
  }
}

let valid = 0, covered = 0;
for (const [file, lines] of perFile) {
  if (!file.startsWith(repoSrc) || file.includes('/obj/') || file.endsWith('.axaml')) continue;
  for (const hits of lines.values()) {
    valid++;
    if (hits > 0) covered++;
  }
}

if (valid === 0) {
  console.error('未采集到本仓库 src/ 的任何行——XML 与仓库路径不匹配？');
  process.exit(2);
}

const rate = covered / valid;
console.log(`行覆盖率: ${covered}/${valid} = ${(rate * 100).toFixed(2)}%（阈值 ${(threshold * 100).toFixed(0)}%）`);
if (rate + 1e-9 < threshold) {
  console.error(`::error::行覆盖率低于基线——新增代码缺测试。补测后本地复测再合入（命令见 docs/DEVELOPMENT.md §9）`);
  process.exit(1);
}
