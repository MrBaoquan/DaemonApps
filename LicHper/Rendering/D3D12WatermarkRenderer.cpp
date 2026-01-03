#include "D3D12WatermarkRenderer.h"
#include "WatermarkConfig.h"
#include "Logger.h"
#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx12.h"
#include <dxgi1_4.h>

// 外部声明
extern std::string g_appID;
std::string GetUserFolder();

namespace LicHper {

D3D12WatermarkRenderer::D3D12WatermarkRenderer() {
    m_pConfig = new WatermarkConfig();
}

D3D12WatermarkRenderer::~D3D12WatermarkRenderer() {
    Shutdown();
    delete m_pConfig;
}

bool D3D12WatermarkRenderer::Initialize(IDXGISwapChain* pSwapChain, ID3D12Device* pDevice, 
    ID3D12CommandQueue* pCommandQueue, HWND hWnd) {
    
    if (m_initialized) return true;
    
    if (!pSwapChain || !pDevice || !pCommandQueue) {
        LOG_ERROR("D3D12WatermarkRenderer: Invalid parameters");
        return false;
    }
    
    m_pDevice = pDevice;
    m_pCommandQueue = pCommandQueue;
    m_hWnd = hWnd;
    
    LOG_INFO("D3D12WatermarkRenderer: Initializing...");
    
    // 获取 SwapChain 信息
    DXGI_SWAP_CHAIN_DESC desc;
    pSwapChain->GetDesc(&desc);
    m_backBufferCount = desc.BufferCount;
    if (m_backBufferCount > MAX_BACK_BUFFERS) {
        m_backBufferCount = MAX_BACK_BUFFERS;
    }
    m_rtvFormat = desc.BufferDesc.Format;
    
    LOG_INFO("D3D12WatermarkRenderer: SwapChain buffer count: {}, format: {}", 
        m_backBufferCount, (int)m_rtvFormat);
    
    // 创建描述符堆
    if (!CreateDescriptorHeaps()) {
        LOG_ERROR("D3D12WatermarkRenderer: Failed to create descriptor heaps");
        return false;
    }
    
    // 创建命令对象
    if (!CreateCommandObjects()) {
        LOG_ERROR("D3D12WatermarkRenderer: Failed to create command objects");
        Shutdown();
        return false;
    }
    
    // 创建渲染目标
    if (!CreateRenderTargets(pSwapChain)) {
        LOG_ERROR("D3D12WatermarkRenderer: Failed to create render targets");
        Shutdown();
        return false;
    }
    
    // 初始化 ImGui
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    io.IniFilename = nullptr;  // 禁用配置文件
    
    ImGui::StyleColorsDark();
    
    // 初始化 ImGui Win32 后端
    ImGui_ImplWin32_Init(m_hWnd);
    
    // 初始化 ImGui D3D12 后端
    D3D12_CPU_DESCRIPTOR_HANDLE srvCpuHandle = m_pSrvHeap->GetCPUDescriptorHandleForHeapStart();
    D3D12_GPU_DESCRIPTOR_HANDLE srvGpuHandle = m_pSrvHeap->GetGPUDescriptorHandleForHeapStart();
    
    if (!ImGui_ImplDX12_Init(m_pDevice, m_backBufferCount, m_rtvFormat, m_pSrvHeap, srvCpuHandle, srvGpuHandle)) {
        LOG_ERROR("D3D12WatermarkRenderer: Failed to initialize ImGui D3D12 backend");
        Shutdown();
        return false;
    }
    
    m_imguiInitialized = true;
    m_initialized = true;
    
    LOG_INFO("D3D12WatermarkRenderer: Initialized successfully");
    return true;
}

void D3D12WatermarkRenderer::Shutdown() {
    LOG_INFO("D3D12WatermarkRenderer: Shutting down...");
    
    WaitForGpu();
    
    // 清理 ImGui
    if (m_imguiInitialized) {
        ImGui_ImplDX12_Shutdown();
        ImGui_ImplWin32_Shutdown();
        ImGui::DestroyContext();
        m_imguiInitialized = false;
    }
    
    // 清理渲染目标
    CleanupRenderTargets();
    
    // 清理 Fence
    if (m_fenceEvent) {
        CloseHandle(m_fenceEvent);
        m_fenceEvent = nullptr;
    }
    if (m_pFence) {
        m_pFence->Release();
        m_pFence = nullptr;
    }
    
    // 清理命令对象
    if (m_pCommandList) {
        m_pCommandList->Release();
        m_pCommandList = nullptr;
    }
    for (UINT i = 0; i < MAX_BACK_BUFFERS; i++) {
        if (m_pCommandAllocators[i]) {
            m_pCommandAllocators[i]->Release();
            m_pCommandAllocators[i] = nullptr;
        }
    }
    
    // 清理描述符堆
    if (m_pSrvHeap) {
        m_pSrvHeap->Release();
        m_pSrvHeap = nullptr;
    }
    if (m_pRtvHeap) {
        m_pRtvHeap->Release();
        m_pRtvHeap = nullptr;
    }
    
    m_pDevice = nullptr;
    m_pCommandQueue = nullptr;
    m_initialized = false;
    
    LOG_INFO("D3D12WatermarkRenderer: Shutdown complete");
}

bool D3D12WatermarkRenderer::CreateDescriptorHeaps() {
    // 创建 RTV 描述符堆
    D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc = {};
    rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    rtvHeapDesc.NumDescriptors = MAX_BACK_BUFFERS;
    rtvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
    
    HRESULT hr = m_pDevice->CreateDescriptorHeap(&rtvHeapDesc, IID_PPV_ARGS(&m_pRtvHeap));
    if (FAILED(hr)) {
        LOG_ERROR("D3D12WatermarkRenderer: Failed to create RTV heap, hr=0x{:X}", (unsigned int)hr);
        return false;
    }
    
    m_rtvDescriptorSize = m_pDevice->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    
    // 创建 SRV 描述符堆（用于 ImGui 字体纹理）
    D3D12_DESCRIPTOR_HEAP_DESC srvHeapDesc = {};
    srvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    srvHeapDesc.NumDescriptors = 64;  // 足够 ImGui 使用
    srvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    
    hr = m_pDevice->CreateDescriptorHeap(&srvHeapDesc, IID_PPV_ARGS(&m_pSrvHeap));
    if (FAILED(hr)) {
        LOG_ERROR("D3D12WatermarkRenderer: Failed to create SRV heap, hr=0x{:X}", (unsigned int)hr);
        return false;
    }
    
    LOG_INFO("D3D12WatermarkRenderer: Descriptor heaps created");
    return true;
}

bool D3D12WatermarkRenderer::CreateCommandObjects() {
    // 创建命令分配器（每个后台缓冲区一个）
    for (UINT i = 0; i < m_backBufferCount; i++) {
        HRESULT hr = m_pDevice->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, 
            IID_PPV_ARGS(&m_pCommandAllocators[i]));
        if (FAILED(hr)) {
            LOG_ERROR("D3D12WatermarkRenderer: Failed to create command allocator {}, hr=0x{:X}", 
                i, (unsigned int)hr);
            return false;
        }
    }
    
