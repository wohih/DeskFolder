#!/usr/bin/env bash
# DeskFolder 在 WorkBuddy 沙箱中构建的固定脚本。
# 解决 dotnet restore 报 "Value cannot be null. (Parameter 'path1')" (NuGet.targets 782) 的环境缺失问题：
# 沙箱进程缺少 ProgramData / ProgramFiles / ProgramFiles(x86)，导致 NuGet 机器级配置路径拼出 null 崩溃。
# 用 env -i 构造干净环境即可修复。
set -e
cd "$(dirname "$0")"
env -i \
  PATH="/c/Program Files/dotnet:/c/WINDOWS/System32:/c/WINDOWS" \
  SystemRoot="C:\\WINDOWS" \
  ProgramData="C:\\ProgramData" \
  ProgramFiles="C:\\Program Files" \
  "ProgramFiles(x86)=C:\\Program Files (x86)" \
  APPDATA="C:\\Users\\唐朋成\\AppData\\Roaming" \
  LOCALAPPDATA="C:\\Users\\唐朋成\\AppData\\Local" \
  USERPROFILE="C:\\Users\\唐朋成" \
  TEMP="C:\\Users\\唐朋成\\AppData\\Local\\Temp" \
  TMP="C:\\Users\\唐朋成\\AppData\\Local\\Temp" \
  "/c/Program Files/dotnet/dotnet.exe" build -c Release "$@"
