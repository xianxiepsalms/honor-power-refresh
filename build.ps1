<#
.SYNOPSIS
    读取 build.config.ini，编译 exe，生成路径配置，并按任务 XML 模板导入计划任务。

.DESCRIPTION
    编译参数与最初已验证成功的那一行一致：
        csc.exe /nologo /target:winexe /platform:x64 /optimize+ /out:<exe> <cs>
    /target:winexe 避免事件触发时弹出控制台窗口，/platform:x64 匹配 64 位 DLL。

    本脚本是「配置 -> 产物 -> 生效」的唯一入口：
      1. 读 build.config.ini（ExePath / TaskName / TaskXml）；
      2. 编译 exe 到 ExePath（-TaskOnly 时跳过编译）；
      3. 生成 build.generated.ps1（解析后的绝对路径与任务名），供 Remove 读取；
      4. 读取导出的任务 XML 模板，自动填充机器相关字段后导入计划任务。

    第 4 步自动覆盖的四处（模板里其它内容一律原样保留，手改后重新导入即生效）：
      <Actions>/<Exec>/<Command>              <- 配置里的 ExePath（唯一与目录有关的字段）
      <RegistrationInfo>/<URI>                <- \TaskName
      <Principals>/<Principal>/<UserId>       <- 当前用户 SID
      <LogonTrigger>/<UserId>                 <- 当前用户名
    模板是任务计划程序导出的 UTF-16 XML；脚本按 BOM 自动识别 UTF-16 / UTF-8。

    导入使用 Register-ScheduledTask -Xml -Force：任务不存在则创建，存在则整体覆盖，
    是单次操作，不需要先删除。-Recreate 才走「先删再导入」（带失败自动回滚）。
    默认每次都按模板覆盖导入，所以改了 XML 里任何设置都会生效；-SkipTaskSync 完全不碰任务。

    「导入成功」不等于「已经跑过一次」：事件触发器要等下一次电源切换或登录才触发，
    需要立即执行一次请加 -RunTaskNow（Start-ScheduledTask）。

    先编译到临时文件，成功后才替换；失败或旧 exe 正被占用时原有 exe 保持不变。

.PARAMETER Config
    人维护的配置文件，默认 <脚本目录>\build.config.ini
.PARAMETER GeneratedConfig
    生成的机器相关配置，默认 <脚本目录>\build.generated.ps1
.PARAMETER Source
    C# 源码路径，默认 <脚本目录>\HonorPowerRefresh.cs
.PARAMETER Output
    产物路径，默认取配置里的 ExePath
.PARAMETER Compiler
    C# 编译器路径，默认 %SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
.PARAMETER TaskXml
    任务定义 XML 模板，默认取配置里的 TaskXml（auto = <脚本目录>\HonorPowerRefreshHook.xml）
.PARAMETER KeepBackup
    替换前把旧 exe 另存为 <Output>.bak
.PARAMETER SkipTaskSync
    只编译并生成配置，完全不碰计划任务
.PARAMETER RunTaskNow
    导入完成后立即启动一次任务（首次安装用）
.PARAMETER Recreate
    导入前先删除同名任务；失败时用备份的旧定义恢复。默认关闭
.PARAMETER TaskOnly
    不编译、不替换 exe，只按模板导入计划任务（改完 XML 后单独导入用）

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -TaskOnly
.EXAMPLE
    .\build.ps1 -SkipTaskSync -Output "$env:TEMP\HonorPowerRefresh-test.exe"
#>
[CmdletBinding()]
param(
    [string] $Config,
    [string] $GeneratedConfig,
    [string] $Source,
    [string] $Output,
    [string] $Compiler,
    [string] $TaskXml,
    [switch] $KeepBackup,
    [switch] $SkipTaskSync,
    [switch] $RunTaskNow,
    [switch] $Recreate,
    [switch] $TaskOnly
)

$ErrorActionPreference = 'Stop'

