// Dear ImGui: standalone example application for DirectX 11

#pragma execution_character_set("utf-8")

// Learn about Dear ImGui:
// - FAQ                  https://dearimgui.com/faq
// - Getting Started      https://dearimgui.com/getting-started
// - Documentation        https://dearimgui.com/docs (same as your local docs/ folder).
// - Introduction, links and more at the top of imgui.cpp

#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"
#include <d3d11.h>
#include <tchar.h>
#include <format>
#include <Windows.h>
#include "mINI/ini.h"

// stb_image for loading images
#define STB_IMAGE_IMPLEMENTATION
#include "stb/stb_image.h"

#include <regex>
#include <string>
#include <codecvt>
#include <locale>
#include <chrono>
#include <thread>
#include <filesystem>

#include <tlhelp32.h>
#include <iostream>
#include <algorithm>
// Data
static ID3D11Device *g_pd3dDevice = nullptr;
static ID3D11DeviceContext *g_pd3dDeviceContext = nullptr;
static IDXGISwapChain *g_pSwapChain = nullptr;
static UINT g_ResizeWidth = 0, g_ResizeHeight = 0;
static ID3D11RenderTargetView *g_mainRenderTargetView = nullptr;

// Watermark image texture
static ID3D11ShaderResourceView* g_pWatermarkTexture = nullptr;
static int g_WatermarkWidth = 0;
static int g_WatermarkHeight = 0;

// Forward declarations of helper functions
bool CreateDeviceD3D(HWND hWnd);
void CleanupDeviceD3D();
void CreateRenderTarget();
void CleanupRenderTarget();
LRESULT WINAPI WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

// Load image texture from file
bool LoadTextureFromFile(const char* filename, ID3D11ShaderResourceView** out_srv, int* out_width, int* out_height)
{
    // Load image using stb_image
    int image_width = 0;
    int image_height = 0;
    unsigned char* image_data = stbi_load(filename, &image_width, &image_height, NULL, 4);
    if (image_data == NULL)
        return false;
    
    // 安全检查：验证图片是否有有效内容（不是全透明或全空白）
    // 检查图片像素，确保至少有一定比例的不透明像素
    int totalPixels = image_width * image_height;
    int visiblePixels = 0;
    int minVisibleRequired = totalPixels / 10; // 至少10%的像素需要可见
    
    for (int i = 0; i < totalPixels; i++) {
        unsigned char alpha = image_data[i * 4 + 3]; // Alpha通道
        unsigned char r = image_data[i * 4 + 0];
        unsigned char g = image_data[i * 4 + 1];
        unsigned char b = image_data[i * 4 + 2];
        
        // 像素可见：Alpha > 30 且颜色不是纯白/纯透明
        if (alpha > 30 && (r > 10 || g > 10 || b > 10)) {
            visiblePixels++;
        }
        
        // 已经满足条件，提前退出
        if (visiblePixels >= minVisibleRequired) {
            break;
        }
    }
    
    // 如果可见像素不足，拒绝加载此图片
    if (visiblePixels < minVisibleRequired) {
        stbi_image_free(image_data);
        return false;
    }

    // Create texture
    D3D11_TEXTURE2D_DESC desc;
    ZeroMemory(&desc, sizeof(desc));
    desc.Width = image_width;
    desc.Height = image_height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.CPUAccessFlags = 0;

    ID3D11Texture2D* pTexture = NULL;
    D3D11_SUBRESOURCE_DATA subResource;
    subResource.pSysMem = image_data;
    subResource.SysMemPitch = desc.Width * 4;
    subResource.SysMemSlicePitch = 0;
    HRESULT hr = g_pd3dDevice->CreateTexture2D(&desc, &subResource, &pTexture);
    
    // Check if texture creation succeeded
    if (FAILED(hr) || pTexture == NULL) {
        stbi_image_free(image_data);
        return false;
    }

    // Create texture view
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc;
    ZeroMemory(&srvDesc, sizeof(srvDesc));
    srvDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MipLevels = desc.MipLevels;
    srvDesc.Texture2D.MostDetailedMip = 0;
    hr = g_pd3dDevice->CreateShaderResourceView(pTexture, &srvDesc, out_srv);
    pTexture->Release();
    
    // Check if shader resource view creation succeeded
    if (FAILED(hr) || *out_srv == NULL) {
        stbi_image_free(image_data);
        return false;
    }

    *out_width = image_width;
    *out_height = image_height;
    stbi_image_free(image_data);

    return true;
}


extern std::string g_appID;
void reqQuitAllTargetWindows();
int RenewByLicense(const char* key);

