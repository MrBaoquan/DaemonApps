#pragma once
#include "cryptopp/include/cryptlib.h"
#include "Windows.h"

// 检查是debug还是release
#if _DEBUG
#pragma comment(lib, "cryptlibd.lib")
#else
#pragma comment(lib, "cryptlib.lib")
#endif

#ifndef VALIDATOR_API
#define VALIDATOR_API __declspec(dllexport)
#endif


extern "C"
{
	// 登录
	VALIDATOR_API BSTR __stdcall Login(BSTR userLicense);

	// 退出
	VALIDATOR_API int __stdcall Logout();

	// 获取软件授权信息
	VALIDATOR_API BSTR __stdcall GetLicense();
	
	// 验证软件是否授权
	VALIDATOR_API int __stdcall Validate(BSTR AppID, int uiFlag = 0);

	// 续订授权
	VALIDATOR_API int __stdcall Renew(BSTR AppID, BSTR expiredAt);

	// 退订授权
	VALIDATOR_API int __stdcall Unsubscribe(BSTR AppID);

	// 清空授权
	VALIDATOR_API int __stdcall ClearLicense();
}

