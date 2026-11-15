$ErrorActionPreference = 'Stop'

# 安装 = 构建 + 同步计划任务 + 立即运行一次。全部逻辑集中在 build.ps1，
# 本文件只保留一个兼容旧习惯的入口。
& (Join-Path $PSScriptRoot 'build.ps1') -RunTaskNow
