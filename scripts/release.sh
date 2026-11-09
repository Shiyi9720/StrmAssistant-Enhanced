#!/usr/bin/env bash
# 一键发布脚本：bump 版本 → 构建 → 打包 → 更新 manifest/CHANGELOG → 提交打 tag → 建 Release
#
# 用法：
#   scripts/release.sh 1.0.2                     # 用 CHANGELOG 里该版本的小节作为发布说明
#   scripts/release.sh 1.0.2 --notes-file n.md   # 或用指定文件作为发布说明
#   scripts/release.sh 1.0.2 --dry-run           # 只做构建/打包/校验，不改 git、不发 Release
#
# 前置：dotnet、zip、gh（已登录且有当前仓库权限）、git 工作区干净
set -euo pipefail

VERSION=""
NOTES_FILE=""
DRY_RUN=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) DRY_RUN=1; shift ;;
    --notes-file) NOTES_FILE="$2"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) VERSION="$1"; shift ;;
  esac
done

[ -n "$VERSION" ] || { echo "用法: scripts/release.sh <x.y.z> [--dry-run] [--notes-file f]"; exit 1; }
echo "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || { echo "版本号必须是 x.y.z"; exit 1; }

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
TAG="v$VERSION"
PROJ="StrmAssistant/StrmAssistant.csproj"
OUT="StrmAssistant/bin/Release/netstandard2.1"
DIST="$ROOT/dist"
REPO_SLUG="$(gh repo view --json nameWithOwner -q .nameWithOwner)"

echo "==> 版本 ${VERSION}（tag ${TAG}），仓库 $REPO_SLUG"

# 1. 版本号
python3 - "$PROJ" "$VERSION" <<'PY'
import re, sys, pathlib
proj, ver = pathlib.Path(sys.argv[1]), sys.argv[2]
s = proj.read_text(encoding="utf-8")
s = re.sub(r"<AssemblyVersion>[^<]*</AssemblyVersion>", f"<AssemblyVersion>{ver}</AssemblyVersion>", s)
s = re.sub(r"<FileVersion>[^<]*</FileVersion>", f"<FileVersion>{ver}</FileVersion>", s)
s = re.sub(r"<InformationalVersion>[^<]*</InformationalVersion>",
           f"<InformationalVersion>{ver}+emby.4.10.2.0</InformationalVersion>", s)
proj.write_text(s, encoding="utf-8")
print("   版本号已写入", proj)
PY

# 2. 构建
echo "==> 构建"
export PATH="$HOME/.dotnet:$PATH"
dotnet build -c Release "$PROJ" 2>&1 | grep -E "error|Build succeeded" | tail -3
ls "$OUT/StrmAssistantLite.dll" >/dev/null || { echo "构建产物缺失"; exit 1; }

