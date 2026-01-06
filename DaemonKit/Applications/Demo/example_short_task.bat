@echo off
REM ================================================================
REM 短期任务示例脚本
REM 说明: 这是一个一次性执行的任务脚本
REM 用途: 定时备份、日志清理、文件同步等短期任务
REM 执行时间: 通常在几秒到几分钟内完成
REM ================================================================

setlocal enabledelayedexpansion

REM 设置脚本编码
chcp 65001 >nul

echo.
echo ================================
echo 短期任务示例 - 开始执行
echo ================================
echo 时间: %date% %time%
echo.

REM 示例1: 备份文件
echo [1/3] 执行文件备份...
if not exist "%TEMP%\DaemonKit_Backup" (
    mkdir "%TEMP%\DaemonKit_Backup"
    echo     创建备份目录成功
)
echo.

REM 示例2: 清理临时文件
echo [2/3] 清理临时文件...
REM 仅作为演示，实际应用中根据需要修改路径
echo     清理 %TEMP% 中的临时文件（演示）
echo.

REM 示例3: 写入日志
echo [3/3] 记录执行日志...
echo %date% %time% - 短期任务已执行 >> "%TEMP%\DaemonKit_ShortTask.log"
echo     日志已记录到 %TEMP%\DaemonKit_ShortTask.log
echo.

echo ================================
echo 短期任务示例 - 执行完成
echo ================================
echo 返回码: 0
echo.

REM 任务完成，脚本退出
REM NoDaemon=true 时，DaemonKit 将不再重启此脚本
exit /b 0
