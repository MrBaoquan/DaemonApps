// dllmain.cpp : 定义 DLL 应用程序的入口点。
#define WIN32_LEAN_AND_MEAN             // 从 Windows 头文件中排除极少使用的内容
#include <windows.h>
#include <vector>



DWORD dwTargetProcessID = -1;
std::vector<HWND> vecTargetWindows;

BOOL CALLBACK EnumWindowsProc(HWND hwnd, LPARAM lParam)
{
    DWORD dwProcessID;
    GetWindowThreadProcessId(hwnd, &dwProcessID);
    if (dwProcessID == dwTargetProcessID)
    {
        vecTargetWindows.push_back(hwnd);
    }
    return TRUE;
}

void reqQuitAllTargetWindows()
{
    vecTargetWindows.clear();
    EnumWindows(EnumWindowsProc, 0);
    for (auto hwnd : vecTargetWindows)
    {
		PostMessage(hwnd, WM_CLOSE, 0, 0);
	}
}


BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    dwTargetProcessID = GetCurrentProcessId();
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