void killProcessByName(const char* lpctstrExeName) {
    HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnapshot) {
        PROCESSENTRY32 pe32;
        pe32.dwSize = sizeof(PROCESSENTRY32);
        if (Process32First(hSnapshot, &pe32)) {
            do {
                if (strcmp(pe32.szExeFile, lpctstrExeName) == 0) {
                    HANDLE hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pe32.th32ProcessID);
                    if (hProcess) {
                        TerminateProcess(hProcess, 0);
                        CloseHandle(hProcess);
                    }
                }
            } while (Process32Next(hSnapshot, &pe32));
        }
        CloseHandle(hSnapshot);
    }
}


// 获取用户目录
std::string GetUserFolder();

std::string GbkToUtf8(const std::string& gbkStr)
{
    int len = MultiByteToWideChar(CP_ACP, 0, gbkStr.c_str(), -1, NULL, 0);
    wchar_t* wstr = new wchar_t[len + 1];
    memset(wstr, 0, len + 1);
    MultiByteToWideChar(CP_ACP, 0, gbkStr.c_str(), -1, wstr, len);

    len = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, NULL, 0, NULL, NULL);
    char* str = new char[len + 1];
    memset(str, 0, len + 1);
    WideCharToMultiByte(CP_UTF8, 0, wstr, -1, str, len, NULL, NULL);

    std::string strTemp = str;
    if (wstr) delete[] wstr;
    if (str) delete[] str;

    return strTemp;
}

std::string Utf8ToGbk(const std::string& utf8Str)
{
	int len = MultiByteToWideChar(CP_UTF8, 0, utf8Str.c_str(), -1, NULL, 0);
	wchar_t* wstr = new wchar_t[len + 1];
	memset(wstr, 0, len + 1);
	MultiByteToWideChar(CP_UTF8, 0, utf8Str.c_str(), -1, wstr, len);

	len = WideCharToMultiByte(CP_ACP, 0, wstr, -1, NULL, 0, NULL, NULL);
	char* str = new char[len + 1];
	memset(str, 0, len + 1);
	WideCharToMultiByte(CP_ACP, 0, wstr, -1, str, len, NULL, NULL);

	std::string strTemp = str;
	if (wstr) delete[] wstr;
	if (str) delete[] str;

	return strTemp;
}

// 设置窗口置顶
void setWindowTop(HWND hwnd)
{
    LONG style = GetWindowLong(hwnd, GWL_EXSTYLE);
    SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED | WS_EX_TOPMOST);
}

ImVec4 ConvertStringToColor(const std::string& colorStr) {
    
    if(colorStr.size() == 0) return ImVec4(1.0f, 1.0f, 1.0f, 1.0f);

    if (colorStr[0] == '#' && colorStr.size() == 9) { //检查字符串格式
        //将每个分量从十六进制字符串转换为整数
        auto r = std::stoi(colorStr.substr(1, 2), nullptr, 16);
        auto g = std::stoi(colorStr.substr(3, 2), nullptr, 16);
        auto b = std::stoi(colorStr.substr(5, 2), nullptr, 16);
        auto a = std::stoi(colorStr.substr(7, 2), nullptr, 16);

        //将整数值归一化到0-1的范围，生成ImVec4
        return ImVec4(r / 255.f, g / 255.f, b / 255.f, a / 255.f);
    }else if (colorStr[0] == '#' && colorStr.size() == 7) { //检查字符串格式
        //将每个分量从十六进制字符串转换为整数
        auto r = std::stoi(colorStr.substr(1, 2), nullptr, 16);
        auto g = std::stoi(colorStr.substr(3, 2), nullptr, 16);
        auto b = std::stoi(colorStr.substr(5, 2), nullptr, 16);

        //将整数值归一化到0-1的范围，生成ImVec4
        return ImVec4(r / 255.f, g / 255.f, b / 255.f, 1.0f);
    }
    
    //如果格式错误，返回默认颜色
    return ImVec4(1.0f, 1.0f, 1.0f, 1.0f);
}

std::wstring to_wstring(const std::string& str) {
    int strLength = (int)str.length() + 1;
    int len = MultiByteToWideChar(CP_ACP, 0, str.c_str(), strLength, 0, 0);
    std::wstring wstr(len, L'\0');
    MultiByteToWideChar(CP_ACP, 0, str.c_str(), strLength, &wstr[0], len);
    return wstr;
}

using namespace std::chrono;

