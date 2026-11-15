# 荣耀电源刷新率钩子

项目/工具名：`honor-power-refresh`（即本文件夹名）。采用小写 kebab-case、无空格，与 `HonorPowerRefresh.exe` 和计划任务 `HonorPowerRefreshHook` 同源，便于命令行引用和纳入版本库。目录改名后，在新目录重跑一次 `./build.ps1`（或双击 `build.bat`）即可让计划任务指向新路径；工程内所有路径都相对自身定位，无需手改 XML。

适用本机 HONOR FMB-P。目标刷新率由 `HonorPowerRefresh.config.ini` 配置，默认接通电源 120 Hz、使用电池 60 Hz。程序在 Windows 记录 AC/DC 电源切换事件（或用户登录）时被唤醒，读当前供电与刷新率；已是目标则直接退出，否则**默认用 Windows 标准 `ChangeDisplaySettingsEx` 一次设定目标 Hz**（先用 `CDS_TEST` 探测驱动是否接受，再应用，最后读回验证）。只有标准 API 失败或读回不符、且 `HardwareHal.dll` 存在时，才回退调用荣耀 OSD 的 `ChangeFrequency`（与 Fn+R 同一入口，按档位轮转）。

这次改动来自对 `ChangeFrequency` 的接口行为确认：它不接受目标 Hz，两个 `ref` 参数只是回传切换前后 Hz，每次调用只把刷新率切到机型档位表里的下一档，所以旧实现必须循环调用 + 固定等待 + 反复读回。详见文末「ChangeFrequency 接口行为说明」。

构建与安装：有两类配置，别混在一起——`build.config.ini` 是**部署配置**（exe 路径、任务名、任务模板，只在 build 时读），`HonorPowerRefresh.config.ini` 是**运行时配置**（目标刷新率、日志开关，exe 每次启动都读）。`HonorPowerRefreshHook.xml` 是计划任务定义模板（任务计划程序导出后**已脱敏、已转为 UTF-8** 的 XML，见下节）。执行 `./build.ps1` 完成全部：读取部署配置 → 编译源码到 `ExePath` → 生成 `build.generated.ps1` → 读取任务 XML 模板，自动填充 `<Command>`（ExePath）、`<URI>`、`Principal.UserId`（当前用户 SID）、`LogonTrigger.UserId`（当前用户名），再用 `Register-ScheduledTask -Xml -Force` 覆盖导入。任务不存在则创建，存在则整体覆盖；系统先校验 XML，导入失败时旧任务保持原样，所以**不需要先删除**（`-Recreate` 才走「先删再导入」，并带失败回滚）。默认每次都按模板重新导入，改了 XML 里任何设置都会生效。

**模板已脱敏（可安全提交/分享）**：原导出 XML 里的真实登录名 `域名\用户名`、账户 SID、以及旧工程绝对路径都已替换为 `REPLACE_ME_USER_NAME` / `REPLACE_ME_USER_SID` / `REPLACE_ME_EXE_PATH` 三个占位符；文件内已无中文姓名、域账号、本机 SID 或个人目录路径。这三处连同 `<URI>` 恰好就是 build 每次导入时无条件重写的四处，所以**占位符不影响功能**，换电脑、换用户、换目录都能用。反过来，**绝对不要拿这份模板去任务计划程序里手动「导入任务」**：那会绕过自动填充，把 `REPLACE_ME_EXE_PATH` 当成真实 exe 路径写进生效态，任务必然启动失败。要生效一律走 `./build.ps1`（或 `./build.bat`）。

