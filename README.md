# honor-power-refresh

按电源状态自动切换屏幕刷新率的 Windows 小工具。接电源与用电池时的目标 Hz、切换方式、日志开关都由配置决定，改数值无需重新编译。**默认走 Windows 标准 API，一次调用直接设定目标刷新率，不再依赖荣耀私有导出轮询。**

## 工作方式

Windows 计划任务 `\HonorPowerRefreshHook` 订阅 System 日志的 `Microsoft-Windows-Kernel-Power` 事件 ID 105（AC/DC 切换），并在用户登录时补跑一次。每次触发只启动一次 `HonorPowerRefresh.exe`：

1. `GetSystemPowerStatus` 读当前供电状态，确定目标刷新率；
2. `EnumDisplaySettingsEx` 读当前刷新率，已是目标则直接退出（幂等）；
3. 枚举当前分辨率可用档位 → `ChangeDisplaySettingsEx` 先用 `CDS_TEST` 探测，再一次设定 `dmDisplayFrequency` → 读回验证；
4. 标准 API 失败或读回不符、且 `HardwareHal.dll` 存在时，才回退到荣耀 `ChangeFrequency`（按档位轮转，需多次调用）。

程序无常驻进程，不装驱动、不改注册表或服务。系统事件只当作「闹钟」，AC/电池由进程自己读取。

## 配置

| 文件 | 作用 | 读取时机 |
| --- | --- | --- |
| `HonorPowerRefresh.config.ini` | `AcRefreshRate` / `BatteryRefreshRate` / `SwitchMethod` / `EnableLogging` | exe 每次启动 |
| `build.config.ini` | `ExePath` / `TaskName` / `TaskXml`（部署路径与任务名） | 仅 build 时 |

`SwitchMethod=Direct`（默认）：标准 API 一次到位；`SwitchMethod=HonorHal`：强制走荣耀私有导出。

## 构建与安装

在目录内双击 `build.bat`，或执行 `./build.ps1`：编译 `HonorPowerRefresh.cs` → 读取 `HonorPowerRefreshHook.xml` 模板，自动填充 `<Command>`、`<URI>`、当前用户 SID 与用户名 → `Register-ScheduledTask -Xml -Force` 覆盖导入任务。首次安装可用 `build.bat -RunTaskNow` 立即跑一次。

目录可整体迁移：搬走后在新目录重跑一次 build，任务的绝对路径会自动更新。完整说明见 [HonorPowerRefresh-说明.md](./HonorPowerRefresh-说明.md)。

## 注意

- 默认路径只依赖 Windows 标准显示 API，荣耀 PC Manager / OSD 更新不再影响钩子。`HardwareHal.dll!ChangeFrequency` 仅作可选回退，缺失也不影响默认工作；其行为说明见说明文档。
- 任务模板 `HonorPowerRefreshHook.xml` 已脱敏，身份与路径字段是占位符，**必须**由 build 填充；不要拿去任务计划程序手动导入。
- 编译产物 `HonorPowerRefresh.exe`、生成配置 `build.generated.ps1` 与运行日志不入库，均由 build 重建。
