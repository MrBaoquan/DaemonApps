# ================================================================
# 测试脚本: PowerShell 短生命周期 (守护测试)
# 用途: 验证 DaemonKit 对快速退出的 .ps1 脚本的守护行为
# 配置: IsScript="true"
# 预期: 脚本退出后, DaemonKit 应自动重启该脚本
# ================================================================

$LogFile = "$env:TEMP\DaemonKit_test_ps1_short.log"

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logLine = "[$timestamp] $Message"
    Write-Host $logLine
    Add-Content -Path $LogFile -Value $logLine -Encoding UTF8
}

Write-Log "===== PowerShell 短生命周期脚本启动 ====="
Write-Log "PID: $PID"

Write-Host ""
Write-Host "[测试] PowerShell 短生命周期脚本 - 开始"
Write-Host "[测试] 此脚本将在 3 秒后退出"
Write-Host ""

Write-Host "[1/3] 执行步骤1..."
Start-Sleep -Seconds 1

Write-Host "[2/3] 执行步骤2..."
Start-Sleep -Seconds 1

Write-Host "[3/3] 执行步骤3..."
Start-Sleep -Seconds 1

Write-Log "脚本正常退出"
Write-Host "[测试] PowerShell 短生命周期脚本 - 正常退出 (exit code 0)"

exit 0
