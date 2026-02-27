@echo off
REM ================================================================
REM 测试脚本: 异常退出批处理 (守护测试)
REM 用途: 验证 DaemonKit 对脚本异常退出的守护行为
REM 配置: IsScript="true"
REM 预期: 脚本以非零退出码退出后, DaemonKit 应自动重启
REM ================================================================

chcp 65001 >nul
setlocal enabledelayedexpansion

set "LOGFILE=%TEMP%\DaemonKit_test_crash.log"

echo [%date% %time%] ===== 异常退出脚本启动 ===== >> "!LOGFILE!"

echo.
echo [测试] 异常退出脚本 - 开始
echo [测试] 此脚本将在 5 秒后以 exit code 1 退出
echo [测试] 若 DaemonKit 守护正常, 将检测到退出并自动重启
echo.

echo [1/2] 模拟正常工作...
timeout /t 3 /nobreak >nul

echo [2/2] 模拟错误发生...
timeout /t 2 /nobreak >nul

echo.
echo [测试] 异常退出脚本 - 以错误码 1 退出
echo [%date% %time%] 脚本异常退出 (exit code 1) >> "!LOGFILE!"

exit /b 1
