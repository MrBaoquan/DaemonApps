# DaemonApps Binary Distribution Package

**Current Build Date:** 2026-01-15 16:45 UTC  
**Status:** ✅ Ready for distribution

---

## Package Contents

### Primary Distribution Folder Structure

```
📦 DaemonApps_Release/
│
├── 📁 x64/  (64-bit - Full support)
│   ├── LicHper.dll (1,616.5 KB)
│   │   └── Watermark rendering engine
│   │       - DirectX 11/12 support
│   │       - DXGI hook + overlay modes
│   │       - INI-based configuration
│   │
│   ├── LicHper_inject.dll (26 KB)
│   │   └── DLL injection module
│   │       - Exports: WangWang()
│   │       - Process injection support
│   │
│   └── LicHper_Injector.exe (80 KB)
│       └── PE import table modifier
│           - Injects DLL imports into executables
│           - Tested with Unreal Engine 5
│           - Critical offset bug fixed
│
└── 📁 x86/  (32-bit - Tool support)
    └── LicHper_Injector.exe (69.5 KB)
        └── PE import table modifier (32-bit)
            - For 32-bit target processes
            - Fully functional standalone
            - No dependencies
```

---

## File Manifest

### x64 Release Binaries

**Location:** `Binaries/Win64/Release/`

| Filename | Size | Type | Purpose | Status |
|----------|------|------|---------|--------|
| LicHper.dll | 1,616.5 KB | DLL | Core watermark rendering | ✅ Ready |
| LicHper.lib | 3.2 KB | LIB | Import library | ✅ Available |
| LicHper_inject.dll | 26 KB | DLL | DLL injection module | ✅ Ready |
| LicHper_inject.lib | 1.9 KB | LIB | Import library | ✅ Available |
| LicHper_Injector.exe | 80 KB | EXE | PE modifier tool | ✅ Ready |
| LicHper_Injector.pdb | 1.73 MB | PDB | Debug symbols | ✅ Available |
| LicHper.pdb | 16.5 MB | PDB | Debug symbols | ✅ Available |
| LicHper_inject.pdb | 1.8 MB | PDB | Debug symbols | ✅ Available |

**Total Size (DLLs + EXE only):** 1.7 MB  
**Total Size (with debug symbols):** 19.3 MB

### x86 Release Binaries

**Location:** `Binaries/Win32/Release/`

| Filename | Size | Type | Purpose | Status |
|----------|------|------|---------|--------|
| LicHper_Injector.exe | 69.5 KB | EXE | PE modifier tool (32-bit) | ✅ Ready |
| LicHper_Injector.pdb | 1.59 MB | PDB | Debug symbols | ✅ Available |

**Total Size (EXE only):** 69.5 KB  
**Total Size (with debug symbols):** 1.66 MB

---

## Distribution Scenarios

### Scenario 1: x64-Only Deployment (Recommended)

**Best for:** Modern applications, Unreal Engine 5, Windows 11+

**Package:** `DaemonApps_x64_Release.zip` (1.7 MB)

Contents:
```
DaemonApps_x64_Release/
├── LicHper.dll
├── LicHper_inject.dll
└── LicHper_Injector.exe
```

**Installation:**
```bash
# Copy to same directory or add to PATH
copy *.dll C:\Windows\System32\
copy *.exe C:\Windows\System32\
```

**Usage:**
```bash
# Inject Q client DLL into game executable
LicHper_Injector.exe game.exe Q
```

### Scenario 2: Dual Architecture Support

**Best for:** Supporting both 32-bit legacy and 64-bit modern applications

**Package:** `DaemonApps_Complete_Release.zip` (1.8 MB)

Contents:
```
DaemonApps_Complete/
├── x64/
│   ├── LicHper.dll
│   ├── LicHper_inject.dll
│   └── LicHper_Injector.exe
└── x86/
    └── LicHper_Injector.exe
```

**Installation:**
```bash
# x64 components
copy x64\*.dll C:\Windows\System32\
copy x64\*.exe C:\Windows\System32\

# x86 components (32-bit specific location)
copy x86\*.exe C:\Windows\SysWOW64\
```

### Scenario 3: Development Package (with Debug Symbols)

**Best for:** Debugging, troubleshooting, development builds

**Package:** `DaemonApps_Debug_Package.zip` (21 MB)

Contents:
```
DaemonApps_Debug/
├── x64/
│   ├── LicHper.dll
│   ├── LicHper.pdb
│   ├── LicHper_inject.dll
│   ├── LicHper_inject.pdb
│   ├── LicHper_Injector.exe
│   └── LicHper_Injector.pdb
└── x86/
    ├── LicHper_Injector.exe
    └── LicHper_Injector.pdb
```

**Debugging Setup:**
```
1. Copy .pdb files to same location as .dll/.exe
2. Configure debugger to load symbol files
3. Set breakpoints and trace execution
```

### Scenario 4: x86-Only (Legacy Support)

**Best for:** Supporting 32-bit applications only

**Package:** `DaemonApps_x86_Release.zip` (69.5 KB)

Contents:
```
DaemonApps_x86_Release/
└── LicHper_Injector.exe
```

**Note:** Does not include watermark rendering support for x86.

---

## How to Use These Binaries

### Basic Usage: Injecting Q Client DLL

#### For 64-bit games:
```powershell
# Navigate to game directory
cd "C:\Program Files (x86)\YourGame"

# Run injector
C:\Path\To\LicHper_Injector.exe game.exe Q

# Expected output:
# Modified game.exe successfully
# Q client DLL added to import table
```

