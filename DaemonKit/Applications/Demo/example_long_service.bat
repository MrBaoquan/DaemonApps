@echo off
REM ================================================================
REM 长期服务示例脚本
REM 说明: 这是一个持续运行的服务脚本
REM 用途: 日志监听、性能监测、实时同步等需要持续运行的服务
REM 执行方式: 启动后持续运行，DaemonKit 会在进程退出后自动重启
REM ================================================================

setlocal enabledelayedexpansion

REM 设置脚本编码
chcp 65001 >nul

echo.
echo ================================
echo 长期服务示例 - 启动
echo ================================
echo 启动时间: %date% %time%
echo.

REM 创建日志文件
set "LOGFILE=%TEMP%\DaemonKit_LongService.log"
if not exist "!LOGFILE!" (
    echo DaemonKit 长期服务日志 > "!LOGFILE!"
)

echo [%date% %time%] 服务启动 >> "!LOGFILE!"
echo 日志位置: !LOGFILE!
echo.

REM 主服务循环
setlocal enabledelayedexpansion
set "INTERVAL=5"
set "COUNT=0"
set "MAX_COUNT=12"

:SERVICE_LOOP
echo [%date% %time%] 监测心跳 >> "!LOGFILE!"
echo.
echo 服务运行中... (心跳 !COUNT!/!MAX_COUNT!)
echo [%date% %time%] 监测心跳 | findstr . >> "!LOGFILE!"

REM 模拟服务工作: 检查某些条件
if !COUNT! equ 3 (
    echo [%date% %time%] 执行定期维护任务 >> "!LOGFILE!"
    echo.
    echo 执行定期维护...
)

REM 每5秒循环一次
timeout /t !INTERVAL! /nobreak >nul

set /a COUNT+=1

REM 演示模式: 运行 60 秒后退出（在实际应用中应该是无限循环）
if !COUNT! lss !MAX_COUNT! goto SERVICE_LOOP

REM 服务正常退出
echo.
echo ================================
echo 长期服务示例 - 关闭
echo ================================
echo [%date% %time%] 服务关闭 >> "!LOGFILE!"
echo 关闭时间: %date% %time%
echo 运行时长: 约 60 秒
echo.
echo 日志文件: !LOGFILE!
echo.

echo 注意: IsScript=true 时，DaemonKit 将在进程退出后自动重启此脚本
echo.

exit /b 0
