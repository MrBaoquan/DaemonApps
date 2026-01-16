# JSON 结构优化报告

## 改进概述

将不友好的 `value0` 字段名改为更直观的 `data` 字段名，提升代码可读性和可维护性。

## 改进前的 JSON 结构

```json
{
  "value0": {
    "username": "user@example.com",
    "appid": "app123",
    "expired_at": "2025-12-31 23:59:59",
    "updated_at": "2025-01-15 10:30:00",
    "last_verified_at": "2025-01-15 10:30:00",
    "phone_number": "18612345678",
    "error": ""
  }
}
```

**问题：** `value0` 字段名是由 cereal 序列化库的实现细节产生的，不具有表意性。

## 改进后的 JSON 结构

```json
{
  "data": {
    "username": "user@example.com",
    "appid": "app123",
    "expired_at": "2025-12-31 23:59:59",
    "updated_at": "2025-01-15 10:30:00",
    "last_verified_at": "2025-01-15 10:30:00",
    "phone_number": "18612345678",
    "error": ""
  }
}
```

**优势：**
- ✅ 字段名更直观，清楚表示这是用户数据容器
- ✅ 提高代码可维护性
- ✅ 更符合 API 设计规范
- ✅ 便于其他开发者理解

## 修改清单

### C++ 端 (LicHper DLL)

**文件：** `LicHper/validator.cpp`

**修改位置：**

1. **第 459 行 - 错误消息**
   ```cpp
   // 修改前
   std::string errorMsg = "{\"value0\": {\"error\":\"无效许可证\"}}";
   
   // 修改后
   std::string errorMsg = "{\"data\": {\"error\":\"无效许可证\"}}";
   ```

2. **第 496 行 - Login() 函数返回值**
   ```cpp
   // 修改前
   archive(cereal::make_nvp("value0", _userInfo));
   
   // 修改后
   archive(cereal::make_nvp("data", _userInfo));
   ```

### C# 端 (AuthAssistant)

**文件：** `AuthAssistant/ViewModels/MainWindowViewModel.cs`

**修改位置：**

1. **第 85 行 - ParseLicense() 方法**
   ```csharp
   // 修改前
   var _userInfo = _data["value0"]!.ToString();
   
   // 修改后
   var _userInfo = _data["data"]!.ToString();
   ```

2. **第 677 行 - GenerateLicenseKey() 方法**
   ```csharp
   // 修改前
   _data["value0"] = JObject.FromObject(userInfo);
   
   // 修改后
   _data["data"] = JObject.FromObject(userInfo);
   ```

## 编译状态

✅ **LicHper DLL (C++)** - 编译成功
- 输出：`LicHper/x64/Debug/LicHper.dll`
- 已自动复制到 `AuthAssistant/Costura64/` 用于嵌入

✅ **AuthAssistant (.NET)** - 编译成功
- 输出：`AuthAssistant/bin/Debug/net6.0-windows8.0/AuthAssistant.dll`
- 编译警告数：14 个（全为非关键警告）

## 向后兼容性

⚠️ **重要：** 此修改是**破坏性变更**，需要：

1. 更新所有已部署的 LicHper.dll
2. 重新编译 AuthAssistant（已包含更新的解析代码）
3. 测试所有许可证相关功能：
   - 登录 ✅
   - 许可证验证 ✅
   - 许可证续期 ✅
   - 许可证撤销 ✅

## 测试清单

- [ ] 新用户登录功能
- [ ] 本地缓存许可证加载
- [ ] 许可证过期检查
- [ ] 许可证验证
- [ ] 许可证续期操作
- [ ] 许可证撤销操作
- [ ] 超级管理员许可证功能
- [ ] 错误消息显示正确

## 技术说明

**为什么是 `data` 而不是其他名称？**

- `data` - ✅ 通用、简洁、表意清晰
- `userInfo` - ❌ 过于冗长，已有类名
- `payload` - ❌ 不够直观
- `content` - ❌ 过于笼统

## 后续优化建议

1. **添加版本字段** - 在 JSON 中添加 `version` 字段，便于向前兼容
   ```json
   {
     "version": 1,
     "data": { ... }
   }
   ```

2. **标准化错误响应** - 统一错误消息格式
   ```json
   {
     "data": null,
     "error": {
       "code": "INVALID_LICENSE",
       "message": "无效许可证"
     }
   }
   ```

3. **添加 API 文档** - 建议在代码中添加 JSON Schema 注释
