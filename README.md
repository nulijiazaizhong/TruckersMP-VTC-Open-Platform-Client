# VTC 客户端（TruckersMP VTC 开放平台 · Avalonia 桌面端）

多车队共享的开源客户端。所有车队**共用同一份代码**，仅 `.env` 不同（品牌、指向的后端、强调色等）。
技术栈：**Avalonia 11 / C# / .NET 10 / MVVM（CommunityToolkit.Mvvm）/ DI（Microsoft.Extensions.DependencyInjection + DryIoc）**。

## 项目结构

```
VTC.Client/                  # 客户端（Avalonia 应用）
├─ src/VTC.Shared/          # 共享库（DTO / 常量 / API 契约），客户端与服务器共用
├─ App.axaml / Program.cs   # 应用入口 + 全局主题与样式
├─ Views/                   # 各页面：登录 / 主窗口 / 首页 / 资讯 / 管理 / 设置 / 个人资料
├─ ViewModels/              # 登录 / 首页 / 主框架 ViewModel
├─ Services/                # API、认证、品牌、存档、DLC、推送、更新等
├─ Models/                  # 客户端本地模型
├─ app-assets/              # 应用图标与品牌素材
└─ .env.example             # 配置模板（复制为 .env 使用）
```

> `VTC.Client.csproj` 通过 `ProjectReference` 引用 `src/VTC.Shared`，无需额外操作。

## 构建与运行

```powershell
# 1. 复制配置（按需修改品牌与后端地址）
copy .env.example .env

# 2. 构建
dotnet build VTC.Client.csproj -c Release

# 3. 运行
dotnet run --project VTC.Client.csproj
```

### 开发模式

`.env` 中 `VTC_DEV_MODE=true`（或命令行 `--dev`）：`VTC_API_BASE` 默认指向本地 docker 服务端 `http://127.0.0.1:8731`，窗口标题带 `[DEV · 地址]` 标识，避免开发/生产混淆。

```powershell
# 指向本地 docker 服务端
dotnet run --project VTC.Client.csproj -- --dev
# 自定义地址
dotnet run --project VTC.Client.csproj -- --dev --api-base=http://127.0.0.1:9999
```

## 配置（.env）

| 变量 | 说明 |
| --- | --- |
| `VTC_API_BASE` | 后端 API 地址（生产填真实后端） |
| `VTC_DEV_MODE` | `true` = 开发模式，默认指向本地 docker 服务端 |
| `TEAM_NAME` / `TEAM_SLOGAN` / `TEAM_LOGO` | 车队品牌（可被后端 `/api/v1/config/public` 覆盖） |
| `LOGIN_BG` / `LOGIN_LAYOUT` | 登录背景图与版式（left / right / center） |
| `ACCENT_COLOR` | 强调色 `#RRGGBB`，注入主题令牌 |
| `VTC_APP_ICON` | 运行时窗口/任务栏图标（支持本地文件 / pack:// / http(s)） |
| `VTC_CDN_DOMAINS` | 远程品牌图 CDN 域名白名单（逗号分隔） |
| `AUTH_MODE` | `password` / `steam` / `hybrid` |
| `SAVE_DIR` / `GAME_DIR` | 游戏存档目录 / 游戏安装目录 |

> **安全**：`.env` 只放"连接与展示"类配置，**严禁**任何后端 secret（DB / S3 / JWT / token）。`.env` 已在 `.gitignore` 中，不要提交。

## 许可

内部开源项目，详见仓库说明。
