@echo off
REM ================================================================
REM 测试脚本: 长生命周期批处理 (守护测试)
REM 用途: 验证 DaemonKit 对持续运行脚本的守护行为
REM 配置: IsScript="true"
REM 预期: 脚本持续运行; 若被外部终止, DaemonKit 应自动重启
REM ================================================================

chcp 65001 >nul
setlocal enabledelayedexpansion

set "LOGFILE=%TEMP%\DaemonKit_test_long.log"
set "COUNT=0"

echo [%date% %time%] ===== 长生命周期脚本启动 ===== >> "!LOGFILE!"

echo.
echo [测试] 长生命周期脚本 - 启动
echo [测试] 此脚本将每隔 5 秒输出一次心跳
echo [测试] 使用任务管理器手动终止 cmd.exe 来测试守护重启
echo.

:LOOP
set /a COUNT+=1
echo [心跳 !COUNT!] %date% %time%
echo [%date% %time%] 心跳 !COUNT! >> "!LOGFILE!"
timeout /t 5 /nobreak >nul
goto LOOP
