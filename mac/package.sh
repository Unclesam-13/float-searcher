#!/bin/bash
# 把编译好的程序装进 .app，签上「本机签名」，再打成 dmg。用法：package.sh 版本号
set -euo pipefail
N="${1:-0}"
cd "$(dirname "$0")"
BIN_DIR=$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)
APP="build/划词助手.app"
rm -rf build && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/FloatSearcher" "$APP/Contents/MacOS/FloatSearcher"
sed "s/__BUILD__/$N/g" Resources/Info.plist > "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
codesign --force --deep --sign - "$APP"
lipo -info "$APP/Contents/MacOS/FloatSearcher"

mkdir -p build/dmg
cp -R "$APP" build/dmg/
ln -s /Applications build/dmg/Applications
OUT="../FloatSearcher-mac-$N.dmg"
for i in 1 2 3 4 5; do
  if hdiutil create -volname "划词助手" -srcfolder build/dmg -ov -format UDZO "$OUT"; then break; fi
  echo "hdiutil 失败，重试 $i"; sleep 5
done
ls -l "$OUT"