# 3. 打包（两种布局：插件目录布局 + 目录源布局）
echo "==> 打包"
rm -rf "$DIST/plugins"; mkdir -p "$DIST/plugins/StrmAssistantLite.deps" "$DIST/plugins/zh" "$DIST/plugins/zh-hant"
cp "$OUT/StrmAssistantLite.dll" "$DIST/plugins/"
for f in "$OUT"/*.dll; do b="$(basename "$f")"; [ "$b" = "StrmAssistantLite.dll" ] && continue; cp "$f" "$DIST/plugins/StrmAssistantLite.deps/"; done
cp "$OUT/zh/StrmAssistantLite.resources.dll" "$DIST/plugins/zh/" 2>/dev/null || true
cp "$OUT/zh-hant/StrmAssistantLite.resources.dll" "$DIST/plugins/zh-hant/" 2>/dev/null || true

FULL_ZIP="StrmAssistantLite-${TAG}-emby4.10.zip"
CAT_ZIP="StrmAssistant-${VERSION}.zip"
( cd "$DIST" && shasum -a 256 $(find plugins -type f | sort) > SHA256SUMS.txt && rm -f "$FULL_ZIP" && zip -qr "$FULL_ZIP" plugins README-安装说明.md SHA256SUMS.txt )
rm -rf "$DIST/catalog_pkg"; mkdir -p "$DIST/catalog_pkg"; cp -R "$DIST/plugins/." "$DIST/catalog_pkg/"
( cd "$DIST/catalog_pkg" && rm -f "../$CAT_ZIP" && zip -qr "../$CAT_ZIP" . )
if command -v md5 >/dev/null 2>&1 && [ "$(uname)" = "Darwin" ]; then SUM="$(md5 -q "$DIST/$CAT_ZIP" | tr 'a-f' 'A-F')"; else SUM="$(md5sum "$DIST/$CAT_ZIP" | cut -d' ' -f1 | tr 'a-f' 'A-F')"; fi
echo "   完整包 $FULL_ZIP / 目录包 ${CAT_ZIP}（MD5 ${SUM}）"

# 4. 更新 manifest.json
echo "==> 更新 manifest.json"
python3 - "$ROOT/manifest.json" "$VERSION" "$REPO_SLUG" "$SUM" "$TAG" "$NOTES_FILE" <<'PY'
import json, sys, pathlib, datetime, re
mf, ver, slug, checksum, tag, notes_file = sys.argv[1:7]
p = pathlib.Path(mf); data = json.loads(p.read_text(encoding="utf-8"))
entry = data[0]
changelog = ""
if notes_file and pathlib.Path(notes_file).exists():
    changelog = pathlib.Path(notes_file).read_text(encoding="utf-8").strip()
else:
    cl = pathlib.Path("CHANGELOG.md")
    if cl.exists():
        text = cl.read_text(encoding="utf-8")
        m = re.search(rf"^##\s+{re.escape(ver)}[^\n]*\n(.*?)(?=^##\s|\Z)", text, re.S | re.M)
        if m: changelog = m.group(1).strip()
versions = [v for v in entry.get("versions", []) if v.get("version") != ver]
versions.insert(0, {
    "version": ver,
    "changelog": changelog or f"{ver}",
    "targetAbi": "4.10.2.0",
    "sourceUrl": f"https://github.com/{slug}/releases/download/{tag}/StrmAssistant-{ver}.zip",
    "checksum": checksum,
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "dependencies": [],
})
entry["versions"] = versions
p.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"   manifest 已加入 {ver}")
PY

if [ "$DRY_RUN" = "1" ]; then
  echo "==> dry-run：已跳过 git 提交、打 tag 与创建 Release"
  echo "    产物：$DIST/$FULL_ZIP"
  echo "          $DIST/$CAT_ZIP"
  exit 0
fi

# 5. 提交 + tag + 推送
echo "==> 提交并打 tag"
git add -A "$PROJ" manifest.json CHANGELOG.md
git commit -m "chore(release): v$VERSION" || echo "   （无变更可提交）"
git tag -f "$TAG"
BRANCH="$(git rev-parse --abbrev-ref HEAD)"
git push origin "$BRANCH"
git push -f origin "$TAG"

# 6. 建 Release
echo "==> 创建 Release $TAG"
NOTES="$DIST/RELEASE-NOTES-$TAG.md"
if [ -n "$NOTES_FILE" ] && [ -f "$NOTES_FILE" ]; then cp "$NOTES_FILE" "$NOTES"; else
  python3 - "$VERSION" "$NOTES" <<'PY'
import sys, pathlib, re
ver, out = sys.argv[1], pathlib.Path(sys.argv[2])
text = pathlib.Path("CHANGELOG.md").read_text(encoding="utf-8")
m = re.search(rf"^##\s+{re.escape(ver)}[^\n]*\n(.*?)(?=^##\s|\Z)", text, re.S | re.M)
body = (m.group(1).strip() if m else ver)
out.write_text(f"**版本 {ver}**｜适配 Emby 4.10.2（`4.10.2.0`）\n\n{body}\n\n"
               "**升级方式**：插件内置 `Update Plugin` 任务会自动更新主程序；若本次改动了依赖或中文资源，请下载完整包覆盖。\n",
               encoding="utf-8")
PY
fi
gh release create "$TAG" --repo "$REPO_SLUG" --target "$BRANCH" \
  --title "$TAG for Emby 4.10.2 (4.10.2.0)" \
  --notes-file "$NOTES" \
  "$DIST/$FULL_ZIP" "$DIST/$CAT_ZIP" "$DIST/plugins/StrmAssistantLite.dll"

echo "==> 完成：$(gh release view "$TAG" --repo "$REPO_SLUG" --json url -q .url)"
