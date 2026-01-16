# PE Import Table 注入技术文档

## 概述

LicHper_Injector 通过修改 PE 文件的 Import Table 实现持久化 DLL 注入。程序启动时 Windows Loader 会自动加载注入的 DLL。

## 核心原理

### 注入策略：新建 Section

采用**新建 Section**方式而非扩展现有 Section，原因：
1. 避免覆盖现有 Section 数据（如 .rdata 中的 C++ 符号）
2. 避免 RVA 计算复杂性
3. 与成熟注入工具保持一致

### Section 布局

```
.zdlla Section:
┌─────────────────────────────────────┐
│ Import Table (原有 IID + 新 IID)    │  ← Import Directory 指向这里
├─────────────────────────────────────┤
│ DLL Name ("lichper_inject.dll")     │
├─────────────────────────────────────┤
│ IMAGE_IMPORT_BY_NAME (Hint + Name)  │
├─────────────────────────────────────┤
│ INT (Import Name Table) - 2 thunks  │
├─────────────────────────────────────┤
│ IAT (Import Address Table) - 2 thunks│
└─────────────────────────────────────┘
```

## 关键修复记录

### 问题：UE 程序启动报 "找不到 .DLL"

**根本原因**：Import Table 空间计算错误

```cpp
// 错误代码
writeOffset += oldTableSize;  // 只跳过旧表大小

// 正确代码  
writeOffset += newTableSize;  // 跳过新表大小（包含新 IID + null terminator）
```

**详细分析**：
- `oldTableSize = (N + 1) * 20` (N 个原有 DLL + null terminator)
- `newTableSize = (N + 2) * 20` (N 个原有 + 1 个新增 + null terminator)
- DLL 名称写入位置 = Section起始 + newTableSize
- 若使用 oldTableSize，DLL 名称会被后续的 `memset(&newTable[N+1], 0, ...)` 覆盖

### 修复前后对比

| 项目 | 修复前 | 修复后 |
|------|--------|--------|
| DLL名称位置 | 与 null terminator 重叠 | 在 null terminator 之后 |
| memset 操作 | 覆盖 DLL 名称 | 不影响 DLL 名称 |
| 程序启动 | 失败 | 成功 |

## 实现要点

### 1. Section Header 空间检查
```cpp
DWORD sectionTableEnd = (BYTE*)&sections[numSections] - fileData.data();
if (sectionTableEnd + sizeof(IMAGE_SECTION_HEADER) > sections[0].PointerToRawData) {
    // 无空间添加新 section header
}
```

### 2. Section 对齐
```cpp
// Virtual Address 按 SectionAlignment 对齐
newSection->VirtualAddress = lastSectionEndVA;  // 已对齐

// Raw Size 按 FileAlignment 对齐
DWORD alignedSize = (dataSize + fileAlignment - 1) & ~(fileAlignment - 1);
```

### 3. Section 权限
```cpp
newSection->Characteristics = 
    IMAGE_SCN_CNT_INITIALIZED_DATA |  // 包含初始化数据
    IMAGE_SCN_MEM_READ |              // 可读
    IMAGE_SCN_MEM_WRITE |             // 可写（IAT 需要）
    IMAGE_SCN_MEM_EXECUTE;            // 可执行（可选）
```

### 4. 清理 Bound Import
```cpp
// 必须清理，否则 loader 会使用过期的绑定地址
ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BOUND_IMPORT].VirtualAddress = 0;
ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BOUND_IMPORT].Size = 0;
```

## 兼容性

| 程序类型 | 状态 | 说明 |
|----------|------|------|
| Unity (IL2CPP) | ✓ | 完全兼容 |
| Unreal Engine 5 | ✓ | 需使用新建 Section 方式 |
| 普通 Win32/x64 | ✓ | 完全兼容 |

## 依赖文件

注入后目标目录需要以下文件：
- `lichper_inject.dll` - 注入 DLL
- `LicHper.dll` - 核心授权库
- `minhook.x64.dll` - Hook 库（x64）

## 调试技巧

1. 使用 PE 工具（如 CFF Explorer）检查注入后的 Import Table
2. 验证新 Section 的 VA、RawPtr、Size 是否正确
3. 确认 Import Directory 指向新 Section
4. 检查 DLL 名称字符串是否完整（非空、非乱码）