int initImgui()
{
    // Create application window
    // ImGui_ImplWin32_EnableDpiAwareness();
    WNDCLASSEXW wc = {sizeof(wc), CS_CLASSDC, WndProc, 0L, 0L, GetModuleHandle(nullptr), nullptr, nullptr, nullptr, nullptr, L"ImGui Example", nullptr};
    ::RegisterClassExW(&wc);

    std::wstring windowTitle = to_wstring(g_appID + " Auth Required");
    HWND hwnd = ::CreateWindowW(wc.lpszClassName, windowTitle.c_str(), WS_POPUP, 0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN), nullptr, nullptr, wc.hInstance, nullptr);

    // 设置透明窗口 并置顶窗口
    LONG style = GetWindowLong(hwnd, GWL_EXSTYLE);
    SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED | WS_EX_TOPMOST );

    //设置黑色为透明
    //第二个参数RGB(0,0,0)表示黑色
    //最后一个参数LWA_COLORKEY表示将特定的颜色设置为透明色
    SetLayeredWindowAttributes(hwnd, RGB(0,0,0), 0, LWA_COLORKEY);

    // Initialize Direct3D
    if (!CreateDeviceD3D(hwnd))
    {
        CleanupDeviceD3D();
        ::UnregisterClassW(wc.lpszClassName, wc.hInstance);
        return 1;
    }

    std::string defaultWaterMark = "{APPID} Demo Version";
    std::string _iniPath = GetUserFolder() + "\\.authrc.ini";
    mINI::INIFile file(_iniPath);
    mINI::INIStructure ini;
    if (file.read(ini) == false) {

        ini["help"]["description"] = "\r\n {APPID} 为授权软件ID, 在显示时会被替换为真实ID, {COUNTDOWN}为程序退出倒计时 \n timeout_kill_self 超时是否关闭主进程 \n timeout_kill_other 为退出时同时关闭的进程列表, 多个进程用 | 分隔";

        ini["watermark"]["title"] = defaultWaterMark;
        ini["watermark"]["font_size"] = "80";
        ini["watermark"]["color"] = "#FF6666";
        ini["watermark"]["animate"] = "true";
        ini["watermark"]["image_path"] = "";  // 留空=默认.lichper/watermark.png, 支持相对路径(.lichper目录下)或绝对路径
        ini["watermark"]["image_scale"] = "1";  // 缩放比例，1=原始尺寸
        ini["watermark"]["image_alpha"] = "0.8";
        ini["watermark"]["image_align"] = "top-center"; // 对齐方式: top-left, top-center, top-right, center-left, center, center-right, bottom-left, bottom-center, bottom-right
        ini["watermark"]["image_padding_x"] = "50"; // 水平边距（像素）
        ini["watermark"]["image_padding_y"] = "50"; // 垂直边距（像素）
        
        ini["program"]["timeout"] = "60";
        ini["program"]["timeout_kill_self"] = "false";
        ini["program"]["timeout_kill_other"] = "";
        
        file.generate(ini,true);
    }

    std::string watermarkTemplate = ini["watermark"].has("title") ? ini["watermark"]["title"] : defaultWaterMark;

    // 安全检查：确保水印文字不为空，防止完全隐藏授权提示
    // 去除 {APPID} 和 {COUNTDOWN} 占位符后检查长度
    auto _customText = std::regex_replace(watermarkTemplate, std::regex("\\{APPID\\}|\\{COUNTDOWN\\}"), "");
    // 去除空格后检查（使用安全的方式处理多字节字符）
    _customText.erase(std::remove_if(_customText.begin(), _customText.end(), [](unsigned char c) { return std::isspace(c); }), _customText.end());
    if (_customText.size() < 2) {
        // 如果太短，强制使用默认水印
        watermarkTemplate = defaultWaterMark;
    }

    int auto_exit = ini["program"].has("timeout") ? std::stoi(ini["program"]["timeout"]) : 60;
    bool auto_exit_enable = auto_exit > 0;

    auto app_start_time =  high_resolution_clock::now();
    auto auto_exit_process = ini["program"].has("timeout_kill_other") ? ini["program"]["timeout_kill_other"] : "";
    auto timeout_kill_self = ini["program"].has("timeout_kill_self") ? ini["program"]["timeout_kill_self"] == "true" : false;

    std::vector<std::string> auto_exit_process_list;
    std::regex re(R"(\|)");
    std::sregex_token_iterator first{auto_exit_process.begin(), auto_exit_process.end(), re, -1}, last;
    auto_exit_process_list = {first, last};
    // 移除非.exe的进程
    auto_exit_process_list.erase(std::remove_if(auto_exit_process_list.begin(), auto_exit_process_list.end(), [](const std::string& s) { return s.find(".exe") == std::string::npos; }), auto_exit_process_list.end());

    bool animate = ini["watermark"].has("animate") ? ini["watermark"]["animate"] == "true" : true;
    int fontSize = ini["watermark"].has("font_size") ? std::stoi(ini["watermark"]["font_size"]) : 80;
    fontSize = std::clamp(fontSize, 36, 132);
    ImVec4 watermark_color = ini["watermark"].has("color") ? ConvertStringToColor(ini["watermark"]["color"]) : ConvertStringToColor("#FF6666");
    
    // 安全检查：确保水印颜色可见（Alpha最低0.5）
    if (watermark_color.w < 0.5f) {
        watermark_color.w = 0.5f;
    }

    // Load watermark image if specified
    std::string imagePath = ini["watermark"].has("image_path") ? ini["watermark"]["image_path"] : "";
    
    // 处理图片路径：支持相对路径（相对于 .lichper 文件夹）
    std::string lichperFolder = GetUserFolder() + "\\.lichper";
    if (imagePath.empty()) {
        // 默认路径：.lichper/watermark.png
        imagePath = lichperFolder + "\\watermark.png";
    } else if (imagePath.find(':') == std::string::npos && imagePath[0] != '\\' && imagePath[0] != '/') {
        // 相对路径：转换为 .lichper 文件夹下的路径
        imagePath = lichperFolder + "\\" + imagePath;
    }
    // 绝对路径保持不变
    
    // 图片缩放比例：0表示不缩放（使用原始尺寸）
    float imageScale = ini["watermark"].has("image_scale") ? std::stof(ini["watermark"]["image_scale"]) : 0.0f;
    imageScale = std::clamp(imageScale, 0.0f, 10.0f); // 限制缩放范围0-10倍
    
    // 透明度：限制0.3-1.0，确保水印可见
    float imageAlpha = ini["watermark"].has("image_alpha") ? std::stof(ini["watermark"]["image_alpha"]) : 0.8f;
    imageAlpha = std::clamp(imageAlpha, 0.3f, 1.0f); // 最低0.3，确保可见
    
    // 图片对齐方式：top-left, top-center, top-right, center-left, center, center-right, bottom-left, bottom-center, bottom-right
    std::string imageAlign = ini["watermark"].has("image_align") ? ini["watermark"]["image_align"] : "top-center";
    
    // 图片边距（像素）：根据对齐方式决定哪个方向生效
    int imagePaddingX = ini["watermark"].has("image_padding_x") ? std::stoi(ini["watermark"]["image_padding_x"]) : 50;
    int imagePaddingY = ini["watermark"].has("image_padding_y") ? std::stoi(ini["watermark"]["image_padding_y"]) : 50;
    imagePaddingX = (std::max)(0, imagePaddingX);
    imagePaddingY = (std::max)(0, imagePaddingY);
    
    bool hasWatermarkImage = false;
    if (std::filesystem::exists(imagePath)) {
        hasWatermarkImage = LoadTextureFromFile(imagePath.c_str(), &g_pWatermarkTexture, &g_WatermarkWidth, &g_WatermarkHeight);
    }

    // Show the window
    ::ShowWindow(hwnd, SW_SHOWDEFAULT);
    ::UpdateWindow(hwnd);

    // Setup Dear ImGui context
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO &io = ImGui::GetIO();
    io.IniFilename = nullptr;
    (void)io;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard; // Enable Keyboard Controls
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad;  // Enable Gamepad Controls

    io.Fonts->Flags |= ImFontAtlasFlags_NoPowerOfTwoHeight;

    // Setup Dear ImGui style
    ImGui::StyleColorsDark();
    // ImGui::StyleColorsLight();

    // Setup Platform/Renderer backends
    ImGui_ImplWin32_Init(hwnd);
    ImGui_ImplDX11_Init(g_pd3dDevice, g_pd3dDeviceContext);

    // Load Fonts
    // - If no fonts are loaded, dear imgui will use the default font. You can also load multiple fonts and use ImGui::PushFont()/PopFont() to select them.
    // - AddFontFromFileTTF() will return the ImFont* so you can store it if you need to select the font among multiple.
    // - If the file cannot be loaded, the function will return a nullptr. Please handle those errors in your application (e.g. use an assertion, or display an error and quit).
    // - The fonts will be rasterized at a given size (w/ oversampling) and stored into a texture when calling ImFontAtlas::Build()/GetTexDataAsXXXX(), which ImGui_ImplXXXX_NewFrame below will call.
    // - Use '#define IMGUI_ENABLE_FREETYPE' in your imconfig file to use Freetype for higher quality font rendering.
    // - Read 'docs/FONTS.md' for more instructions and details.
    // - Remember that in C/C++ if you want to include a backslash \ in a string literal you need to write a double backslash \\ !
    // io.Fonts->AddFontDefault();
    // io.Fonts->AddFontFromFileTTF("c:\\Windows\\Fonts\\segoeui.ttf", 18.0f);
    // io.Fonts->AddFontFromFileTTF("../../misc/fonts/DroidSans.ttf", 16.0f);
    // io.Fonts->AddFontFromFileTTF("../../misc/fonts/Roboto-Medium.ttf", 16.0f);
    // io.Fonts->AddFontFromFileTTF("../../misc/fonts/Cousine-Regular.ttf", 15.0f);
    // ImFont* font = io.Fonts->AddFontFromFileTTF("c:\\Windows\\Fonts\\ArialUni.ttf", 18.0f, nullptr, io.Fonts->GetGlyphRangesJapanese());
    
    // 配置字体抗锯齿参数以获得更平滑的字体渲染效果
    ImFontConfig fontConfig1;
    fontConfig1.OversampleH = 3; // 水平过采样，提高字体清晰度
    fontConfig1.OversampleV = 1; // 垂直过采样降低，减少黑边
    fontConfig1.PixelSnapH = false; // 禁用像素对齐，允许亚像素渲染
    fontConfig1.RasterizerMultiply = 1.3f; // 增强字体亮度，减少黑边
    
    ImFontConfig fontConfig2;
    fontConfig2.OversampleH = 3; // 水平过采样，提高字体清晰度
    fontConfig2.OversampleV = 1; // 垂直过采样降低，减少黑边
    fontConfig2.PixelSnapH = false; // 禁用像素对齐，允许亚像素渲染
    fontConfig2.RasterizerMultiply = 1.3f; // 增强字体亮度，减少黑边
    
    ImFont *font = io.Fonts->AddFontFromFileTTF("c:\\Windows\\Fonts\\msyh.ttc", 18.0f, &fontConfig1, io.Fonts->GetGlyphRangesChineseSimplifiedCommon());
    ImFont *titleFont = io.Fonts->AddFontFromFileTTF("c:\\Windows\\Fonts\\msyh.ttc", fontSize, &fontConfig2, io.Fonts->GetGlyphRangesChineseSimplifiedCommon());

    IM_ASSERT(font != nullptr);

    // Our state
    bool show_demo_window = false;
    bool show_another_window = false;
    // 清除颜色为黑色
    ImVec4 clear_color = ImVec4(0.0f, 0.0f, 0.0f, 0.0f);

    // Main loop
    bool done = false;

    std::string licenseError;

    
    ImVec2 titlePosition = ImVec2(0, 0);
    ImVec2 titleVelocity = ImVec2(1, 1);

    // 限制30帧/s
    const float frameTime = 1.0f / 60.0f;
    const auto frameDuration = duration_cast<milliseconds>(duration<float>(frameTime));


    while (!done)
    {
        auto frameStart = high_resolution_clock::now();

        // Poll and handle messages (inputs, window resize, etc.)
        // See the WndProc() function below for our to dispatch events to the Win32 backend.
        MSG msg;
        while (::PeekMessage(&msg, nullptr, 0U, 0U, PM_REMOVE))
        {
            ::TranslateMessage(&msg);
            ::DispatchMessage(&msg);
            if (msg.message == WM_QUIT)
                done = true;
        }


        auto app_time_elapsed = std::chrono::duration_cast<std::chrono::seconds>(high_resolution_clock::now() - app_start_time);
        if (auto_exit_enable &&timeout_kill_self) {

        }
        // 将{APPID}替换为实际的APPID
        std::string watermarkText = std::regex_replace(watermarkTemplate, std::regex("\\{APPID\\}"), GbkToUtf8(g_appID));
        // 将watermarkText中的{COUNTDOWN}替换为剩余时间
        auto _remain = auto_exit - app_time_elapsed.count();
        _remain = max(_remain, 0);

        std::string countdownText = std::format("{:02d}:{:02d}:{:02d}", _remain / 3600, (_remain % 3600) / 60, _remain % 60);
        watermarkText = std::regex_replace(watermarkText, std::regex("\\{COUNTDOWN\\}"), countdownText);

        if(auto_exit_enable && app_time_elapsed.count() > auto_exit){

            for (auto &process : auto_exit_process_list)
            {
                killProcessByName(process.c_str());
            }

            if(timeout_kill_self){
                done = true;
            }
        }

        if (done){
            reqQuitAllTargetWindows();
            break;
        }

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // Handle window resize (we don't resize directly in the WM_SIZE handler)
        if (g_ResizeWidth != 0 && g_ResizeHeight != 0)
        {
            CleanupRenderTarget();
            g_pSwapChain->ResizeBuffers(0, g_ResizeWidth, g_ResizeHeight, DXGI_FORMAT_UNKNOWN, 0);
            g_ResizeWidth = g_ResizeHeight = 0;
            CreateRenderTarget();
        }

        // Start the Dear ImGui frame
        ImGui_ImplDX11_NewFrame();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();
        {
            // 设置窗口充满整个屏幕
            ImGui::SetNextWindowPos(ImVec2(0, 0));
            ImGui::SetNextWindowSize(ImVec2(io.DisplaySize.x, io.DisplaySize.y));
            
            ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(1.0f, 1.0f, 1.0f, 0.0f));
            ImGui::Begin("Transparent", nullptr, ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoTitleBar);
            float window_width = io.DisplaySize.x;
            float window_height = io.DisplaySize.y;
      
            // Display watermark image if loaded
            if (hasWatermarkImage && g_pWatermarkTexture) {
                // 计算实际显示尺寸
                float displayWidth, displayHeight;
                
                if (imageScale > 0.0f) {
                    // 使用缩放比例（基于原始尺寸）
                    displayWidth = g_WatermarkWidth * imageScale;
                    displayHeight = g_WatermarkHeight * imageScale;
                } else {
                    // 使用原始尺寸
                    displayWidth = (float)g_WatermarkWidth;
                    displayHeight = (float)g_WatermarkHeight;
                }
                
                ImVec2 imageSize = ImVec2(displayWidth, displayHeight);
                ImVec2 imagePos;
                
                // 根据 align 和 padding 计算图片位置
                float posX = 0, posY = 0;
                
                // 水平对齐
                if (imageAlign.find("left") != std::string::npos) {
                    posX = (float)imagePaddingX;
                } else if (imageAlign.find("right") != std::string::npos) {
                    posX = window_width - imageSize.x - imagePaddingX;
                } else {
                    // center (默认)
                    posX = (window_width - imageSize.x) / 2;
                }
                    
                    // 垂直对齐
                    if (imageAlign.find("top") != std::string::npos) {
                        posY = (float)imagePaddingY;
                    } else if (imageAlign.find("bottom") != std::string::npos) {
                        posY = window_height - imageSize.y - imagePaddingY;
                    } else {
                        // center (默认)
                        posY = (window_height - imageSize.y) / 2;
                    }
                    
                    imagePos = ImVec2(posX, posY);
                    
                    ImGui::SetCursorPos(imagePos);
                    ImGui::Image((void*)g_pWatermarkTexture, imageSize, ImVec2(0, 0), ImVec2(1, 1), ImVec4(1, 1, 1, imageAlpha));
                }
                
                // Display text watermark (始终显示，确保授权提示可见)
                ImGui::PushFont(titleFont);
                ImGui::PushStyleColor(ImGuiCol_Text, watermark_color);
                const std::string& title = watermarkText;
                ImVec2 titleSize = ImGui::CalcTextSize((const char*)(title.data()));

        
                if (animate) {
                    // 标题随机移动，不会超出屏幕，触碰到边界时反向移动
                    if (titlePosition.x + titleSize.x + 10 >= window_width) {
                        titleVelocity.x = -1;
                    }
                    if (titlePosition.x <= 0) {
                        titleVelocity.x = 1;
                    }
                    if (titlePosition.y + titleSize.y + 10 >= window_height) {
                        titleVelocity.y = -1;
                    }
                    if (titlePosition.y <= 0) {
                        titleVelocity.y = 1;
                    }
                    titlePosition.x += (titleVelocity.x);
                    titlePosition.y += (titleVelocity.y);
                }
                else {
                    titlePosition = ImVec2((window_width - titleSize.x) - 50, 150);
                }
                ImGui::SetCursorPos(titlePosition);
                ImGui::Text((const char*)(title.data()));
                ImGui::PopStyleColor();
                ImGui::PopFont();
             

            static bool showActiveWindow = false;
            ImGui::SetCursorPosX(window_width - 90);
            if(animate)
                ImGui::SetCursorPosY(100);
            ImGui::PushStyleColor(ImGuiCol_Button, (ImVec4)ImColor::HSV(0.0f, 0.6f, 0.6f));
            ImGui::PushStyleColor(ImGuiCol_ButtonHovered, (ImVec4)ImColor::HSV(0.0f, 0.7f, 0.7f));
            ImGui::PushStyleColor(ImGuiCol_ButtonActive, (ImVec4)ImColor::HSV(0.0f, 0.8f, 0.8f));
            if(showActiveWindow==false){
                if (ImGui::ArrowButton("##right", ImGuiDir_Right)) {
                    showActiveWindow = true;
                }
            }else{
                if (ImGui::ArrowButton("##down", ImGuiDir_Down)) {
                    showActiveWindow = false;
                }
            }
            ImGui::PopStyleColor(3);
            // ImGui::Text("Application average %.3f ms/frame (%.1f FPS)", 1000.0f / io.Framerate, io.Framerate);
            ImGui::End();
            ImGui::PopStyleColor();

            if(showActiveWindow){
                // 授权窗口 960 540  居中显示
                ImVec2 licenseWindowSize = ImVec2(640, 420);
                ImGui::SetNextWindowPos(ImVec2((io.DisplaySize.x - licenseWindowSize.x) / 2, (io.DisplaySize.y - licenseWindowSize.y) / 2));
                ImGui::SetNextWindowSize(licenseWindowSize);
                ImGui::Begin("License", nullptr, ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoTitleBar);

                ImGui::SetCursorPosX(20);
                ImGui::SetCursorPosY(20);
                std::string tipText = std::format("请输入软件授权码:    APPID - [{}]",GbkToUtf8(g_appID));
                ImGui::Text(tipText.c_str());
                static char text[1024 * 16] = "";
                ImVec2 inputSize = ImVec2(600, 250);
                ImGui::SetCursorPosX((licenseWindowSize.x - inputSize.x) / 2);
                ImGui::SetCursorPosY(50);
                ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(16.0f, 16.0f));
                if (ImGui::InputTextMultiline("##source", text, IM_ARRAYSIZE(text), inputSize))
                {
                    // 文本自动换行处理
                }
                ImGui::PopStyleVar();

                if(licenseError.size()>0){
                    ImGui::SetCursorPosX(20);
                    ImGui::SetCursorPosY(310);
                    ImGui::TextColored(ImVec4(1.0f, 0.0f, 0.0f, 1.0f), licenseError.c_str());
                }

                // 创建 applay 和 cancel 两个按钮，横向居中显示
                ImGui::SetCursorPosX((licenseWindowSize.x - 240 - 30) / 2);
                ImGui::SetCursorPosY(340);

                // 保存原始按钮颜色
                ImVec4 btn_color = ImGui::GetStyle().Colors[ImGuiCol_Button];
                ImVec4 btn_hovered_color = ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered];
                ImVec4 btn_active_color = ImGui::GetStyle().Colors[ImGuiCol_ButtonActive];

                // 设置取消按钮颜色
                ImGui::GetStyle().Colors[ImGuiCol_Button] = ImVec4(0.8f, 0.2f, 0.2f, 1.0f);
                ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered] = ImVec4(0.9f, 0.3f, 0.3f, 1.0f);
                ImGui::GetStyle().Colors[ImGuiCol_ButtonActive] = ImVec4(0.7f, 0.1f, 0.1f, 1.0f);


                ImVec2 buttonSize = ImVec2(120, 40);
                if (ImGui::Button("取消", buttonSize))
                {
                    showActiveWindow = false;
                }
                ImGui::SameLine();


                ImGui::SetCursorPosX((licenseWindowSize.x - 240 - 30) / 2 + 150);
                // 设置应用按钮颜色
                ImGui::GetStyle().Colors[ImGuiCol_Button] = ImVec4(0.2f, 0.8f, 0.2f, 1.0f);
                ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered] = ImVec4(0.3f, 0.9f, 0.3f, 1.0f);
                ImGui::GetStyle().Colors[ImGuiCol_ButtonActive] = ImVec4(0.1f, 0.7f, 0.1f, 1.0f);
                if (ImGui::Button("确认", buttonSize))
                {
                    if (RenewByLicense(text)!=0)
                    {
                        licenseError = "授权码错误，请检查...";
                    }
                    else {
                        done = true;
                    }
                }

                // 恢复按钮颜色
                ImGui::GetStyle().Colors[ImGuiCol_Button] = btn_color;
                ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered] = btn_hovered_color;
                ImGui::GetStyle().Colors[ImGuiCol_ButtonActive] = btn_active_color;

                ImGui::End();
            }
        }

        // Rendering
        ImGui::Render();
        const float clear_color_with_alpha[4] = {clear_color.x * clear_color.w, clear_color.y * clear_color.w, clear_color.z * clear_color.w, clear_color.w};
        g_pd3dDeviceContext->OMSetRenderTargets(1, &g_mainRenderTargetView, nullptr);
        g_pd3dDeviceContext->ClearRenderTargetView(g_mainRenderTargetView, clear_color_with_alpha);
        ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());

        g_pSwapChain->Present(1, 0); // Present with vsync
        // g_pSwapChain->Present(0, 0); // Present without vsync

        auto frameEnd = std::chrono::steady_clock::now();
        auto frameElapsed = std::chrono::duration_cast<std::chrono::milliseconds>(frameEnd - frameStart);
        if(frameElapsed < frameDuration){
            std::this_thread::sleep_for(frameDuration - frameElapsed);
        }
    }

    // Cleanup
    if (g_pWatermarkTexture) {
        g_pWatermarkTexture->Release();
        g_pWatermarkTexture = nullptr;
    }
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    CleanupDeviceD3D();
    ::DestroyWindow(hwnd);
    ::UnregisterClassW(wc.lpszClassName, wc.hInstance);

    return 0;
}

