# DaemonApps Build Commands Reference

Quick commands to rebuild all components with MSBuild.

## Prerequisites

```powershell
# Install Visual Studio 2022 Professional with C++ development tools
# Install Windows 10 SDK (version 10.0.26100.0 or later)
# Navigate to repository root
cd C:\Users\Administrator\source\repos\DaemonApps
```

## Build Commands

### 1. LicHper_Injector (PE Import Table Modifier)

#### x64 Release Build
```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper_Injector\LicHper_Injector.vcxproj' `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /m `
  /v:minimal
```

**Output:** `Binaries\Win64\Release\LicHper_Injector.exe` (80 KB)

#### x86 Release Build
```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper_Injector\LicHper_Injector.vcxproj' `
  /p:Configuration=Release `
  /p:Platform=Win32 `
  /m `
  /v:minimal
```

**Output:** `Binaries\Win32\Release\LicHper_Injector.exe` (69.5 KB)

---

### 2. LicHper_inject (DLL Injection Module)

#### x64 Release Build
```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper_inject\LicHper_inject.vcxproj' `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /m `
  /v:minimal
```

**Output:** `Binaries\Win64\Release\LicHper_inject.dll` (26 KB)

#### x86 Release Build (⚠️ Currently fails - see troubleshooting)
```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper_inject\LicHper_inject.vcxproj' `
  /p:Configuration=Release `
  /p:Platform=Win32 `
  /m `
  /v:minimal
```

**Issue:** Depends on LicHper.dll (x86) which has build issues

---

### 3. LicHper (Watermark Rendering Engine)

#### x64 Release Build
```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper\LicHper.vcxproj' `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /m `
  /v:minimal
```

**Output:** `Binaries\Win64\Release\LicHper.dll` (1,616.5 KB)

#### x86 Release Build (⚠️ Currently fails - see troubleshooting)
```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper\LicHper.vcxproj' `
  /p:Configuration=Release `
  /p:Platform=Win32 `
  /m `
  /v:minimal
```

**Issue:** 
- Missing spdlog library for x86 platform
- Header redefinitions in rendering classes

---

## Batch Build All Components

### x64 Release (Complete)
```powershell
$projects = @(
    'LicHper\LicHper.vcxproj',
    'LicHper_inject\LicHper_inject.vcxproj',
    'LicHper_Injector\LicHper_Injector.vcxproj'
)

$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'

foreach ($project in $projects) {
    Write-Host "Building $project for x64..."
    & $msbuild $project `
        /p:Configuration=Release `
        /p:Platform=x64 `
        /m `
        /v:minimal
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Build failed for $project" -ForegroundColor Red
    } else {
        Write-Host "SUCCESS: $project built" -ForegroundColor Green
    }
}
```

### x86 Release (LicHper_Injector only)
```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper_Injector\LicHper_Injector.vcxproj' `
  /p:Configuration=Release `
  /p:Platform=Win32 `
  /m `
  /v:minimal
```

---

## Verify Builds

### Check x64 Binaries
```powershell
Get-ChildItem 'Binaries\Win64\Release\' `
    -Include 'LicHper*.dll', 'LicHper_Injector.exe' |
    Select-Object Name, @{N='Size(KB)';E={[math]::Round($_.Length/1KB,1)}} |
    Format-Table -AutoSize
```

Expected output:
```
Name                   Size(KB)
----                   --------
LicHper.dll            1616.5
LicHper_inject.dll       26
LicHper_Injector.exe     80
```

### Check x86 Binaries
```powershell
Get-ChildItem 'Binaries\Win32\Release\' `
    -Include 'LicHper_Injector.exe' |
    Select-Object Name, @{N='Size(KB)';E={[math]::Round($_.Length/1KB,1)}} |
    Format-Table -AutoSize
