# PowerShell 短期任务示例脚本
# 说明: 这是一个 PowerShell 版本的一次性任务脚本
# 用途: 演示如何在 DaemonKit 中运行 PowerShell 脚本
# 特点: 支持 NoDaemon=true 标记以禁用自动重启

Write-Host ""
Write-Host "================================"
Write-Host "PowerShell 短期任务示例 - 开始执行"
Write-Host "================================"
Write-Host "执行时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host ""

# 示例1: 获取系统信息
Write-Host "[1/3] 收集系统信息..."
$computerInfo = Get-ComputerInfo -Property WindowsVersion, OsVersion -ErrorAction SilentlyContinue
Write-Host "     Windows 版本: $($computerInfo.WindowsVersion)"
Write-Host ""

# 示例2: 列出进程
Write-Host "[2/3] 列出前5个进程..."
Get-Process | Sort-Object WorkingSet -Descending | Select-Object -First 5 | Format-Table Name, WorkingSet -AutoSize
Write-Host ""

# 示例3: 写入日志
Write-Host "[3/3] 记录执行日志..."
$logPath = "$env:TEMP\DaemonKit_PowerShellTask.log"
"$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - PowerShell 短期任务已执行" | Add-Content -Path $logPath
Write-Host "     日志已记录到 $logPath"
Write-Host ""

Write-Host "================================"
Write-Host "PowerShell 短期任务示例 - 执行完成"
Write-Host "================================"
Write-Host ""

# 任务完成
exit 0
