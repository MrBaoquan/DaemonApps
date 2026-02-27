@echo off
REM ================================================================
REM 测试脚本: 短生命周期批处理 (守护测试)
REM 用途: 验证 DaemonKit 对快速退出脚本的守护行为
REM 配置: NoDaemon="false" IsScript="true"
REM 预期: 脚本退出后, DaemonKit 应自动重启该脚本
REM ================================================================

chcp 65001 >nul
setlocal enabledelayedexpansion

set "LOGFILE=%TEMP%\DaemonKit_test_short.log"

echo [%date% %time%] ===== 短生命周期脚本启动 ===== >> "!LOGFILE!"
echo [%date% %time%] PID: 未知 (批处理无法获取自身PID) >> "!LOGFILE!"

echo.
echo [测试] 短生命周期脚本 - 开始
echo [测试] 此脚本将在 3 秒后退出
echo [测试] 若 DaemonKit 守护正常, 将自动重启此脚本
echo.

REM 模拟短任务
echo [1/3] 执行步骤1...
timeout /t 1 /nobreak >nul

echo [2/3] 执行步骤2...
timeout /t 1 /nobreak >nul

echo [3/3] 执行步骤3...
timeout /t 1 /nobreak >nul

echo.
echo [测试] 短生命周期脚本 - 正常退出 (exit code 0)
echo [%date% %time%] 脚本正常退出 >> "!LOGFILE!"

exit /b 0
