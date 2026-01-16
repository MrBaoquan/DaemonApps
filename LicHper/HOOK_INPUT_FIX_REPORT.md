# Hook 模式文本输入问题诊断报告

## 问题描述
在 Hook 模式下，无授权水印弹出的授权码输入窗口无法输入/粘贴文本，但按钮可以正常点击。

---

## 根本原因分析

### 关键代码位置
[HookRenderer.cpp#L455-L467](HookRenderer.cpp#L455-L467)

```cpp
LRESULT CALLBACK HookRenderer::GetMsgHookProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode >= 0 && s_instance && wParam == PM_REMOVE) {
        MSG* pMsg = reinterpret_cast<MSG*>(lParam);
        
        // 将消息转发给 ImGui
        if (pMsg->hwnd == s_instance->m_hwndTarget || 
            pMsg->hwnd == nullptr ||
            GetParent(pMsg->hwnd) == s_instance->m_hwndTarget) {
            
            ImGui_ImplWin32_WndProcHandler(pMsg->hwnd, pMsg->message, pMsg->wParam, pMsg->lParam);
        }
    }
    
    return CallNextHookEx(s_instance ? s_instance->m_hGetMsgHook : nullptr, nCode, wParam, lParam);
}
```

### 问题分析

#### 问题 1: 关键消息类型被阻止
**GetMessage Hook 的行为**：
- 在 `PM_REMOVE` 时才转发消息
- 但 GetMessage Hook 获取的是消息队列中的消息，**不会自动转换为 WM_CHAR**
- Windows 的消息转换管道（`TranslateMessage`）发生在**应用程序消息循环**中，不在 Hook 中

**缺失的消息转换**：
```cpp
// ✗ Hook 中只能看到 WM_KEYDOWN/WM_KEYUP
// ✓ 需要 TranslateMessage 转换为 WM_CHAR（文本输入）
TranslateMessage(&msg);  // 这在应用程序循环中，Hook 看不到结果
DispatchMessage(&msg);
```

#### 问题 2: InputTextMultiline 依赖 WM_CHAR
ImGui 的 `InputTextMultiline` 使用 `ImGuiInputTextFlags_None`（默认），它依赖：
- `WM_CHAR` - 获取实际的文本字符
- `WM_KEYDOWN/WM_KEYUP` - 获取修饰键和特殊键

**流程对比**：

**Overlay 模式（正常工作）**：
```
应用程序消息循环
    ↓
GetMessage() → WM_KEYDOWN "A"
    ↓
TranslateMessage() → 转换为 WM_CHAR 65
    ↓
WM_CHAR → ImGui 接收 → 文本框输入字符
```

**Hook 模式（失败）**：
```
GetMessage Hook 
    ↓
WM_KEYDOWN → ImGui_ImplWin32_WndProcHandler()
    ↓
但 TranslateMessage 在主循环中，Hook 无法预知转换结果
    ↓
WM_CHAR 消息 → ImGui 不会收到（Hook 不再拦截，或消息时序混乱）
    ↓
结果：无法输入文本
```

#### 问题 3: 消息顺序和时序问题
GetMessage Hook 和应用主循环的消息处理可能错开：
1. Hook 拦截消息转发给 ImGui
2. 同时消息继续在队列中
3. 应用主循环处理消息时再次转发
4. 导致时序混乱，WM_CHAR 丢失或重复

---

## 为什么按钮可以点击？

**鼠标输入工作正常**：
- `WM_LBUTTONDOWN`, `WM_LBUTTONUP` 消息直接被 ImGui 处理
- 无需 `TranslateMessage` 转换
- 坐标计算直接有效

**文本输入失败**：
- 需要 `WM_CHAR` 消息（由 `TranslateMessage` 生成）
- Hook 无法看到这些转换后的消息
- 导致 ImGui 的 InputText 无法接收字符

---

## 解决方案

### 方案 1: 使用 WH_KEYBOARD Hook（推荐）

在 Hook 中直接转换 WM_KEYDOWN 为文本字符：

```cpp
// HookRenderer.h 中添加
private:
    static LRESULT CALLBACK KeyboardHookProc(int nCode, WPARAM wParam, LPARAM lParam);
    HHOOK m_hKeyboardHook = nullptr;
```

```cpp
// HookRenderer.cpp 中实现

void HookRenderer::InstallInputHook() {
    if (m_inputHookInstalled) return;
    
    // 使用 WH_KEYBOARD Hook 而非 WH_GETMESSAGE
    m_hKeyboardHook = SetWindowsHookExA(
        WH_KEYBOARD,          // Hook 键盘事件
        KeyboardHookProc, 
        nullptr, 
        GetCurrentThreadId()
    );
    
    if (m_hKeyboardHook) {
        m_inputHookInstalled = true;
        LOG_INFO("HookRenderer: Keyboard hook installed successfully");
    } else {
        LOG_ERROR("HookRenderer: Failed to install keyboard hook, error: {}", GetLastError());
    }
}

LRESULT CALLBACK HookRenderer::KeyboardHookProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode < 0) {
        return CallNextHookEx(s_instance ? s_instance->m_hKeyboardHook : nullptr, nCode, wParam, lParam);
    }
    
    LPKBDLLHOOKSTRUCT p = reinterpret_cast<LPKBDLLHOOKSTRUCT>(lParam);
    
    if (s_instance && s_instance->m_hwndTarget) {
        // 构造虚拟消息并转发给 ImGui
        switch (wParam) {
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN: {
                ImGuiIO& io = ImGui::GetIO();
                int vkey = p->vkCode;
                
                // 更新按键状态
                if (vkey < 256) {
                    io.KeysDown[vkey] = true;
                }
                
                // 对于可打印字符，模拟 WM_CHAR
                if (p->scanCode < 256) {
                    // 使用 MapVirtualKey 和 ToAscii 转换
                    byte keyState[256] = {};
                    wchar_t outChar[4] = {};
                    int result = ToUnicodeEx(
                        vkey, 
                        p->scanCode, 
                        keyState, 
                        outChar, 
                        4, 
                        0,
                        GetKeyboardLayout(0)
                    );
                    
                    if (result > 0) {
                        // 将字符添加到 ImGui 输入
                        io.AddInputCharacter((unsigned int)outChar[0]);
                    }
                }
                break;
            }
            
            case WM_KEYUP:
            case WM_SYSKEYUP: {
                ImGuiIO& io = ImGui::GetIO();
                int vkey = p->vkCode;
                if (vkey < 256) {
                    io.KeysDown[vkey] = false;
                }
                break;
            }
        }
    }
    
    return CallNextHookEx(
        s_instance ? s_instance->m_hKeyboardHook : nullptr, 
        nCode, 
        wParam, 
        lParam
    );
}
```

### 方案 2: 增强 GetMessage Hook（备选）

改进消息处理，确保 WM_CHAR 被正确转发：

```cpp
LRESULT CALLBACK HookRenderer::GetMsgHookProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode >= 0 && s_instance && wParam == PM_REMOVE) {
        MSG* pMsg = reinterpret_cast<MSG*>(lParam);
        
        // 检查消息是否属于目标窗口
        bool isTargetWindow = (pMsg->hwnd == s_instance->m_hwndTarget || 
                              pMsg->hwnd == nullptr ||
                              GetParent(pMsg->hwnd) == s_instance->m_hwndTarget);
        
        if (isTargetWindow) {
            // ✓ 关键改进：处理所有输入相关消息
            switch (pMsg->message) {
                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_CHAR:
                case WM_SYSKEYDOWN:
                case WM_SYSKEYUP:
                case WM_SYSCHAR:
                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                case WM_MOUSEMOVE:
                case WM_MOUSEWHEEL:
                case WM_MOUSEHWHEEL:
                    // 转发所有这些消息到 ImGui
                    ImGui_ImplWin32_WndProcHandler(
                        pMsg->hwnd, 
                        pMsg->message, 
                        pMsg->wParam, 
                        pMsg->lParam
                    );
                    break;
            }
        }
    }
    
    return CallNextHookEx(
        s_instance ? s_instance->m_hGetMsgHook : nullptr, 
        nCode, 
        wParam, 
        lParam
    );
}
```

**问题**：此方案仍可能有时序问题，因为 WM_CHAR 的生成取决于 TranslateMessage 调用时机。

---

## 推荐修复步骤

### Step 1: 替换 GetMessage Hook 为 Keyboard Hook

编辑 [HookRenderer.cpp#L424-L435](HookRenderer.cpp#L424-L435)：

```cpp
void HookRenderer::InstallInputHook() {
    if (m_inputHookInstalled) return;
    
    // 改为 WH_KEYBOARD Hook
    m_hKeyboardHook = SetWindowsHookExA(
        WH_KEYBOARD,
        KeyboardHookProc, 
        nullptr, 
        GetCurrentThreadId()
    );
    
    if (m_hKeyboardHook) {
        m_inputHookInstalled = true;
        LOG_INFO("HookRenderer: Keyboard hook installed successfully");
    } else {
        LOG_ERROR("HookRenderer: Failed to install keyboard hook, error: {}", GetLastError());
    }
}
```

### Step 2: 更新 Keyboard Hook 实现

替换 [HookRenderer.cpp#L444-L453](HookRenderer.cpp#L444-L453) 的 `KeyboardHookProc`：

（见方案 1 中的完整实现）

### Step 3: 卸载 GetMessage Hook

编辑 [HookRenderer.cpp#L436-L442](HookRenderer.cpp#L436-L442)：

```cpp
void HookRenderer::UninstallInputHook() {
    if (!m_inputHookInstalled) return;
    
    if (m_hKeyboardHook) {
        UnhookWindowsHookEx(m_hKeyboardHook);
        m_hKeyboardHook = nullptr;
    }
    
    // 移除以下代码（不再使用 GetMessage Hook）
    // if (m_hGetMsgHook) {
    //     UnhookWindowsHookEx(m_hGetMsgHook);
    //     m_hGetMsgHook = nullptr;
    // }
    
    m_inputHookInstalled = false;
    LOG_INFO("HookRenderer: Input hooks uninstalled");
}
```

### Step 4: 移除不使用的 GetMessage Hook

编辑 [HookRenderer.h#L78-82](HookRenderer.h#L78-82)，移除：

```cpp
// 删除这些成员（改用 Keyboard Hook）
// HHOOK m_hGetMsgHook = nullptr;
```

---

## 验证修复

修复后，测试以下功能：

✅ **文本输入**
```
点击输入框 → 输入 "test123" → 应显示在框内
```

✅ **复制粘贴**
```
复制 "ABC123XYZ"
右键粘贴或 Ctrl+V → 应显示在框内
```

✅ **特殊键**
```
Backspace, Delete, Home, End → 应正常工作
Ctrl+A, Ctrl+C, Ctrl+V → 应正常工作
```

✅ **按钮点击**
```
鼠标点击"取消"、"确认"按钮 → 应正常响应
```

---

## 潜在副作用和对策

| 问题 | 原因 | 对策 |
|------|------|------|
| 其他应用键盘输入受影响 | Hook 在所有应用中生效 | 仅在特定窗口转发消息，其他窗口直接 CallNextHookEx |
| IME (输入法) 冲突 | Hook 干扰 IME 消息 | 改用 WH_KEYBOARD_LL (低级 Hook)，但需提升权限 |
| 性能下降 | Hook 频繁调用 | Hook 内逻辑应尽可能简洁，避免复杂计算 |

---

## 参考链接

- [WH_KEYBOARD Hook - MSDN](https://docs.microsoft.com/en-us/windows/win32/winmsg/wh-keyboard)
- [WM_CHAR Message - MSDN](https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-char)
- [ToUnicodeEx Function - MSDN](https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-tounicodeex)
- [ImGui Win32 Implementation](https://github.com/ocornut/imgui/blob/master/backends/imgui_impl_win32.cpp)

---

## 附录：完整修复代码

（待在下一步实现）

