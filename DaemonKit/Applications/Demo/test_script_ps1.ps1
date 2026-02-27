# ================================================================
# 测试脚本: PowerShell 长期服务 (守护测试)
# 用途: 验证 DaemonKit 对 .ps1 脚本的启动和守护行为
# 配置: IsScript="true"
# 预期: 脚本持续运行; 若被终止, DaemonKit 应自动重启
# ================================================================

$LogFile = "$env:TEMP\DaemonKit_test_ps1.log"

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logLine = "[$timestamp] $Message"
    Write-Host $logLine
    Add-Content -Path $LogFile -Value $logLine -Encoding UTF8
}

Write-Log "===== PowerShell 测试脚本启动 ====="
Write-Log "PID: $PID"
Write-Log "PowerShell 版本: $($PSVersionTable.PSVersion)"

Write-Host ""
Write-Host "[测试] PowerShell 长期服务脚本 - 启动"
Write-Host "[测试] 此脚本将每隔 5 秒输出一次心跳"
Write-Host "[测试] 使用任务管理器手动终止 powershell.exe 来测试守护重启"
Write-Host ""

$count = 0
while ($true) {
    $count++
    Write-Log "心跳 $count (PID: $PID)"
    Start-Sleep -Seconds 5
}
