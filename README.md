# SinUpdateHub

> 一款深色风格的 Windows 桌面工具，集成 **GitHub / WinGet 软件更新管理**、**SinNote Markdown 笔记** 与 **WebDAV 自动定时云备份**。

## 核心功能

### 1. 软件更新管理（GitHub / WinGet / 官网下载）
- **表格式软件清单**：一屏展示项目名称、包匹配规则、当前版本、最新版本、安装目录、来源与状态；支持搜索、状态/来源筛选以及表头三态排序（名称 / 来源 / 状态）。
- **GitHub 来源**：填写仓库地址自动读取最新 Release，支持关键词或正则在 Assets 中精准匹配安装包。
- **WinGet 来源**：内置微软官方 WinGet 本地 SQLite 索引（`source.msix`），按软件名称直接搜索包 ID，不消耗 GitHub API 额度。
- **软件下载（官网/手动来源）**：支持维护无 GitHub/WinGet 源的软件官网下载地址，一键检测链接可达性并快速打开下载页。
- **本机版本自动识别**：结合注册表卸载项、MSIX/Appx 包与绑定 `.exe` 文件版本自动识别当前版本，一键下载并拉起安装器。

### 2. SinNote（OneNote 风格深色 Markdown 笔记）
- **分区 / 页两级结构**：支持自定义分区配色、全文搜索（当前页 / 全部分区）与拖拽排序。
- **离线 Vditor 编辑器**：内置离线 Vditor（即时渲染 / 所见即所得 / 分屏预览），支持 190+ 编程语言代码高亮、Windows 本机系统字体选择与画布缩放。
- **增强表格与行内排版**：支持选中文本高亮、字体颜色、局部改字体、左/中/右对齐、单单元格多行粘贴、表格渐进 `Ctrl+A` 全选以及粘贴截图外置缓存防卡死。
- **笔记回收站**：删除分区或笔记页时先移入回收站保留 30 天，支持一键还原（原分区已删时自动重建）、彻底删除与到期自动清理。

### 3. 备份与恢复（WebDAV + 本地 JSON）
- **WebDAV 自动定时备份**：支持坚果云、群晖等标准 WebDAV 服务，**默认每 24 小时自动后台静默备份**（可自定义 `1~720` 小时或关闭），并自动清理超出保留数量的旧备份。
- **选择性导入与恢复**：从 WebDAV 或本地 JSON 恢复时，支持按软件条目、SinNote 分区/单页自由勾选，并可选择「合并恢复」或「完全覆盖」。
- **敏感信息加密**：WebDAV 密码与 GitHub Token 均通过 Windows DPAPI 加密存储在本地 `%APPDATA%\SinUpdateHub\settings.json`。

## 技术栈

| 模块 | 说明 |
|---|---|
| 界面 | .NET 8 + WPF，自绘深色主题（GitHub Dark + 紫灰强调色） |
| 核心库 | `SinUpdateHub.Core`（来源提供器、版本比对、注册表/MSIX 识别、下载器、WebDAV、JSON 存储） |
| 笔记编辑器 | WebView2 + 离线 Vditor 4 + Highlight.js（190+ 语言） |
| 测试 | `tests/SinUpdateHub.Smoke`（含离线回归测试与模拟 WebDAV 服务测试） |
| 打包 | Win-x64 单文件自包含 `.exe`（无需额外安装 .NET 运行时） |

## 目录结构

```text
SinUpdateHub/
├── SinUpdateHub.sln
├── src/
│   ├── SinUpdateHub.Core/          # 纯业务逻辑与数据模型（无 UI 依赖）
│   └── SinUpdateHub.App/           # WPF 桌面应用（ViewModels / Views / Themes / Assets）
├── tests/
│   └── SinUpdateHub.Smoke/         # 自动化冒烟与回归测试
├── tools/                          # 编译、发布与部署脚本
└── docs/                           # 使用说明与更新日志
```

## 快速构建与测试

```powershell
# 编译解决方案
dotnet build SinUpdateHub.sln

# 运行离线回归测试
dotnet run --project tests\SinUpdateHub.Smoke\SinUpdateHub.Smoke.csproj -- --offline

# 发布单文件 exe（输出至 dist\SinUpdateHub.exe）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1
```

## 数据存放位置（仅本地）

| 内容 | 路径 |
|---|---|
| 软件清单与 SinNote 数据 | `%APPDATA%\SinUpdateHub\data.json` |
| 应用设置（密码/Token DPAPI 加密） | `%APPDATA%\SinUpdateHub\settings.json` |
| 下载缓存 | `%LOCALAPPDATA%\SinUpdateHub\downloads` |
| WinGet 本地索引 | `%LOCALAPPDATA%\SinUpdateHub\winget\index.db` |
| 运行日志 | `%APPDATA%\SinUpdateHub\logs\app.log` |