if (-not $Config)          { $Config          = Join-Path $PSScriptRoot 'build.config.ini' }
if (-not $GeneratedConfig) { $GeneratedConfig = Join-Path $PSScriptRoot 'build.generated.ps1' }
if (-not $Source)          { $Source          = Join-Path $PSScriptRoot 'HonorPowerRefresh.cs' }
if (-not $Compiler)        { $Compiler        = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe' }

$Config          = [IO.Path]::GetFullPath($Config)
$GeneratedConfig = [IO.Path]::GetFullPath($GeneratedConfig)
$Source          = [IO.Path]::GetFullPath($Source)
$Compiler        = [IO.Path]::GetFullPath($Compiler)

function Resolve-ConfiguredPath([string] $Value, [string] $BaseDir, [string] $Fallback) {
    if (-not $Value -or $Value -ieq 'auto') { return $Fallback }
    if ([IO.Path]::IsPathRooted($Value)) { return [IO.Path]::GetFullPath($Value) }
    return [IO.Path]::GetFullPath((Join-Path $BaseDir $Value))
}

function Get-TaskXmlText([string] $Path) {
    # 任务计划程序导出的是 UTF-16 LE + BOM；也可能是编辑器另存的 UTF-8。
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        return [Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
    }
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        return [Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3)
    }
    return [Text.Encoding]::UTF8.GetString($bytes)
}

if (-not (Test-Path -LiteralPath $Config -PathType Leaf)) { throw "找不到部署配置: $Config" }

$cfg = @{}
foreach ($line in [IO.File]::ReadAllLines($Config, [Text.Encoding]::UTF8)) {
    $text = $line.Trim()
    if (-not $text -or $text.StartsWith(';') -or $text.StartsWith('#')) { continue }
    $eq = $text.IndexOf('=')
    if ($eq -lt 1) { continue }
    $cfg[$text.Substring(0, $eq).Trim()] = $text.Substring($eq + 1).Trim()
}

$taskName = $cfg['TaskName']
if (-not $taskName) { $taskName = 'HonorPowerRefreshHook' }

if (-not $Output) {
    $Output = Resolve-ConfiguredPath $cfg['ExePath'] $PSScriptRoot (Join-Path $PSScriptRoot 'HonorPowerRefresh.exe')
}
$Output = [IO.Path]::GetFullPath($Output)

if (-not $TaskXml) {
    $TaskXml = Resolve-ConfiguredPath $cfg['TaskXml'] $PSScriptRoot (Join-Path $PSScriptRoot 'HonorPowerRefreshHook.xml')
}
$TaskXml = [IO.Path]::GetFullPath($TaskXml)

$outDir = Split-Path -Path $Output -Parent
if (-not (Test-Path -LiteralPath $outDir -PathType Container)) { throw "输出目录不存在: $outDir" }
$genDir = Split-Path -Path $GeneratedConfig -Parent
if (-not (Test-Path -LiteralPath $genDir -PathType Container)) { throw "生成配置目录不存在: $genDir" }

Write-Output "部署配置 : $Config"
Write-Output "ExePath  : $Output"
Write-Output "TaskName : $taskName"
Write-Output "TaskXml  : $TaskXml"

