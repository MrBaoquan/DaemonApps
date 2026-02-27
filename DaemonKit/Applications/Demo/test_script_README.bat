@echo off
REM ================================================================
REM 批处理守护测试说明
REM ================================================================
REM
REM 本目录包含 5 个测试脚本, 覆盖 DaemonKit 脚本守护的各种场景:
REM
REM  脚本                          配置                    预期行为
REM  ─────────────────────────────  ──────────────────────  ─────────────
REM  test_script_short.bat         IsScript=true           3秒退出, 自动重启
REM  test_script_long.bat          IsScript=true           持续运行, 手动Kill后自动重启
REM  test_script_crash.bat         IsScript=true           5秒异常退出, 自动重启
REM  test_script_with_child.bat    IsScript=true           带子进程, Kill时子进程一并终止
REM  test_script_nodaemon.bat      NoDaemon=true           退出后不重启
REM
REM ================================================================
REM 配置示例 (在 DaemonKit 进程树中添加节点):
REM
REM 方法1: 直接使用绝对路径
REM   Name="测试短脚本" Path="C:\...\Applications\Demo\test_script_short.bat" IsScript="true"
REM
REM 方法2: 使用 NoDaemon 标记一次性脚本
REM   Name="测试一次性" Path="C:\...\Applications\Demo\test_script_nodaemon.bat" NoDaemon="true"
REM
REM ================================================================
REM 测试步骤:
REM
REM 1. 在 DaemonKit 中添加脚本节点 (使用上述配置)
REM 2. 启动进程树
REM 3. 观察日志输出, 确认脚本启动成功
REM 4. 在任务管理器中手动终止 cmd.exe 进程
REM 5. 确认 DaemonKit 自动重启了被终止的脚本 (NoDaemon 除外)
REM 6. 日志文件保存在 %%TEMP%%\DaemonKit_test_*.log
REM
REM ================================================================

echo 此文件为说明文件, 不需要直接运行
echo 请参阅文件内的注释了解测试方法
pause
