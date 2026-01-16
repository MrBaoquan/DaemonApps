# Hook 输入问题 - 深度诊断与修复

**日期**：2026-01-15  
**状态**：✅ 关键错误已修复  
**编译**：成功

---

## 🔴 发现的关键问题

### 问题：Hook 类型与参数结构不匹配

**严重性**：致命错误 ⚠️

#### 错误代码：
```cpp
// ❌ 错误：使用 WH_KEYBOARD 但参数按 WH_KEYBOARD_LL 处理
m_hKeyboardHook = SetWindowsHookExA(WH_KEYBOARD, KeyboardHookProc, 
                                   nullptr, GetCurrentThreadId());

LRESULT CALLBACK HookRenderer::KeyboardHookProc(int nCode, WPARAM wParam, LPARAM lParam) {
    // ❌ 这是 WH_KEYBOARD_LL 的结构！
    LPKBDLLHOOKSTRUCT p = reinterpret_cast<LPKBDLLHOOKSTRUCT>(lParam);
    int vkey = p->vkCode;  // 完全错误的数据！
}
```

#### 为什么这是致命错误？

| Hook 类型 | wParam | lParam |
|-----------|--------|--------|
| **WH_KEYBOARD** | 虚拟键码 (VK_A, VK_SPACE 等) | 键盘状态标志位（扫描码、扩展键等） |
| **WH_KEYBOARD_LL** | 消息类型 (WM_KEYDOWN, WM_KEYUP) | **指向 KBDLLHOOKSTRUCT 结构的指针** |

**我的代码混用了两者**：
- 安装了 `WH_KEYBOARD`（线程级钩子）
- 但参数处理按 `WH_KEYBOARD_LL`（系统级钩子）
- 结果：lParam 被错误解释为结构指针 → 访问无效内存 → 输入完全失败

---

## ✅ 修复方案

### 方案：统一使用 WH_KEYBOARD_LL

**原因**：
1. `WH_KEYBOARD_LL` 是系统级钩子，更底层、更可靠
2. 参数结构 `KBDLLHOOKSTRUCT` 包含完整的键盘事件信息
3. 不依赖线程上下文，更适合 DLL 注入场景

#### 修复 1：安装正确的 Hook 类型

```cpp
// ✅ 正确
m_hKeyboardHook = SetWindowsHookExA(
    WH_KEYBOARD_LL,        // ← 低级键盘钩子
    KeyboardHookProc, 
    nullptr,               // hMod (系统级钩子传 nullptr)
    0                      // ← 系统级钩子，线程 ID 必须是 0
);
```

#### 修复 2：正确检查 nCode

```cpp
// ✅ WH_KEYBOARD_LL 使用 HC_ACTION
if (nCode < 0 || nCode != HC_ACTION) {
    return CallNextHookEx(nullptr, nCode, wParam, lParam);
}
```

#### 修复 3：CallNextHookEx 参数

```cpp
// ✅ 低级钩子的第一个参数必须是 nullptr
return CallNextHookEx(nullptr, nCode, wParam, lParam);

// ❌ 错误（之前的代码）
return CallNextHookEx(s_instance->m_hKeyboardHook, ...);
```

#### 修复 4：移除错误的消息泵

```cpp
// ❌ 删除了这段（在 Hook 模式下不需要）
MSG msg;
while (PeekMessageA(&msg, m_hwndTarget, 0, 0, PM_REMOVE)) {
    ImGui_ImplWin32_WndProcHandler(...);
    TranslateMessage(&msg);
    DispatchMessageA(&msg);
}

// ✅ Hook 模式下，输入完全通过 KeyboardHookProc 处理
```

**原因**：在 Hook 模式下：
- 键盘事件已被 `WH_KEYBOARD_LL` 拦截
- 不应该从宿主窗口的消息队列获取消息
- 那些消息是应用自己的，不是我们的

---

## 📊 技术对比

### WH_KEYBOARD vs WH_KEYBOARD_LL

