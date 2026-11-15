$ErrorActionPreference = 'Stop'

# 任务名优先取 build.ps1 生成的配置；没生成过时回退到默认名，保证仍可卸载。
$taskName = 'HonorPowerRefreshHook'
$generated = Join-Path $PSScriptRoot 'build.generated.ps1'
if (Test-Path -LiteralPath $generated -PathType Leaf) {
    . $generated
    if ($TaskName) { $taskName = $TaskName }
}
Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Write-Output "Removed $taskName. Files in this folder were not deleted."