// Helper functions

bool CreateDeviceD3D(HWND hWnd)
{
    // Setup swap chain
    DXGI_SWAP_CHAIN_DESC sd;
    ZeroMemory(&sd, sizeof(sd));
    sd.BufferCount = 2;
    sd.BufferDesc.Width = 0;
    sd.BufferDesc.Height = 0;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferDesc.RefreshRate.Numerator = 60;
    sd.BufferDesc.RefreshRate.Denominator = 1;
    sd.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hWnd;
    sd.SampleDesc.Count = 1;
    sd.SampleDesc.Quality = 0;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    UINT createDeviceFlags = 0;
    // createDeviceFlags |= D3D11_CREATE_DEVICE_DEBUG;
    D3D_FEATURE_LEVEL featureLevel;
    const D3D_FEATURE_LEVEL featureLevelArray[2] = {
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_0,
    };
    HRESULT res = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, createDeviceFlags, featureLevelArray, 2, D3D11_SDK_VERSION, &sd, &g_pSwapChain, &g_pd3dDevice, &featureLevel, &g_pd3dDeviceContext);
    if (res == DXGI_ERROR_UNSUPPORTED) // Try high-performance WARP software driver if hardware is not available.
        res = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, createDeviceFlags, featureLevelArray, 2, D3D11_SDK_VERSION, &sd, &g_pSwapChain, &g_pd3dDevice, &featureLevel, &g_pd3dDeviceContext);
    if (res != S_OK)
        return false;

    CreateRenderTarget();
    return true;
}

