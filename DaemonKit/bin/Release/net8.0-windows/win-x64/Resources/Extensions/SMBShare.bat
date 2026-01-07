chcp 65001
@echo off
rem cd /d %~dp0../../Share

cd /d %~dp0../../Applications
for /f "delims=" %%i in ("%cd%") do set sharename=%%~ni
net user guest /active:yes
net share Applications=%cd% /unlimited /GRANT:Everyone,FULL  /remark:"共享文件夹" /CACHE:No
Icacls %cd% /grant Everyone:F /inheritance:e /T

netsh advfirewall firewall set rule group="网络发现" new enable=yes
netsh advfirewall firewall set rule group="文件和打印机共享" new enable=yes