| 特性 | WH_KEYBOARD | WH_KEYBOARD_LL |
|------|-------------|----------------|
| **级别** | 线程级 | 系统级 |
| **作用域** | 仅当前线程 | 整个系统 |
| **参数** | wParam=虚拟键码<br>lParam=状态位 | wParam=消息类型<br>lParam=KBDLLHOOKSTRUCT* |
| **性能** | 快 | 稍慢（跨进程） |
| **可靠性** | 依赖线程 | 更可靠 |
| **适用场景** | 单进程单线程 | DLL 注入、全局钩子 |

**选择 WH_KEYBOARD_LL 的原因**：
1. ✅ 我们的代码已经按 KBDLLHOOKSTRUCT 处理
2. ✅ DLL 注入场景，系统级钩子更合适
3. ✅ 更稳定，不受线程影响

---

## 🔧 修改清单

### 文件：HookRenderer.cpp

| 位置 | 修改 | 原因 |
|------|------|------|
| `InstallInputHook()` | `WH_KEYBOARD` → `WH_KEYBOARD_LL` | 匹配参数结构 |
| `InstallInputHook()` | 线程 ID → 0 | 系统级钩子要求 |
| `KeyboardHookProc()` | 添加 `nCode != HC_ACTION` 检查 | 低级钩子要求 |
| `KeyboardHookProc()` | `LPKBDLLHOOKSTRUCT` → `KBDLLHOOKSTRUCT*` | 正确类型 |
| `KeyboardHookProc()` | `CallNextHookEx(nullptr, ...)` | 低级钩子要求 |
| `ProcessInput()` | 删除消息泵 | Hook 模式不需要 |

---

## ✔️ 编译验证

```
时间：2026-01-15 10:xx:xx
结果：成功
警告：0
错误：0
```

✅ DLL 已更新到 4 个位置

---

## 🧪 预期行为

### 修复后应该：

1. ✅ Hook 成功安装（日志：`"Keyboard hook installed successfully"`）
2. ✅ 捕获所有键盘事件（包括 Backspace, Delete 等）
3. ✅ 正确转换字符（使用 `ToUnicodeEx`）
4. ✅ 修饰键状态正确（Ctrl, Shift, Alt）
5. ✅ 剪贴板通过 ImGui 回调处理（之前已设置）

### 测试步骤：

```
1. 运行无授权的 D3D11 应用
2. 水印和授权窗口出现
3. 点击输入框
4. 输入 "hello" → 应该显示
5. 按 Backspace → 删除字符
6. Ctrl+V 粘贴 → 应该工作
```

---

## 🔍 为什么之前的修复无效？

### 第一次尝试（失败）：

```cpp
// ❌ 安装了 WH_KEYBOARD
m_hKeyboardHook = SetWindowsHookExA(WH_KEYBOARD, ...);

// ❌ 但代码按 WH_KEYBOARD_LL 处理
LPKBDLLHOOKSTRUCT p = reinterpret_cast<LPKBDLLHOOKSTRUCT>(lParam);
```

**结果**：
- lParam 实际上是一个 32 位整数（键盘状态标志）
- 被强制转换为指针并解引用 → 访问随机内存地址
- p->vkCode 读取到垃圾数据
- 输入完全不工作

### 现在（修复）：

```cpp
// ✅ 安装 WH_KEYBOARD_LL
m_hKeyboardHook = SetWindowsHookExA(WH_KEYBOARD_LL, ...);

// ✅ 正确解释 lParam
KBDLLHOOKSTRUCT* p = reinterpret_cast<KBDLLHOOKSTRUCT*>(lParam);
```

**结果**：
- lParam 真的是指向 KBDLLHOOKSTRUCT 的指针
- p->vkCode 读取正确的虚拟键码
- 输入应该正常工作

---

## 📝 总结

### 根本原因
**Hook 类型与参数处理不匹配** - 这是一个编程错误，导致内存访问错误。

### 修复措施
1. ✅ 将 Hook 类型从 `WH_KEYBOARD` 改为 `WH_KEYBOARD_LL`
2. ✅ 修正所有相关的参数处理代码
3. ✅ 删除不需要的消息泵（Hook 模式）
4. ✅ 保持剪贴板回调（ImGui 层面）

### 下一步
- [ ] 在实际应用中测试
- [ ] 验证所有输入功能（文本、删除、粘贴）
- [ ] 检查日志确认 Hook 安装成功

