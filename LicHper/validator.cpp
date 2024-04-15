#include "validator.h"

// Crypto++ 加密库
#include "cryptopp/include/aes.h"
#include "cryptopp/include/modes.h"
#include "cryptopp/include/filters.h"
#include "cryptopp/include/base64.h"

// cereal 序列化库
#include "cereal/types/string.hpp"
#include "cereal/types/map.hpp"
#include "cereal/archives/binary.hpp"
#include "cereal/archives/json.hpp"



#include <comdef.h>
#include <Wbemidl.h>
#include <string>
#pragma comment(lib, "wbemuuid.lib")

#include <UserEnv.h>
#pragma comment(lib, "userenv.lib")

#include <cstdlib>

#include <filesystem>
#include <fstream>
#include <map>
#include <sstream>

#include <chrono>
#include <format>

#include <codecvt>
#include <locale>
#include <Windows.h>

#include <thread>


int initImgui();

// 将时间转为字符串
std::string time_to_string(const std::chrono::system_clock::time_point& tp) {
	using namespace std::chrono;

	// 将时间点转化为time_t类型
	time_t t = system_clock::to_time_t(tp);

	// 创建一个tm结构体
	std::tm tm = {};
	// 将time_t类型的时间转化为tm结构体
	localtime_s(&tm, &t);

	// 将tm结构体按照给定的格式转化为字符串
	char buffer[80];
	strftime(buffer, sizeof(buffer), "%Y-%m-%d %H:%M:%S", &tm);

	return std::string(buffer);
}

// 将字符串转为时间
std::chrono::system_clock::time_point string_to_time(const std::string& time_str) {
    using namespace std::chrono;
    // 创建一个tm结构体
    std::tm tm = {};
    // 把字符串按照给定的格式解析到tm结构体中
    std::istringstream time_stream(time_str);
    time_stream >> std::get_time(&tm, "%Y-%m-%d %H:%M:%S");

    // 将tm结构体转化为时间点time_point类型
    system_clock::time_point tp = system_clock::from_time_t(std::mktime(&tm));
    return tp;
}



// 获取主板BIOS UUID
std::string GetBiosUUID()
{
    CoInitializeEx(0, COINIT_MULTITHREADED);
    CoInitializeSecurity(NULL, -1, NULL, NULL, RPC_C_AUTHN_LEVEL_DEFAULT, RPC_C_IMP_LEVEL_IMPERSONATE, NULL, EOAC_NONE, NULL);

    IWbemLocator* pLoc = NULL;
    CoCreateInstance(CLSID_WbemLocator, 0, CLSCTX_INPROC_SERVER, IID_IWbemLocator, (LPVOID*)&pLoc);

    IWbemServices* pSvc = NULL;
    pLoc->ConnectServer(_bstr_t(L"ROOT\\CIMV2"), NULL, NULL, 0, NULL, 0, 0, &pSvc);

    CoSetProxyBlanket(pSvc, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, NULL, RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE, NULL, EOAC_NONE);

    IEnumWbemClassObject* pEnumerator = NULL;
    pSvc->ExecQuery(bstr_t("WQL"), bstr_t("SELECT UUID FROM Win32_ComputerSystemProduct"), WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY, NULL, &pEnumerator);

    IWbemClassObject* pclsObj = NULL;
    ULONG uReturn = 0;

    std::string biosUUID = "";

    while (pEnumerator)
    {
        pEnumerator->Next(WBEM_INFINITE, 1, &pclsObj, &uReturn);

        if (uReturn == 0) break;

        VARIANT vtProp;
        pclsObj->Get(L"UUID", 0, &vtProp, 0, 0);
        biosUUID = _bstr_t(vtProp.bstrVal);
        VariantClear(&vtProp);
        pclsObj->Release();
    }

    pSvc->Release();
    pEnumerator->Release();
    pLoc->Release();
    CoUninitialize();

    return biosUUID;
}

// 获取用户目录
std::string GetUserFolder() {
    char* userFolder;
    size_t len;
    std::string _folder = "";
    _dupenv_s(&userFolder, &len, "USERPROFILE");
    if (userFolder != NULL) {
        _folder = userFolder;
        free(userFolder);
    }
    return _folder;
}
using namespace CryptoPP;

std::string licensePath() {
    return GetUserFolder() + "\\.authrc";
}



// AES加密函数
std::string AESEncrypt(const std::string& plaintext, const std::string& key) {
    std::string ciphertext;

    try {
        CBC_Mode<AES>::Encryption encryption((byte*)key.c_str(), key.length(), (byte*)key.c_str());
        StringSource(plaintext, true, new StreamTransformationFilter(encryption, new Base64Encoder(new StringSink(ciphertext))));
    }
    catch (const CryptoPP::Exception& e) {
        std::cerr << "Encryption error: " << e.what() << std::endl;
    }

    return ciphertext;
}