**不想敲命令就双击 `build.bat`**：它用 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1` 调用同目录的脚本，并先 `cd /d` 到脚本所在目录（因此从任何位置双击、或以管理员身份运行都不会因工作目录不同而跑偏）。所有参数原样透传，例如 `build.bat -TaskOnly`、`build.bat -RunTaskNow`、`build.bat -SkipTaskSync`。双击（无参数）跑完会暂停显示结果，带参数跑则不暂停，便于脚本调用；退出码沿用 build.ps1 的退出码。若导入任务提示「拒绝访问」，右键以管理员身份运行。

`./Install-HonorPowerRefresh.ps1` 是 `build.ps1 -RunTaskNow` 的入口，等价于 `build.bat -RunTaskNow`，首次安装用它会立即跑一次。注意「导入成功」不等于「已执行」：事件触发器要等下一次电源切换或登录才触发。以后仅在系统记录电源切换事件（Kernel-Power 105）或当前用户登录时启动程序，完成检查和切换后自动退出，平时没有钩子常驻进程。

任务名是 `\HonorPowerRefreshHook`（任务计划程序库根目录）。用 `taskschd.msc` 打开控制台查看，`schtasks /query /tn "\HonorPowerRefreshHook" /xml` 可导出当前定义。

**XML 里的路径要不要跟着项目地址改？不用。** 准确说法是：与目录绑定的字段只有一个——任务定义 `<Actions><Exec><Command>`，它的值是 **exe 的完整文件路径**（不是项目目录；`ExePath` 允许把 exe 放在与源码不同的位置）。同一个字段有三份拷贝，只有一份算数：

| 存在位置 | 角色 | 系统是否读取 |
|---|---|---|
| `build.config.ini` 的 `ExePath` | **唯一权威配置** | 否，仅 build 读 |
| `HonorPowerRefreshHook.xml` 的 `<Command>` | 已脱敏的 `REPLACE_ME_EXE_PATH` 占位符；是导入的骨架，不是配置 | **否** |
| 导入后 `%SystemRoot%\System32\Tasks` 里的任务定义 | **生效态** | 是 |

所以迁移时人只改配置（或干脆让 `ExePath=auto` 自己跟着目录走），build 每次导入都用 `ExePath` 无条件重写该字段；模板里留着占位符完全不影响结果。运行日志里会同时打印「模板路径」（会显示 `REPLACE_ME_EXE_PATH`）和「导入路径」，可直接看到这次覆盖。也正因如此，**不要把工程里这份 XML 直接拿去任务计划程序里手动导入**——那会绕过自动填充，把占位符写进生效态。

只编译、不碰任务：`./build.ps1 -SkipTaskSync`。只改了 XML、不想重新编译：`./build.ps1 -TaskOnly`（只导入任务）。可用 `-Output` 覆盖产物路径、`-Config` 用另一份配置、`-TaskXml` 指定另一份模板、`-GeneratedConfig` 改生成配置位置、`-KeepBackup` 备份旧 exe。先编译到临时文件，成功后才替换，失败时原有 exe 保持不变。

可迁移：整个目录搬走后保持 `ExePath=auto` 与 `TaskXml=auto`，在**新目录**跑一次 `./build.ps1`，任务的绝对路径会自动改为新位置；若要把 exe 部署到与源码不同的目录，在 `build.config.ini` 里写绝对路径即可。**唯一对目录敏感的是系统里持久化的任务定义（`<Exec><Command>` 里的绝对路径）**，工程文件本身都用相对自身目录定位。模板里的 URI、SID、用户名、exe 路径都已是占位符，导入时自动按当前用户与当前目录覆盖，换电脑/换用户也能用。`build.generated.ps1` 是含本机路径的生成物，不要提交到版本库；`HonorPowerRefreshHook.xml` 已转为 UTF-8（无 BOM），版本库里可直接 diff；脚本按 BOM 自动识别编码，仍兼容任务计划程序 GUI 导出的 UTF-16 模板。

运行时配置 `HonorPowerRefresh.config.ini`（必须和 `HonorPowerRefresh.exe` 同目录）：

| 键 | 默认 | 说明 |
|---|---|---|
| `AcRefreshRate` | `120` | 接通电源时的目标刷新率，单位 Hz |
| `BatteryRefreshRate` | `60` | 使用电池时的目标刷新率，单位 Hz |
| `SwitchMethod` | `Direct` | `Direct` = 标准 API 一次设定（默认，无轮询）；`HonorHal` = 强制走荣耀私有导出 |
| `EnableLogging` | `false` | 改成 `true` 开启日志，切换记录追加到同目录 `HonorPowerRefresh.log` |

目标刷新率以前写死在源码 `Sync()` 里，现在改为配置化。**改这四个值不需要重新编译、也不需要重跑 build**：exe 每次启动都读一遍配置，改完下次电源切换或登录触发即生效。`SwitchMethod` 未知值会回退 `Direct` 并在开启日志时记一条警告。注释用 `;` 或 `#`，未知键忽略；值非数字或超出 `1..1000` 时该项回退到内置默认值，并在开启日志时记一条警告。目标 Hz 若不在当前分辨率实际支持的档位里，程序会跳过本次切换（已有逻辑，不会硬设一个不支持的值）。