    // 创建命令列表
    HRESULT hr = m_pDevice->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
        m_pCommandAllocators[0], nullptr, IID_PPV_ARGS(&m_pCommandList));
    if (FAILED(hr)) {
        LOG_ERROR("D3D12WatermarkRenderer: Failed to create command list, hr=0x{:X}", (unsigned int)hr);
        return false;
    }
    
    // 关闭命令列表（稍后重置使用）
    m_pCommandList->Close();
    
    // 创建 Fence
    hr = m_pDevice->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&m_pFence));
    if (FAILED(hr)) {
        LOG_ERROR("D3D12WatermarkRenderer: Failed to create fence, hr=0x{:X}", (unsigned int)hr);
        return false;
    }
    
    m_fenceEvent = CreateEvent(nullptr, FALSE, FALSE, nullptr);
    if (!m_fenceEvent) {
        LOG_ERROR("D3D12WatermarkRenderer: Failed to create fence event");
        return false;
    }
    
    LOG_INFO("D3D12WatermarkRenderer: Command objects created");
    return true;
}

bool D3D12WatermarkRenderer::CreateRenderTargets(IDXGISwapChain* pSwapChain) {
    D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle = m_pRtvHeap->GetCPUDescriptorHandleForHeapStart();
    
    for (UINT i = 0; i < m_backBufferCount; i++) {
        HRESULT hr = pSwapChain->GetBuffer(i, IID_PPV_ARGS(&m_pRenderTargets[i]));
        if (FAILED(hr)) {
            LOG_ERROR("D3D12WatermarkRenderer: Failed to get back buffer {}, hr=0x{:X}", 
                i, (unsigned int)hr);
            return false;
        }
        
        m_pDevice->CreateRenderTargetView(m_pRenderTargets[i], nullptr, rtvHandle);
        m_rtvHandles[i] = rtvHandle;
        rtvHandle.ptr += m_rtvDescriptorSize;
    }
    
    LOG_INFO("D3D12WatermarkRenderer: Render targets created");
    return true;
}

