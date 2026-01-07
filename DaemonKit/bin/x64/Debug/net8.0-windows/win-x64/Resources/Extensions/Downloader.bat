@echo off && color A
setlocal EnableDelayedExpansion
echo 请输入要下载的文件地址
set /p fileUrl=:
for /r %%f in (%fileUrl%) do (
	set saveTo=%userprofile%\downloads\%%~nxf
)

curl -L %fileUrl%>%saveTo%
echo 文件已下载至:%saveTo%
explorer.exe /select,%saveTo%
ping 127.0.0.1 -n 3 >nul