@echo off
REM ================================================================
REM 测试脚本: 带子进程的批处理 (进程树终止测试)
REM 用途: 验证 Kill(true) 能正确终止脚本启动的子进程树
REM 配置: IsScript="true"
REM 预期: KillNode 应同时终止 cmd.exe 和它启动的子进程 (ping)
REM ================================================================

chcp 65001 >nul
setlocal enabledelayedexpansion

set "LOGFILE=%TEMP%\DaemonKit_test_child.log"

echo [%date% %time%] ===== 带子进程脚本启动 ===== >> "!LOGFILE!"

echo.
echo [测试] 带子进程脚本 - 启动
echo [测试] 此脚本将启动一个长时间运行的 ping 子进程
echo [测试] 当 DaemonKit 终止此脚本时, ping 子进程也应被一并终止
echo.

echo [INFO] 启动 ping 子进程 (持续 ping localhost)...
echo [%date% %time%] 启动 ping 子进程 >> "!LOGFILE!"

REM 启动一个长运行的子进程 (ping -t 持续运行)
REM 使用 start /B 在后台启动, 让 cmd.exe 作为父进程
start /B ping -n 9999 127.0.0.1 >nul

echo [INFO] 子进程已启动, 主脚本进入等待循环
echo [INFO] 在任务管理器中可以看到 cmd.exe 下有 PING.EXE 子进程
echo.

set "COUNT=0"
:LOOP
set /a COUNT+=1
echo [心跳 !COUNT!] %date% %time% (主脚本 + ping 子进程运行中)
echo [%date% %time%] 心跳 !COUNT! >> "!LOGFILE!"
timeout /t 5 /nobreak >nul
goto LOOP