void D3D12WatermarkRenderer::CleanupRenderTargets() {
    for (UINT i = 0; i < MAX_BACK_BUFFERS; i++) {
        if (m_pRenderTargets[i]) {
            m_pRenderTargets[i]->Release();
            m_pRenderTargets[i] = nullptr;
        }
    }
}

void D3D12WatermarkRenderer::WaitForGpu() {
    if (!m_pCommandQueue || !m_pFence || !m_fenceEvent) return;
    
    m_currentFenceValue++;
    m_pCommandQueue->Signal(m_pFence, m_currentFenceValue);
    
    if (m_pFence->GetCompletedValue() < m_currentFenceValue) {
        m_pFence->SetEventOnCompletion(m_currentFenceValue, m_fenceEvent);
        WaitForSingleObject(m_fenceEvent, INFINITE);
    }
}

void D3D12WatermarkRenderer::UpdateConfig(const WatermarkConfig& config) {
    std::lock_guard<std::mutex> lock(m_configMutex);
    *m_pConfig = config;
}

void D3D12WatermarkRenderer::OnResize(UINT width, UINT height) {
    // SwapChain 大小变化时需要重新创建渲染目标
    // 这由 HookRenderer 在 OnResizeBuffers 回调中处理
}

void D3D12WatermarkRenderer::Render(IDXGISwapChain* pSwapChain) {
    if (!m_initialized || !m_imguiInitialized) return;
    
    // 获取当前后台缓冲区索引
    IDXGISwapChain3* pSwapChain3 = nullptr;
    UINT backBufferIndex = 0;
    if (SUCCEEDED(pSwapChain->QueryInterface(IID_PPV_ARGS(&pSwapChain3)))) {
        backBufferIndex = pSwapChain3->GetCurrentBackBufferIndex();
        pSwapChain3->Release();
    }
    
    if (backBufferIndex >= m_backBufferCount) {
        return;
    }
    
    // 等待之前的帧完成
    UINT64 fenceValue = m_fenceValues[backBufferIndex];
    if (fenceValue != 0 && m_pFence->GetCompletedValue() < fenceValue) {
        m_pFence->SetEventOnCompletion(fenceValue, m_fenceEvent);
        WaitForSingleObject(m_fenceEvent, INFINITE);
    }
    
    // 重置命令分配器和命令列表
    m_pCommandAllocators[backBufferIndex]->Reset();
    m_pCommandList->Reset(m_pCommandAllocators[backBufferIndex], nullptr);
    
    // 资源屏障：PRESENT -> RENDER_TARGET
    D3D12_RESOURCE_BARRIER barrier = {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = m_pRenderTargets[backBufferIndex];
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    m_pCommandList->ResourceBarrier(1, &barrier);
    
    // 设置渲染目标
    m_pCommandList->OMSetRenderTargets(1, &m_rtvHandles[backBufferIndex], FALSE, nullptr);
    
    // 设置描述符堆
    ID3D12DescriptorHeap* ppHeaps[] = { m_pSrvHeap };
    m_pCommandList->SetDescriptorHeaps(1, ppHeaps);
    
    // 开始 ImGui 帧
    ImGui_ImplDX12_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();
    
    // 获取窗口大小
    ImGuiIO& io = ImGui::GetIO();
    float windowWidth = io.DisplaySize.x;
    float windowHeight = io.DisplaySize.y;
    
    // 渲染水印内容
    RenderWatermarkContent(windowWidth, windowHeight);
    
    // 渲染授权窗口
    if (m_showLicenseWindow) {
        if (RenderLicenseWindow(windowWidth, windowHeight)) {
            if (m_exitCallback) {
                m_exitCallback();
            }
        }
    }
    
    // 结束 ImGui 帧
    ImGui::Render();
    ImGui_ImplDX12_RenderDrawData(ImGui::GetDrawData(), m_pCommandList);
    
    // 资源屏障：RENDER_TARGET -> PRESENT
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
    m_pCommandList->ResourceBarrier(1, &barrier);
    
    // 关闭并执行命令列表
    m_pCommandList->Close();
    ID3D12CommandList* ppCommandLists[] = { m_pCommandList };
    m_pCommandQueue->ExecuteCommandLists(1, ppCommandLists);
    
    // 设置 Fence 值
    m_currentFenceValue++;
    m_fenceValues[backBufferIndex] = m_currentFenceValue;
    m_pCommandQueue->Signal(m_pFence, m_currentFenceValue);
}

void D3D12WatermarkRenderer::RenderWatermarkContent(float windowWidth, float windowHeight) {
    std::lock_guard<std::mutex> lock(m_configMutex);
    
    // 设置透明窗口覆盖整个屏幕
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(ImVec2(windowWidth, windowHeight));
    
    ImGuiWindowFlags flags = ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
        ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoScrollbar |
        ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoBackground |
        ImGuiWindowFlags_NoBringToFrontOnFocus;
    
    ImGui::Begin("##Watermark", nullptr, flags);
    
    // 计算已运行时间
    auto now = std::chrono::high_resolution_clock::now();
    auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(now - m_startTime);
    
    // 处理水印文本
    std::string text = m_pConfig->title;
    
    // 替换 {APPID}
    size_t pos = text.find("{APPID}");
    if (pos != std::string::npos) {
        text.replace(pos, 7, m_pConfig->appID);
    }
    
    // 替换 {COUNTDOWN}
    pos = text.find("{COUNTDOWN}");
    if (pos != std::string::npos) {
        int remain = m_pConfig->timeout - (int)elapsed.count();
        if (remain < 0) remain = 0;
        char countdown[32];
        snprintf(countdown, sizeof(countdown), "%02d:%02d:%02d", 
            remain / 3600, (remain % 3600) / 60, remain % 60);
        text.replace(pos, 11, countdown);
    }
    
    // 渲染水印文本
    if (!text.empty()) {
        // 确保颜色可见
        ImVec4 color = m_pConfig->color;
        if (color.w < 0.5f) color.w = 0.5f;
        
        ImU32 imColor = IM_COL32(
            (int)(color.x * 255),
            (int)(color.y * 255),
            (int)(color.z * 255),
            (int)(color.w * 255)
        );
        
        ImVec2 textSize = ImGui::CalcTextSize(text.c_str());
        
        // 简单地在右上角显示
        float posX = windowWidth - textSize.x - 50.0f;
        float posY = 100.0f;
        
        ImDrawList* drawList = ImGui::GetWindowDrawList();
        drawList->AddText(ImVec2(posX, posY), imColor, text.c_str());
    }
    
    ImGui::End();
}

bool D3D12WatermarkRenderer::RenderLicenseWindow(float windowWidth, float windowHeight) {
    bool shouldExit = false;
    
    // 计算窗口大小和位置
    float winWidth = 350.0f;
    float winHeight = 200.0f;
    float winX = (windowWidth - winWidth) / 2.0f;
    float winY = (windowHeight - winHeight) / 2.0f;
    
    ImGui::SetNextWindowPos(ImVec2(winX, winY), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(ImVec2(winWidth, winHeight), ImGuiCond_FirstUseEver);
    
    ImGuiWindowFlags flags = ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoResize;
    
    if (ImGui::Begin("License", &m_showLicenseWindow, flags)) {
        ImGui::TextWrapped("This software is running in trial mode.");
        ImGui::Spacing();
        ImGui::Separator();
        ImGui::Spacing();
        
        std::lock_guard<std::mutex> lock(m_configMutex);
        ImGui::Text("App ID: %s", m_pConfig->appID.c_str());
        
        ImGui::Spacing();
        ImGui::Separator();
        ImGui::Spacing();
        
        // 按钮
        float buttonWidth = 80.0f;
        float spacing = 10.0f;
        float totalWidth = buttonWidth * 2 + spacing;
        float startX = (winWidth - totalWidth) / 2.0f;
        
        ImGui::SetCursorPosX(startX);
        if (ImGui::Button("[=]", ImVec2(buttonWidth, 30))) {
            m_showLicenseWindow = false;
        }
        
        ImGui::SameLine(0, spacing);
        if (ImGui::Button("[X]", ImVec2(buttonWidth, 30))) {
            shouldExit = true;
        }
    }
    ImGui::End();
    
    return shouldExit;
}

} // namespace LicHper
