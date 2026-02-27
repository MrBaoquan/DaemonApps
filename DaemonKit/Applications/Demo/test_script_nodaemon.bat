@echo off
REM ================================================================
REM 测试脚本: 一次性执行批处理 (NoDaemon 测试)
REM 用途: 验证 NoDaemon="true" 时脚本退出后不会被重启
REM 配置: NoDaemon="true"
REM 预期: 脚本执行完毕后正常退出, DaemonKit 不应重启此脚本
REM ================================================================

chcp 65001 >nul
setlocal enabledelayedexpansion

set "LOGFILE=%TEMP%\DaemonKit_test_nodaemon.log"

echo [%date% %time%] ===== 一次性脚本启动 ===== >> "!LOGFILE!"

echo.
echo [测试] 一次性脚本 (NoDaemon) - 开始
echo [测试] 此脚本执行完毕后应正常退出, 不会被 DaemonKit 重启
echo.

echo [步骤1] 模拟数据处理...
timeout /t 2 /nobreak >nul
echo     完成

echo [步骤2] 模拟报告生成...
timeout /t 2 /nobreak >nul
echo     完成

echo [步骤3] 写入执行记录...
echo [%date% %time%] 一次性脚本执行完毕 >> "!LOGFILE!"
echo     完成

echo.
echo [测试] 一次性脚本 - 正常退出
echo [测试] 检查: 此脚本退出后不应被 DaemonKit 重启
echo.

exit /b 0
