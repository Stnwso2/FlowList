# Focus List / 焦点清单

一个本地优先的 Windows 悬浮任务清单和 Codex 个人插件。

## 初版范围

- 今天、本周、以后、完成、历史五个视图；历史按日期保留已完成和未完成的旧任务
- 漏过日期但仍未完成的任务会继续显示在“今天”，并标记为逾期
- 新增、编辑、删除、勾选任务
- 截止日期、优先级和备注
- 本地 JSON 持久化，无账号、无云服务
- 窗口拖动、缩放、收拢、置顶/普通层级切换和位置记忆
- Codex 工具：打开悬浮窗、列出、新增、更新和删除任务

任务数据默认保存在 `%LOCALAPPDATA%\FocusList\tasks.json`。

## 参考案例

- [AtEase00/backlog](https://github.com/AtEase00/backlog)：本地优先、常驻侧边栏、未完成/已完成分区和托盘交互。
- [tanxestudio/Windows-desktop-Tasks-Widget](https://github.com/tanxestudio/Windows-desktop-Tasks-Widget)：Windows 小部件、层级切换、优先级和截止日期。
- [super-productivity/super-productivity](https://github.com/super-productivity/super-productivity)：成熟任务模型、计划与完成状态组织。

参考项目仅用于产品模式和架构研究，本实现为独立代码。

## 开发验证

```powershell
npm install
npm test
dotnet run --project .\tools\GenerateFocusListIcon\GenerateFocusListIcon.csproj -- .\assets\focus-list.ico
dotnet build .\desktop\FocusListFloat.csproj -c Release /warnaserror
dotnet publish .\desktop\FocusListFloat.csproj -c Release -r win-x64 -o .\assets\desktop /p:PublishSingleFile=true /p:SelfContained=false /warnaserror
```