void CleanupDeviceD3D()
{
    CleanupRenderTarget();
    if (g_pSwapChain)
    {
        g_pSwapChain->Release();
        g_pSwapChain = nullptr;
    }
    if (g_pd3dDeviceContext)
    {
        g_pd3dDeviceContext->Release();
        g_pd3dDeviceContext = nullptr;
    }
    if (g_pd3dDevice)
    {
        g_pd3dDevice->Release();
        g_pd3dDevice = nullptr;
    }
}

void CreateRenderTarget()
{
    ID3D11Texture2D *pBackBuffer;
    g_pSwapChain->GetBuffer(0, IID_PPV_ARGS(&pBackBuffer));
    g_pd3dDevice->CreateRenderTargetView(pBackBuffer, nullptr, &g_mainRenderTargetView);
    pBackBuffer->Release();
}

void CleanupRenderTarget()
{
    if (g_mainRenderTargetView)
    {
        g_mainRenderTargetView->Release();
        g_mainRenderTargetView = nullptr;
    }
}

// Forward declare message handler from imgui_impl_win32.cpp
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

// Win32 message handler
// You can read the io.WantCaptureMouse, io.WantCaptureKeyboard flags to tell if dear imgui wants to use your inputs.
// - When io.WantCaptureMouse is true, do not dispatch mouse input data to your main application, or clear/overwrite your copy of the mouse data.
// - When io.WantCaptureKeyboard is true, do not dispatch keyboard input data to your main application, or clear/overwrite your copy of the keyboard data.
// Generally you may always pass all inputs to dear imgui, and hide them from your application based on those two flags.
LRESULT WINAPI WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam))
        return true;

    switch (msg)
    {
    case WM_SIZE:
        if (wParam == SIZE_MINIMIZED)
            return 0;
        g_ResizeWidth = (UINT)LOWORD(lParam); // Queue resize
        g_ResizeHeight = (UINT)HIWORD(lParam);
        return 0;
    case WM_SYSCOMMAND:
        if ((wParam & 0xfff0) == SC_KEYMENU) // Disable ALT application menu
            return 0;
        break;
    case WM_DESTROY:
        ::PostQuitMessage(0);
        return 0;
    }
    return ::DefWindowProcW(hWnd, msg, wParam, lParam);
}
