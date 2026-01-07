@echo off

%1 mshta vbscript:CreateObject("Shell.Application").ShellExecute("cmd.exe","/c %~s0 ::","","runas",1)(window.close)&&exit
cd /d "%~dp0"


setlocal EnableDelayedExpansion
color A

echo 请选择网络适配器
set _id=0
for /f "skip=3 tokens=1*" %%i in ('netsh interface show interface') DO (
	set /a _id+=1
	echo [!_id!] %%j
)

:SelectNetAdapter
set /p _input=:

set _id=0
for /f "skip=3 tokens=3*" %%i in ('netsh interface show interface') DO (
	set /a _id+=1
	set name_!_id!=%%j
)

if "!name_%_input%!" EQU "" (
	echo 输入有误,请重新输入
	echo.
	goto SelectNetAdapter
)

echo.
set address="!name_%_input%!"
echo 你已选择%address%
echo 输入IPV4地址
set /p IP=:

echo 输入子网掩码
set /p MASK=:

echo 输入网关地址
set /p GATEWAY=:

echo 输入DNS:
set /p DNS1=:

netsh interface ipv4 set address name=%address% static addr=%IP% mask=%MASK% gateway=%GATEWAY%
netsh interface ipv4 set dns %address% static %DNS1%

echo IP更改完成
echo 正在读取IP信息:
ping 127.0.0.1 -n 5 >nul

netsh interface ip show addresses %address%

pause