开启日志后每条记录会带一行 `Config: AC=... Hz, battery=... Hz`，可直接核对配置有没有被读到；关闭日志时不再追加，旧日志文件不会自动删除。验证方法：把 `EnableLogging=true`，改一下目标值，触发一次电源切换或执行 `Start-ScheduledTask -TaskName HonorPowerRefreshHook`，再看 `.log`。

卸载：执行 `./Remove-HonorPowerRefresh.ps1`。

三层归属：浏览器打开 `HonorPowerRefresh-layers.html`（单文件、离线可用），区分 Windows 系统事件、本工程产出的文件与荣耀私有组件，并附边界对照表。

仅处理主显示器。若当前分辨率下没有目标刷新率，程序会停止本次切换；开启日志后会记录原因。默认路径不再依赖荣耀私有导出，管家更新改掉 `ChangeFrequency` 也不会让钩子失效；只有显式设 `SwitchMethod=HonorHal` 或 Direct 失败回退时才会用到它。

## ChangeFrequency 接口行为说明

目标为荣耀 PC Manager 自带的 `C:\Program Files\HONOR\OSD\HardwareHal.dll`，`ChangeFrequency` 是其中序号 2 的命名导出。接口行为观察结果如下：

**观察（接口行为，尚未做运行时实测）**

| 问题 | 结论 |
|---|---|
| 两个 `ref` 参数是入参还是回传？ | **纯输出**。`param_1` 写回切换后 Hz（after），`param_2` 写回切换前 Hz（before）；DLL 从不读取调用者传入的值。 |
| 能否一次直接设目标 Hz？ | **不能**。函数没有目标 Hz 入参；它按产品型号（`GLO-G` / `FRB-X` / `BRB-X` / `DRB-P` / `XWC-P` / `NWB-G` 等）硬编码档位表（如 `[60,120]`、`[48,60,90]`、`[60,120,165]`、`[60,180]`），每次调用切到「比当前高的下一档」，到顶回绕到最低档。 |
| 返回值 | `1` = 读到当前 Hz、切换调用成功、且读回非零；`0` = 读当前 Hz 失败 / 机型不在支持表 / 切换失败 / 读回为 0。 |
| 底层用什么切刷新率？ | **标准 Win32 API**：用 `EnumDisplaySettingsExW(NULL,-1,...,2)` 读 `dmDisplayFrequency`；用 `ChangeDisplaySettingsW(&DEVMODE, 1)` 设 `dmFields=DM_DISPLAYFREQUENCY` + `dmDisplayFrequency`，一次到位。此外还会用到 `SetDisplayConfig` / `QueryDisplayConfig`（其它路径使用）。 |
| 是否依赖 OSD 守护进程 / IPC？ | 该函数路径上未见 IPC 调用；`IPCMessage.dll` 的导入服务于其它 HAL 导出。 |
| 副作用 | 机型识别（`GetProductName`）、读支持档位、一次显示模式变更（HAL 传 `flags=1`，即 `CDS_UPDATEREGISTRY`，会写显示配置）；未见注册表服务项或网络访问。 |

**推断**：工具此前必须「循环调用 + `Sleep(350)` + 读回」，正是因为导出只能轮转一档；档位表随机型变化，外部无法预知需要几次。

**本机只读验证**：当前 3120×2080 / 120 Hz，可用档位 `[60, 120]`；对 60、120 Hz 分别做 `CDS_TEST` 均返回 `DISP_CHANGE_SUCCESSFUL(0)`，说明标准 API 一次设定可行，因此默认路径改为 Direct。该探针只测试、不应用模式变更。

**未知**：未在本会话实测 Direct 的端到端效果（需用户触发一次电源切换或任务）；其它机型档位表与 `SetDisplayConfig` 路径未逐一验证；特殊机型的 59 Hz 分支未深入分析。

**结论落地**：`SwitchMethod=Direct` 为默认 —— 一次调用、无轮询、无多次闪烁、不依赖管家更新。`HonorHal` 保留为回退，且把固定 350ms 等待换成有界读回（最多 6×100ms）。