// AES解密函数
std::string AESDecrypt(const std::string& ciphertext, const std::string& key) {
    std::string decrypted = "";
    try {
        CBC_Mode<AES>::Decryption decryption((byte*)key.c_str(), key.length(), (byte*)key.c_str());
        StringSource(ciphertext, true, new Base64Decoder(new StreamTransformationFilter(decryption, new StringSink(decrypted))));
    }
    catch (const CryptoPP::Exception& e) {
        return "";
    }

    return decrypted;
}

// 用于密钥信息记得加密存储
std::string LICENSE_KEY = "mrbaoquan1231231";

// 没有许可证的日期
std::string NO_LICENSE = "1970-01-01 00:00:00";

std::string latestSystime(std::string lastVerifiedTime){
    auto _now = std::chrono::system_clock::now();
    auto _last = string_to_time(lastVerifiedTime);
    if(_last > _now){
        return time_to_string(_last);
    }
    return time_to_string(_now);
}

struct LicenseInfo {
    // 姓名
    std::string username;
    // 更新时间
    std::string updated_at;
    // 过期时间
    std::string expired_at;
    // 最新有效系统时间
    std::string last_verified_at;
    // 软件ID
    std::string appid;

    template<class Archive>
    void serialize(Archive& archive)
    {
        archive(CEREAL_NVP(username), CEREAL_NVP(updated_at), CEREAL_NVP(expired_at), CEREAL_NVP(last_verified_at), CEREAL_NVP(appid));
    }
};

struct License {
    // 机器序列号
    std::string serial_number;

    std::map<std::string, LicenseInfo> data;

    // 用户登录缓存的密钥
    std::string cached_license;

    // 用户登录缓存密钥过期时间
    std::string license_expired_at;


    LicenseInfo operator[](const std::string& key)
    {
        LicenseInfo info;
        if (data.find(key) == data.end())
        {
            std::stringstream ss;
            {
                info.username = "system";
                std::string _systime = time_to_string(std::chrono::system_clock::now());
                info.updated_at = _systime;
                // 3天后过期
                info.expired_at = NO_LICENSE;
                info.last_verified_at = latestSystime(NO_LICENSE);
                info.appid = key;
            }
            data.insert(std::pair<std::string, LicenseInfo>(key, info));
            Save(*this);
        }

       return data[key];
    }

    static License Load()
    {
        std::string _licensePath = licensePath();
        License _license;
        if (!std::filesystem::exists(_licensePath)) // 如果授权文件不存在
        {
            _license.serial_number = GetBiosUUID();
            _license.license_expired_at = NO_LICENSE;
            // 使用 cereal 序列化库保存授权信息
            Save(_license);
        }
        // 使用 cereal 序列化库加载授权信息
        std::ifstream is(_licensePath,std::ios::binary);
        std::stringstream ss;
        ss << is.rdbuf();
        is.close();
        std::string encrypted = ss.str();
        std::string decrypted = AESDecrypt(encrypted, LICENSE_KEY);
        if(decrypted.empty())
        {
            // 删除license文件
            std::filesystem::remove(_licensePath);
            return Load();
        }
        
        
        std::stringstream ss2;
        ss2 << decrypted;
        {
            cereal::JSONInputArchive archive(ss2);
            archive(_license);
        }
        return _license;
    }

    static void Save(const License& license) {

        // 使用 cereal 序列化库保存授权信息
        std::stringstream ss;
        {
            cereal::JSONOutputArchive archive(ss);
            archive(license);
        }
        // 将序列化数据加密后保存到文件
        auto jsonData1 = ss.str();
        std::string encrypted = AESEncrypt(jsonData1, LICENSE_KEY);
        std::ofstream os(licensePath(), std::ios::binary);
        os << encrypted;
        os.close();
    }

    template<class Archive>
    void serialize(Archive& archive)
    {
        archive(CEREAL_NVP(serial_number), CEREAL_NVP(data), CEREAL_NVP(cached_license), CEREAL_NVP(license_expired_at));
    }
};

std::string BSTR2String(const BSTR bstr)
{
    _bstr_t t(bstr);
	return (char*)t;
}

BSTR string2BSTR(const std::string& str)
{
    return SysAllocString(_bstr_t(str.c_str()));
}  

struct UserInfo {
    // 用户名
    std::string username;
    // 过期时间
    std::string expired_at;

    // 联系方式
    std::string phone_number;

    // 软件id
    std::string appid;

    // 错误信息
    std::string error;

    template<class Archive>
    void serialize(Archive& archive)
    {
        archive(CEREAL_NVP(username), CEREAL_NVP(expired_at), CEREAL_NVP(phone_number), CEREAL_NVP(error), CEREAL_NVP(appid));
    }
};
std::string GbkToUtf8(const std::string& gbkStr);
std::string Utf8ToGbk(const std::string& utf8Str);
UserInfo loadUser(const std::string& userLicense)
{
    UserInfo _userInfo;
    std::string _userInfoJsonString = AESDecrypt(userLicense, LICENSE_KEY);
    if(_userInfoJsonString == "")
    {
        _userInfo.error = "无效许可证";
        return _userInfo;
    }

    std::stringstream ss;
    ss << _userInfoJsonString;
    
    {
        cereal::JSONInputArchive archive(ss);
        archive(_userInfo);
    }
    _userInfo.username = Utf8ToGbk(_userInfo.username);
    _userInfo.appid = Utf8ToGbk(_userInfo.appid);
    return _userInfo;
}