#### For 32-bit games:
```powershell
cd "C:\Program Files\YourGame"
C:\Path\To\LicHper_Injector_x86.exe game.exe Q
```

### Advanced: Loading Custom DLLs

The Q string can be replaced with any DLL name:
```powershell
# Load custom authentication DLL
LicHper_Injector.exe game.exe MyCustom.dll

# Load with full path
LicHper_Injector.exe game.exe "C:\MyLibs\custom.dll"
```

### Configuration: Watermark Settings

Edit `%USERPROFILE%\.authrc.ini` to customize watermark:

```ini
[Rendering]
Enabled=1
Mode=Hook  ; or Overlay for fallback
Opacity=0.7
Scale=1.0
Position=TopRight  ; TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight

[Text]
Message=Watermark Text Here
FontSize=14
Color=16777215  ; RGB: 255,255,255 (white)

[DirectX]
D3D11=1
D3D12=1
```

---

## Version Information

### Build Metadata

```
Build Date: 2026-01-15
Build Time: 16:45 UTC
Configuration: Release
Compiler: MSVC v143 (VS2022 Professional)
C++ Standard: C++20
Target SDK: Windows 10 SDK (10.0.26100.0)
```

### Component Versions

- **LicHper.dll:** v1.0 (Final)
  - DirectX 11/12 support
  - ImGui rendering framework
  - spdlog-based logging

- **LicHper_inject.dll:** v1.0 (Final)
  - DLL injection capability
  - WangWang() export function

- **LicHper_Injector.exe:** v1.0 (Final)
  - PE import table modification
  - Critical bug fixes applied (offset calculation)

---

## System Requirements

### Minimum Requirements
- **OS:** Windows 10 or later (any edition)
- **Architecture:** x64 processor (for 64-bit) or i386+ (for 32-bit)
- **RAM:** 256 MB minimum
- **Storage:** 2 MB free space

### Runtime Dependencies
- **Visual C++ Runtime:** MSVC v143 redistributable (if not already installed)
- **DirectX:** DirectX 11 or later
- **Windows SDK:** 10.0.26100.0 or compatible

### Optional Dependencies
- **Visual Studio:** For debugging with .pdb files
- **Visual C++ Tools:** For runtime debugging

---

## Troubleshooting

### "DLL not found" Error

**Problem:** Game cannot load injected DLL

**Solution:**
1. Verify DLL is in same directory as .exe
2. Check DLL architecture matches game (x86 vs x64)
3. Use dependency walker to check DLL dependencies
4. Ensure Visual C++ runtime is installed

### "Access denied" Error

**Problem:** Cannot modify game executable

**Solution:**
1. Run LicHper_Injector as Administrator
2. Disable antivirus/game protection temporarily
3. Check file permissions (must be writable)
4. Ensure game is not running

### Game Crashes After Injection

**Problem:** Game crashes after DLL injection

**Potential Causes:**
1. Wrong DLL architecture (x86 DLL into x64 game)
2. Missing DLL dependencies
3. Memory layout conflicts
4. Game integrity check failing

**Solutions:**
1. Verify correct architecture is used
2. Check event viewer for error codes
3. Use debug symbols (.pdb) to get stack traces
4. Check with game vendor for integrity verification

### Watermark Not Showing

**Problem:** Watermark rendering not visible

**Causes:**
1. INI configuration file not found
2. Direct3D hook failed (Overlay mode should activate)
3. Window focus issue (overlay requires window focus)
4. Graphics driver incompatibility

**Solutions:**
1. Create `.authrc.ini` in `%USERPROFILE%`
2. Check application event log for hook errors
3. Try Overlay mode instead of Hook mode
4. Update graphics driver

---

## Distribution Checklist

Before distributing, verify:

- [ ] All x64 DLLs present in x64 folder
- [ ] LicHper_Injector.exe present for both architectures
- [ ] File sizes match specification (within 1%)
- [ ] No extra files or debug symbols (unless debug package)
- [ ] README.md or INSTALL.txt included
- [ ] License information included
- [ ] Configuration examples provided
- [ ] System requirements documented

---

## License & Usage Rights

These binaries are part of the DaemonApps licensing system. Usage is subject to:
- End User License Agreement (EULA)
- License key validation through AuthAssistant
- Watermark rendering support for licensed applications

For licensing inquiries, see LICENSE file in repository root.

---

## Support & Updates

### Reporting Issues
1. Collect error logs from Application Event Viewer
2. Note exact DLL version and game title
3. Describe steps to reproduce
4. Submit to development team with .dmp files if available

### Receiving Updates
- Monitor repository for release notes
- Update binaries when new versions are released
- Test new versions in staging environment first
- Keep .pdb symbols synchronized with binaries

---

## Quick Reference

### File Download Checklist
```
x64 Release Minimum:
  ✓ LicHper.dll (1.6 MB)
  ✓ LicHper_inject.dll (26 KB)  
  ✓ LicHper_Injector.exe (80 KB)

x86 Release Minimum:
  ✓ LicHper_Injector.exe (70 KB)

Optional (for debugging):
  ✓ LicHper.pdb (16.5 MB)
  ✓ LicHper_inject.pdb (1.8 MB)
  ✓ LicHper_Injector.pdb (1.7 MB, x64)
  ✓ LicHper_Injector.pdb (1.6 MB, x86)
```

### Typical Deployment
1. Download x64 Release (1.7 MB)
2. Extract to application directory or System32
3. Create .authrc.ini in user profile
4. Run LicHper_Injector.exe on target game
5. Launch game with injected DLL

---

**Package Generated:** 2026-01-15 16:45 UTC  
**Status:** ✅ Complete & Ready for Distribution

