#!/bin/sh
set -eu

PROJECT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$PROJECT_DIR"

command -v node >/dev/null 2>&1 || {
  echo "未找到 Node.js，请安装 Node.js 20 或 22 LTS。" >&2
  exit 1
}
command -v pnpm >/dev/null 2>&1 || {
  echo "未找到 pnpm，请运行 corepack enable 和 corepack prepare pnpm@11.19.0 --activate。" >&2
  exit 1
}

VERSION=$(node -p "require('./package.json').version")
case "$VERSION" in
  ''|*[!0-9A-Za-z.-]*)
    echo "package.json 中的版本号格式无效：$VERSION" >&2
    exit 1
    ;;
esac

FINAL_DIR="$PROJECT_DIR/release/final-macos-$VERSION"
case "$FINAL_DIR" in
  "$PROJECT_DIR"/release/final-macos-*) ;;
  *)
    echo "拒绝清理异常路径：$FINAL_DIR" >&2
    exit 1
    ;;
esac

echo "==> 安装/校验依赖"
pnpm install --frozen-lockfile

echo "==> 编译 Intel/Apple Silicon 通用原生探测器"
sh ./scripts/build-native-macos.sh

echo "==> 运行自动化测试"
pnpm test

echo "==> 打包 macOS x64 与 arm64"
CSC_IDENTITY_AUTO_DISCOVERY=false pnpm run dist:mac

rm -rf "$FINAL_DIR"
mkdir -p "$FINAL_DIR"

GUIDE_OUT="$FINAL_DIR/客户使用说明-Electron.txt"
sed "1s/.*/利亚方舟海螺云网络诊断工具 Electron $VERSION/" \
  "$PROJECT_DIR/客户使用说明-Electron.txt" > "$GUIDE_OUT"

for ARCH in x64 arm64; do
  for EXT in dmg zip; do
    ARTIFACT="$PROJECT_DIR/release/modern/LYFZ-NetDiag-Electron-$VERSION-mac-$ARCH.$EXT"
    if [ ! -f "$ARTIFACT" ]; then
      echo "缺少打包产物：$ARTIFACT" >&2
      exit 1
    fi
    cp "$ARTIFACT" "$FINAL_DIR/"
  done
done

(
  cd "$FINAL_DIR"
  shasum -a 256 LYFZ-NetDiag-Electron-* > SHA256SUMS.txt
)

echo "==> 打包完成：$FINAL_DIR"
echo "注意：当前产物未签名、未公证，正式分发前应完成 Developer ID 签名和 Apple 公证。"