std::string g_appID;

// 根据用户许可证对app进行授权
int RenewByLicense(const char* key) {
    const auto& user = loadUser(key);
    if (user.error != "")
    {
		return 10001;   // 无效的许可证
	}

    if (user.appid != g_appID)
    {
		return 10002;   // 无效的软件ID
	}

    auto license = License::Load();
	auto appInfo = license[user.appid];
    appInfo.username = user.username;
    appInfo.expired_at = user.expired_at;
    appInfo.last_verified_at = latestSystime(appInfo.last_verified_at);
    license.data[user.appid] = appInfo;
    License::Save(license);
	return Validate(string2BSTR(appInfo.appid));
}


bool checkLogin()
{
    License license = License::Load();
    
    if(license.cached_license == "")
    {
        return false;
    }

    if (license.license_expired_at < time_to_string(std::chrono::system_clock::now()))
    {
        return false;
    }

    auto user = loadUser(license.cached_license);
    if (user.error != "")
    {
        return false;
    }

    return true;
}

// 登录
VALIDATOR_API BSTR __stdcall Login(BSTR userLicense){
    std::string _userLicense = BSTR2String(userLicense);
    std::string errorMsg = "{value0: {error:\"无效许可证\"}}";
    // 如果_userLicense为空, 尝试使用本地缓存登录
    if (_userLicense == "")
    {
        License license = License::Load();
        if (license.license_expired_at < time_to_string(std::chrono::system_clock::now()))
        {
            return string2BSTR(errorMsg);
        }
        _userLicense = license.cached_license;
    }

    const auto& _userInfo = loadUser(_userLicense);
    if(_userInfo.phone_number == "" || _userInfo.username == "")
    {
        return string2BSTR(errorMsg);
    }

    if (_userInfo.error == "")
    {
        License license = License::Load();
        license.cached_license = _userLicense;
        
        std::string _today = time_to_string(std::chrono::system_clock::now());
        _today = _today.substr(0, 10) + " 23:59:59";
        license.license_expired_at = _today;
        License::Save(license);	
	}

    std::string _userInfoJsonString;
    {
        std::stringstream ss;
        {
            cereal::JSONOutputArchive archive(ss);
            archive(_userInfo);
        }
        _userInfoJsonString = ss.str();
    }

    return string2BSTR(_userInfoJsonString);
}

// 退出
VALIDATOR_API int __stdcall Logout(){
    License license = License::Load();
    license.cached_license = "";
    license.license_expired_at = NO_LICENSE;
    License::Save(license);
    return 0;
}

VALIDATOR_API BSTR __stdcall GetLicense()
{
    LicenseInfo info;
    auto license = License::Load();
    std::stringstream ss;
    {
		cereal::JSONOutputArchive archive(ss);
        archive(CEREAL_NVP(license));
	}
    return string2BSTR(ss.str());
}



// 验证授权
VALIDATOR_API int __stdcall Validate(BSTR AppID, int uiFlag)
{
    auto license = License::Load();
    auto machineCode = GetBiosUUID();

    // 机器码不匹配
    if (license.serial_number != machineCode)
    {
        return 10001;
    }

    std::string _appID = BSTR2String(AppID);
    g_appID = _appID;
    auto _licenseData = license[_appID];

    bool _isNoLicense = _licenseData.expired_at == NO_LICENSE;
    bool _isExpired = string_to_time(_licenseData.expired_at) < std::chrono::system_clock::now();
    if(_isExpired || _isNoLicense){
        /*std::thread th(initImgui);
        th.detach();*/
        if(uiFlag == 0)
            initImgui();
        return 10002;
    }

    _licenseData.last_verified_at = latestSystime(_licenseData.last_verified_at);
    license.data[_appID] = _licenseData;
    License::Save(license);
    
    return 0;
}


VALIDATOR_API int __stdcall Renew(BSTR AppID, BSTR expiredAt)
{
    if(checkLogin()==false){
        return 10001;
    }

    auto _userInfo = loadUser(License::Load().cached_license);

    auto license = License::Load();
    const auto& _appID = BSTR2String(AppID);
    auto appLicense = license[_appID];
    appLicense.expired_at = BSTR2String(expiredAt);
    appLicense.username = _userInfo.username;

    license.data[_appID] = appLicense;
    License::Save(license);
    return 0;
}

VALIDATOR_API int __stdcall Unsubscribe(BSTR AppID)
{
    if(checkLogin()==false){
        return 10001;
    }

    auto license = License::Load();
    const auto& _appID = BSTR2String(AppID);
	auto appLicense = license[_appID];
    appLicense.expired_at = NO_LICENSE;
    license.data[_appID] = appLicense;
	License::Save(license);

    return 0;
}

VALIDATOR_API int __stdcall ClearLicense(){
    auto license = License::Load();
    license.data.clear();
    License::Save(license);
    return 0;
}
