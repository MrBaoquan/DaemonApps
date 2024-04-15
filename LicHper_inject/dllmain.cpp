// dllmain.cpp : 定义 DLL 应用程序的入口点。

#include <process.h>
#include <Windows.h>

#include <filesystem>
#include <string>
#include <comdef.h>

#pragma comment(lib,"LicHper.lib")

#ifdef WANGWANG_EXPORTS
#define WANGWANG_API __declspec(dllexport)
#else
#define WANGWANG_API __declspec(dllimport)
#endif

extern "C"
{
	// 验证软件是否授权
	__declspec(dllimport) int __stdcall Validate(BSTR AppID, int uiFlag = 0);
}


std::string processName ="Unknown";

extern "C"
{
    WANGWANG_API int WangWang(void)
    {
        
        return 2021;
    }

}

unsigned proc_t(LPVOID lParam)
{
    // Check();
    
    Validate(SysAllocString(_bstr_t(processName.c_str())));
    
    _endthreadex(0);
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule,
    DWORD  ul_reason_for_call,
    LPVOID lpReserved
)
{
    static char szPath[MAX_PATH] = { 0 };
    GetModuleFileNameA(nullptr, szPath, MAX_PATH);

    std::filesystem::path fullpath(szPath);
    processName = fullpath.filename().stem().string();
    // 获取进程名

    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
    {
        _beginthreadex(nullptr, 0, (_beginthreadex_proc_type)proc_t, nullptr, 0, nullptr);
    }
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