if (-not $TaskOnly) {
    if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw "找不到 C# 编译器: $Compiler" }
    if (-not (Test-Path -LiteralPath $Source   -PathType Leaf)) { throw "找不到 C# 源码: $Source" }

    $buildArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+')
    $tempOut = Join-Path $outDir ('.' + [IO.Path]::GetFileName($Output) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')

    Write-Output "编译器   : $Compiler"
    Write-Output "源码     : $Source"
    Write-Output "参数     : $($buildArgs -join ' ')"
    Write-Output "临时产物 : $tempOut"

    try {
        & $Compiler @buildArgs "/out:$tempOut" $Source
        $code = $LASTEXITCODE
        if ($code -ne 0) { throw "C# 编译失败，退出码 $code" }
        if (-not (Test-Path -LiteralPath $tempOut -PathType Leaf)) { throw '编译器返回 0，但未生成产物' }
        if ((Get-Item -LiteralPath $tempOut).Length -eq 0) { throw '生成的产物为空文件' }

        if ($KeepBackup -and (Test-Path -LiteralPath $Output -PathType Leaf)) {
            Copy-Item -LiteralPath $Output -Destination "$Output.bak" -Force
            Write-Output "已备份旧 exe: $Output.bak"
        }

        # 若旧 exe 正在运行（任务恰好触发），Move-Item 会失败，此时原 exe 仍保持原样
        Move-Item -LiteralPath $tempOut -Destination $Output -Force
        Write-Output "已写入   : $Output"
    }
    catch {
        throw "编译或替换失败（原有 exe 未被改动）：$($_.Exception.Message)"
    }
    finally {
        if (Test-Path -LiteralPath $tempOut -PathType Leaf) {
            Remove-Item -LiteralPath $tempOut -Force -ErrorAction SilentlyContinue
        }
    }

    $item = Get-Item -LiteralPath $Output
    Write-Output ("完成: {0} ({1} bytes, {2})" -f $item.FullName, $item.Length, $item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
} else {
    Write-Output '已跳过编译（-TaskOnly）。'
    if (-not (Test-Path -LiteralPath $Output -PathType Leaf)) {
        Write-Warning "配置的 exe 尚不存在，任务会指向该路径: $Output"
    }
}

# 生成机器相关配置。必须写 UTF-8 BOM：PowerShell 5.1 会以 ANSI 读取无 BOM 的 .ps1，
# 而路径可能含中文，乱码会导致 dot-source 解析失败。
$escapedExe  = $Output.Replace("'", "''")
$escapedTask = $taskName.Replace("'", "''")
$generatedText = @"
# Generated by build.ps1 - do not edit. Do not commit to version control.
# GeneratedAt: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
`$ExePath = '$escapedExe'
`$TaskName = '$escapedTask'
"@
[IO.File]::WriteAllText($GeneratedConfig, $generatedText, (New-Object System.Text.UTF8Encoding($true)))
Write-Output "已生成配置: $GeneratedConfig"

if (-not $SkipTaskSync) {
    if (-not (Test-Path -LiteralPath $TaskXml -PathType Leaf)) { throw "找不到任务 XML 模板: $TaskXml" }

    $templateText = Get-TaskXmlText $TaskXml
    try { [xml] $doc = $templateText } catch { throw "任务 XML 模板无法解析: $($_.Exception.Message)" }

    $cmdNode = $doc.SelectSingleNode("//*[local-name()='Command']")
    if (-not $cmdNode) { throw '任务 XML 模板中找不到 <Command> 节点' }
    # 模板里的路径只是导出时的快照，属于被覆盖的占位值；真正的配置是 ExePath。
    $templateCommand = $cmdNode.InnerText
    $cmdNode.InnerText = $Output

    $uriNode = $doc.SelectSingleNode("//*[local-name()='RegistrationInfo']/*[local-name()='URI']")
    if ($uriNode) { $uriNode.InnerText = '\' + $taskName }

    $currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $sidNode = $doc.SelectSingleNode("//*[local-name()='Principals']/*[local-name()='Principal']/*[local-name()='UserId']")
    if ($sidNode) { $sidNode.InnerText = $currentIdentity.User.Value }

    $logonNodes = $doc.SelectNodes("//*[local-name()='LogonTrigger']/*[local-name()='UserId']")
    foreach ($n in $logonNodes) { $n.InnerText = $currentIdentity.Name }

    $targetXml = $doc.OuterXml
    Write-Output "任务模板 : $TaskXml"
    Write-Output "模板路径 : $templateCommand (已脱敏占位符，导入时被覆盖)"
    Write-Output "导入路径 : $Output"
    Write-Output '自动填充 : Command / URI / Principal.UserId / LogonTrigger.UserId'

    $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($Recreate -and $existing) {
        $backupXml = Export-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        try {
            Register-ScheduledTask -TaskName $taskName -Xml $targetXml -Force -ErrorAction Stop | Out-Null
        }
        catch {
            Register-ScheduledTask -TaskName $taskName -Xml $backupXml -Force -ErrorAction SilentlyContinue | Out-Null
            throw "先删后导入失败，已用备份定义恢复原任务：$($_.Exception.Message)"
        }
        Write-Output "已删除并按模板重新导入: $taskName"
    }
    else {
        # -Force 覆盖是单次操作：系统先校验 XML，失败时已有任务保持原样，无需先删。
        Register-ScheduledTask -TaskName $taskName -Xml $targetXml -Force -ErrorAction Stop | Out-Null
        $verb = if ($existing) { '更新' } else { '创建' }
        Write-Output "${verb}任务（按模板覆盖导入）: $taskName -> $Output"
    }

    if ($RunTaskNow) {
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        Write-Output "已立即运行一次: $taskName"
    }
} else {
    Write-Output '已跳过计划任务同步（-SkipTaskSync）。'
}

Write-Output '提示: 目录搬走后在新的位置重跑 ./build.ps1，任务会自动指向新路径；只改 XML 时用 ./build.ps1 -TaskOnly 导入。'