```

Expected output:
```
Name                   Size(KB)
----                   --------
LicHper_Injector.exe    69.5
```

---

## Troubleshooting

### Error: C1083 - Cannot open include file "spdlog/spdlog.h"

**Cause:** x86 (Win32) vcpkg dependencies not installed

**Solution 1: Install x86 libraries**
```bash
cd LicHper/vcpkg
.\vcpkg install spdlog fmt --triplet x86-windows-static
```

Then rebuild with x86 platform.

**Solution 2: Use x64 only (recommended)**
```powershell
# Just build x64 versions
# They provide full functionality
# Deploy only x64 binaries
```

### Error: C2086, C3158, C2575 - Header redefinition errors

**Cause:** Class definitions duplicated across multiple headers

**Why x64 works:** Uses precompiled headers (PCH) that cache first definition

**Why x86 fails:** Different PCH settings or compilation order

**Solution:**
1. Check `LicHper.vcxproj` for PrecompiledHeader settings
2. Ensure Win32 and x64 configurations have identical PCH settings
3. Or refactor headers to avoid redefinitions (separate interface/implementation)

### Error: The system cannot find the path specified

**Cause:** MSBuild path or project path incorrect

**Solution:** Use absolute paths or navigate to project directory first
```powershell
cd C:\Users\Administrator\source\repos\DaemonApps
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' ...
```

### Warning: C4819 - File contains characters not representable

**Cause:** Chinese/multibyte characters in source code with ANSI encoding

**Solution:** File will still compile - warnings are informational

**To suppress:**
1. Save file as UTF-8 with BOM
2. Or add to project: `/wd4819` compiler flag

---

## Output Directories

### x64 Release
- Binaries: `Binaries\Win64\Release\`
- Object files: `LicHper\obj\x64\Release\`
- Intermediate: `..\obj\Win64\Release\`

### x86 Release  
- Binaries: `Binaries\Win32\Release\`
- Object files: `LicHper\obj\Win32\Release\`
- Intermediate: `..\obj\Win32\Release\`

---

## Build Parameters Explained

| Parameter | Value | Meaning |
|-----------|-------|---------|
| `/p:Configuration` | Release | Optimized build (no debug info) |
| `/p:Platform` | x64 or Win32 | Target architecture |
| `/m` | (multiple) | Parallel compilation |
| `/v:minimal` | (verbosity) | Minimal output (quiet) |

---

## Advanced Options

### Verbose Output (for debugging)
```powershell
# Change /v:minimal to /v:detailed
/v:detailed
```

### Clean Before Building
```powershell
# Add /t:Clean before /t:Build
/t:Clean,Build
```

### Force Rebuild (skip incremental)
```powershell
# Use Rebuild target
/t:Rebuild
```

### Full Example with Clean + Rebuild
```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper_Injector\LicHper_Injector.vcxproj' `
  /t:Clean,Rebuild `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /m `
  /v:detailed
```

---

## Restore from Clean State

If you need to rebuild from scratch:

```powershell
# Remove all build artifacts
Remove-Item -Recurse 'Binaries\*'
Remove-Item -Recurse 'LicHper\obj'
Remove-Item -Recurse 'LicHper_inject\obj'
Remove-Item -Recurse 'LicHper_Injector\obj'

# Then run full build
# (see batch build commands above)
```

---

## Visual Studio GUI Alternative

If you prefer the GUI:

1. **Open Solution:**
   ```
   File > Open > Project/Solution
   Navigate to: DaemonApps.sln
   ```

2. **Select Configuration:**
   - Configuration dropdown: "Release"
   - Platform dropdown: "x64" or "Win32"

3. **Build:**
   - Right-click project > Build
   - Or: Build > Build Solution (F7)

4. **Find Output:**
   - Files appear in Binaries\Win64\Release\ or Binaries\Win32\Release\

---

**Last Updated:** 2026-01-15  
**MSBuild Version:** VS2022 Professional (17.14+)  
**C++ Standard:** C++20

