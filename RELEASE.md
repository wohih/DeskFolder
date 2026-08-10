# 发布 Release 指南（DeskFolder）

本文档说明如何为 DeskFolder 打标签并发布 GitHub Release，附带可下载的二进制文件。

---

## 0. 版本号

当前版本号在 `DeskFolder.csproj` 的 `<Version>` 节点（当前为 `1.0.0`）。
每次发版前，如需升版本，先改这里，再提交：

```xml
<PropertyGroup>
  <Version>1.0.1</Version>
</PropertyGroup>
```

---

## 1. 发布前检查清单

- [ ] 代码已合并并 `git push` 到 `main`
- [ ] 本地已 `git pull` 确保与远程一致
- [ ] 功能自测通过（展开/折叠、拖动、主题、托盘菜单）
- [ ] 如需新版本号，已更新 `DeskFolder.csproj` 的 `<Version>` 并提交

---

## 2. 打包二进制产物

进入项目根目录（仓库根 `DeskFolder/`）：

```bash
cd D:\Windows美化\DeskFolder
```

### 方式 A：Framework-dependent（推荐，体积小，需 .NET 8 桌面运行时）

```bash
# 构建
bash build.sh

# 发布到 release/fd-v1.0.0/
env -i PATH="/c/Program Files/dotnet:/c/WINDOWS/System32:/c/WINDOWS" \
  SystemRoot="C:\WINDOWS" ProgramData="C:\ProgramData" \
  "ProgramFiles(x86)=C:\Program Files (x86)" \
  APPDATA="C:\Users\唐朋成\AppData\Roaming" LOCALAPPDATA="C:\Users\唐朋成\AppData\Local" \
  USERPROFILE="C:\Users\唐朋成" TEMP="C:\Users\唐朋成\AppData\Local\Temp" \
  "/c/Program Files/dotnet/dotnet.exe" publish -c Release -o release/fd-v1.0.0
```

> 普通本机（已装好 .NET SDK、环境变量齐全）可简化为一行：
> ```bash
> dotnet publish -c Release -o release/fd-v1.0.0
> ```

发布后 `release/fd-v1.0.0/` 目录核心文件：

| 文件 | 说明 |
|------|------|
| `DeskFolder.exe` | 主程序入口（约 150KB） |
| `DeskFolder.dll` | 程序集 |
| `DeskFolder.runtimeconfig.json` | 运行时配置（声明依赖 .NET 8） |
| `DeskFolder.deps.json` | 依赖清单 |

> 历史迭代构建产物在 `_archive/`（如 `out_rel18` 即 v1.0.0 初版），**仅本地备份、不进仓库、不上传 Release**。
> 使用者需先在 https://dotnet.microsoft.com/download/dotnet/8.0 安装 **.NET 8 桌面运行时**。

### 方式 B：Self-contained（免运行时，体积大，适合发给没装运行时的用户）

```bash
dotnet publish -c Release -r win-x64 \
  -p:SelfContained=true -p:PublishSingleFile=true \
  -o release/fd-v1.0.0-portable
```

生成的 `DeskFolder.exe` 自带运行时，用户**无需安装 .NET** 即可双击运行（体积约 100MB+）。

---

## 3. 创建 GitHub Release

1. 打开 <https://github.com/wohih/DeskFolder/releases/new>
2. **Choose a tag**：填 `v1.0.0`（须与 `<Version>` 一致；如不存在会提示创建）
3. **Release title**：填 `DeskFolder v1.0.0`
4. **Describe**（发布说明）示例：

   ```markdown
   ## DeskFolder v1.0.0

   Windows 桌面快捷方式文件夹工具首个正式版。

   ### 功能
   - 悬停展开 / 平滑动画的文件夹窗口
   - 纯色与图片背景主题（单图/多图、轮播/随机）
   - 托盘右键菜单：全局主题设置 / 重新排列 / 新建空白文件夹 / 开机自启动
   - 拖动换位、拖放添加 .lnk 快捷方式、自定义行列数

   ### 使用
   下载 `DeskFolder-v1.0.0.zip`，解压后双击 `DeskFolder.exe`。
   若提示缺少 .NET 运行时，请安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)。
   ```

5. 把步骤 2 的产物**打包成 zip**（建议命名 `DeskFolder-v1.0.0.zip`），拖到 **"Attach binaries by dropping them here or selecting them"** 区域上传
   - 也可分别上传 `DeskFolder.exe` / `DeskFolder.dll` / `*.json` 等文件
6. 勾选 **Set as the latest release**
7. 点 **Publish release**

---

## 4. 校验

- 从 Release 页面下载附件，解压到干净目录
- 双击 `DeskFolder.exe`，确认能正常启动、托盘图标出现、右键菜单可用
- 首次启动会自动导入桌面 `.lnk` 快捷方式

---

## 5. 快捷参考

| 动作 | 命令 |
|------|------|
| 构建 | `bash build.sh` 或 `dotnet build -c Release` |
| 发布（FD） | `dotnet publish -c Release -o release/fd-vX.Y.Z` |
| 发布（便携） | `dotnet publish -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -o release/fd-vX.Y.Z-portable` |
| 打标签 | `git tag v1.0.0 && git push origin v1.0.0` |

> 标签 `vX.Y.Z` 建议与 `DeskFolder.csproj` 的 `<Version>` 保持一致，便于追溯。
