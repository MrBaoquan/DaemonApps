#include <windows.h>
#include <iostream>
#include <string>
#include <filesystem>
#include <algorithm>
#include <cstring>
#include <fstream>
#include <vector>
#include <imagehlp.h>

#pragma comment(lib, "Imagehlp.lib")

#pragma warning(disable:4996)

namespace fs = std::filesystem;

// PE Import Table modification for persistent DLL injection
class PEImportInjector {
private:
    std::vector<BYTE> fileData;
    PIMAGE_DOS_HEADER dosHeader;
    PIMAGE_NT_HEADERS ntHeaders;
    PIMAGE_SECTION_HEADER sections;
    
    DWORD RvaToFileOffset(DWORD rva) {
        for (int i = 0; i < ntHeaders->FileHeader.NumberOfSections; i++) {
            if (rva >= sections[i].VirtualAddress && 
                rva < sections[i].VirtualAddress + sections[i].Misc.VirtualSize) {
                return sections[i].PointerToRawData + (rva - sections[i].VirtualAddress);
            }
        }
        return 0;
    }
    
    PIMAGE_SECTION_HEADER FindSectionByRva(DWORD rva) {
        for (int i = 0; i < ntHeaders->FileHeader.NumberOfSections; i++) {
            if (rva >= sections[i].VirtualAddress && 
                rva < sections[i].VirtualAddress + sections[i].Misc.VirtualSize) {
                return &sections[i];
            }
        }
        return nullptr;
    }
    
public:
    bool LoadFile(const std::string& filePath) {
        std::ifstream file(filePath, std::ios::binary | std::ios::ate);
        if (!file.is_open()) {
            std::cerr << "Failed to open file" << std::endl;
            return false;
        }
        
        std::streamsize size = file.tellg();
        file.seekg(0, std::ios::beg);
        
        fileData.resize(size);
        if (!file.read((char*)fileData.data(), size)) {
            std::cerr << "Failed to read file" << std::endl;
            return false;
        }
        file.close();
        
        dosHeader = (PIMAGE_DOS_HEADER)fileData.data();
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE) {
            std::cerr << "Invalid DOS signature" << std::endl;
            return false;
        }
        
        ntHeaders = (PIMAGE_NT_HEADERS)(fileData.data() + dosHeader->e_lfanew);
        if (ntHeaders->Signature != IMAGE_NT_SIGNATURE) {
            std::cerr << "Invalid NT signature" << std::endl;
            return false;
        }
        
        sections = IMAGE_FIRST_SECTION(ntHeaders);
        
        return true;
    }
    
    bool AddImport(const std::string& dllName) {
        DWORD importRVA = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
        DWORD importSize = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].Size;
        
        if (importRVA == 0) {
            std::cerr << "Processing failed" << std::endl;
            return false;
        }
        
        PIMAGE_SECTION_HEADER importSection = FindSectionByRva(importRVA);
        if (!importSection) {
            std::cerr << "Processing failed" << std::endl;
            return false;
        }
        
        DWORD importOffset = RvaToFileOffset(importRVA);
        PIMAGE_IMPORT_DESCRIPTOR importDesc = (PIMAGE_IMPORT_DESCRIPTOR)(fileData.data() + importOffset);
        
        int oldImportCount = 0;
        PIMAGE_IMPORT_DESCRIPTOR tempDesc = importDesc;
        while (tempDesc->Name != 0) {
            DWORD nameOffset = RvaToFileOffset(tempDesc->Name);
            const char* importName = (const char*)(fileData.data() + nameOffset);
            
            if (_stricmp(importName, dllName.c_str()) == 0) {
                std::cout << "Already configured" << std::endl;
                return true;
            }
            
            oldImportCount++;
            tempDesc++;
        }
        
        int newImportCount = oldImportCount + 1;
        DWORD oldTableSize = (oldImportCount + 1) * sizeof(IMAGE_IMPORT_DESCRIPTOR);
        DWORD newTableSize = (newImportCount + 1) * sizeof(IMAGE_IMPORT_DESCRIPTOR);
        
        std::cout << "Processing..." << std::endl;

        const bool is64 = ntHeaders->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC;
        const size_t thunkSize = is64 ? sizeof(IMAGE_THUNK_DATA64) : sizeof(IMAGE_THUNK_DATA32);
        
        // Use lowercase dll name like the successful tool
        std::string lowerDllName = dllName;
        std::transform(lowerDllName.begin(), lowerDllName.end(), lowerDllName.begin(), ::tolower);
        size_t dllNameLen = lowerDllName.length() + 1;
        
        DWORD fileAlignment = ntHeaders->OptionalHeader.FileAlignment;
        DWORD sectionAlignment = ntHeaders->OptionalHeader.SectionAlignment;
        
        // Calculate space needed: new import table + dll name + hint/name + INT + IAT
        DWORD sectionDataSize = newTableSize
            + static_cast<DWORD>(dllNameLen)
            + sizeof(WORD) + static_cast<DWORD>(strlen("WangWang")) + 1
            + static_cast<DWORD>(thunkSize * 4)
            + 64; // padding
        
        // Align section size
        DWORD alignedSectionSize = (sectionDataSize + fileAlignment - 1) & ~(fileAlignment - 1);
        
        // Get last section info
        PIMAGE_SECTION_HEADER lastSection = &sections[ntHeaders->FileHeader.NumberOfSections - 1];
        DWORD lastSectionEndRaw = lastSection->PointerToRawData + lastSection->SizeOfRawData;
        DWORD lastSectionEndVA = lastSection->VirtualAddress + 
            ((lastSection->Misc.VirtualSize + sectionAlignment - 1) & ~(sectionAlignment - 1));
        
        // Check if we can add a new section header
        DWORD sectionTableEnd = (DWORD)((BYTE*)&sections[ntHeaders->FileHeader.NumberOfSections] - fileData.data());
        DWORD firstSectionRaw = sections[0].PointerToRawData;
        if (sectionTableEnd + sizeof(IMAGE_SECTION_HEADER) > firstSectionRaw) {
            std::cerr << "No space for new section header" << std::endl;
            return false;
        }
        
        // Create new section header
        PIMAGE_SECTION_HEADER newSection = &sections[ntHeaders->FileHeader.NumberOfSections];
        memset(newSection, 0, sizeof(IMAGE_SECTION_HEADER));
        memcpy(newSection->Name, ".zdlla", 6);
        newSection->Misc.VirtualSize = sectionDataSize;
        newSection->VirtualAddress = lastSectionEndVA;
        newSection->SizeOfRawData = alignedSectionSize;
        newSection->PointerToRawData = lastSectionEndRaw;
        newSection->Characteristics = IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ | IMAGE_SCN_MEM_WRITE | IMAGE_SCN_MEM_EXECUTE;
        
        // Update PE header
        ntHeaders->FileHeader.NumberOfSections++;
        ntHeaders->OptionalHeader.SizeOfImage = newSection->VirtualAddress + 
            ((newSection->Misc.VirtualSize + sectionAlignment - 1) & ~(sectionAlignment - 1));
        
        // Extend file
        fileData.resize(lastSectionEndRaw + alignedSectionSize, 0);
        
        // Re-get pointers after resize
        dosHeader = (PIMAGE_DOS_HEADER)fileData.data();
        ntHeaders = (PIMAGE_NT_HEADERS)(fileData.data() + dosHeader->e_lfanew);
        sections = IMAGE_FIRST_SECTION(ntHeaders);
        newSection = &sections[ntHeaders->FileHeader.NumberOfSections - 1];
        
        // Now write data to new section
        DWORD writeOffset = newSection->PointerToRawData;
        DWORD writeRVA = newSection->VirtualAddress;
        
        // 1. Copy old import table first
        DWORD newTableOffset = writeOffset;
        DWORD newTableRVA = writeRVA;
        memcpy(fileData.data() + newTableOffset, fileData.data() + importOffset, oldTableSize - sizeof(IMAGE_IMPORT_DESCRIPTOR));
        writeOffset += newTableSize; // Skip space for NEW table (includes new IID + null terminator)
        writeRVA += newTableSize;
        
        // 2. Write DLL name
        DWORD dllNameOffset = writeOffset;
        DWORD dllNameRVA = writeRVA;
        memcpy(fileData.data() + dllNameOffset, lowerDllName.c_str(), dllNameLen);
        writeOffset += static_cast<DWORD>(dllNameLen);
        writeRVA += static_cast<DWORD>(dllNameLen);
        
        // Align to WORD
        if (writeRVA % 2 != 0) {
            writeOffset++;
            writeRVA++;
        }
        
        // 3. Write IMAGE_IMPORT_BY_NAME
        DWORD importByNameOffset = writeOffset;
        DWORD importByNameRVA = writeRVA;
        WORD hint = 0;
        memcpy(fileData.data() + importByNameOffset, &hint, sizeof(hint));
        const char* funcName = "WangWang";
        size_t funcNameLen = strlen(funcName) + 1;
        memcpy(fileData.data() + importByNameOffset + sizeof(hint), funcName, funcNameLen);
        writeOffset += static_cast<DWORD>(sizeof(hint) + funcNameLen);
        writeRVA += static_cast<DWORD>(sizeof(hint) + funcNameLen);
        
        // Align to thunk size
        while (writeRVA % static_cast<DWORD>(thunkSize) != 0) {
            writeOffset++;
            writeRVA++;
        }
        
        // 4. Write INT (2 thunks)
        DWORD intOffset = writeOffset;
        DWORD intRVA = writeRVA;
        writeOffset += static_cast<DWORD>(thunkSize * 2);
        writeRVA += static_cast<DWORD>(thunkSize * 2);
        
        // 5. Write IAT (2 thunks)
        DWORD iatOffset = writeOffset;
        DWORD iatRVA = writeRVA;
        
        if (is64) {
            auto intThunk = reinterpret_cast<IMAGE_THUNK_DATA64*>(fileData.data() + intOffset);
            auto iatThunk = reinterpret_cast<IMAGE_THUNK_DATA64*>(fileData.data() + iatOffset);
            intThunk[0].u1.AddressOfData = importByNameRVA;
            intThunk[1].u1.AddressOfData = 0;
            iatThunk[0].u1.AddressOfData = importByNameRVA;
            iatThunk[1].u1.AddressOfData = 0;
        } else {
            auto intThunk = reinterpret_cast<IMAGE_THUNK_DATA32*>(fileData.data() + intOffset);
            auto iatThunk = reinterpret_cast<IMAGE_THUNK_DATA32*>(fileData.data() + iatOffset);
            intThunk[0].u1.AddressOfData = importByNameRVA;
            intThunk[1].u1.AddressOfData = 0;
            iatThunk[0].u1.AddressOfData = importByNameRVA;
            iatThunk[1].u1.AddressOfData = 0;
        }
        
        // 6. Add new IID to import table
        PIMAGE_IMPORT_DESCRIPTOR newTable = (PIMAGE_IMPORT_DESCRIPTOR)(fileData.data() + newTableOffset);
        IMAGE_IMPORT_DESCRIPTOR newIID = {0};
        newIID.OriginalFirstThunk = intRVA;
        newIID.Name = dllNameRVA;
        newIID.FirstThunk = iatRVA;
        newTable[oldImportCount] = newIID;
        memset(&newTable[oldImportCount + 1], 0, sizeof(IMAGE_IMPORT_DESCRIPTOR));
        
        // Clear bound import directory
        ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BOUND_IMPORT].VirtualAddress = 0;
        ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BOUND_IMPORT].Size = 0;
        
        // Point import directory to new table
        ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress = newTableRVA;
        ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].Size = newTableSize;

        std::cout << "Configuration applied successfully" << std::endl;
        
        return true;
    }
    
    bool SaveFile(const std::string& filePath) {
        // Recompute PE checksum
        DWORD headerSum = 0, checkSum = 0;
        if (CheckSumMappedFile(fileData.data(), static_cast<DWORD>(fileData.size()), &headerSum, &checkSum)) {
            ntHeaders->OptionalHeader.CheckSum = checkSum;
        }

        // Remove read-only attribute if present
        DWORD attrs = GetFileAttributesA(filePath.c_str());
        if (attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_READONLY)) {
            SetFileAttributesA(filePath.c_str(), attrs & ~FILE_ATTRIBUTE_READONLY);
        }

        std::ofstream file(filePath, std::ios::binary | std::ios::trunc);
        if (!file.is_open()) {
            std::cerr << "Failed to save file (access denied or file in use)" << std::endl;
            std::cerr << "Try: 1) Run as Administrator 2) Close the target application" << std::endl;
            return false;
        }
        
        file.write((char*)fileData.data(), fileData.size());
        if (!file.good()) {
            std::cerr << "Failed to write file data" << std::endl;
            file.close();
            return false;
        }
        file.close();
        
        return true;
    }
};

// Case-insensitive file search
fs::path FindFileIgnoreCase(const fs::path& directory, const std::string& filename) {
    if (!fs::exists(directory) || !fs::is_directory(directory)) {
        return "";
    }
    
    std::string lowerFilename = filename;
    std::transform(lowerFilename.begin(), lowerFilename.end(), lowerFilename.begin(), ::tolower);
    
    for (const auto& entry : fs::directory_iterator(directory)) {
        if (!entry.is_regular_file()) continue;
        
        std::string entryFilename = entry.path().filename().string();
        std::string lowerEntryFilename = entryFilename;
        std::transform(lowerEntryFilename.begin(), lowerEntryFilename.end(), lowerEntryFilename.begin(), ::tolower);
        
        if (lowerEntryFilename == lowerFilename) {
            return entry.path();
        }
    }
    
    return "";
}

std::string GetUserHome() {
    char* buffer = nullptr;
    size_t len = 0;
    if (_dupenv_s(&buffer, &len, "USERPROFILE") == 0 && buffer) {
        std::string result(buffer);
        free(buffer);
        return result;
    }
    return "";
}

bool BackupOriginalExe(const std::string& targetExePath) {
    try {
        fs::path originalPath(targetExePath);
        fs::path backupPath = originalPath;
        backupPath.replace_extension(".exe.bak");
        
        // Skip if backup already exists
        if (fs::exists(backupPath)) {
            std::cout << "Backup already exists" << std::endl;
            return true;
        }
        
        // Create backup
        fs::copy_file(originalPath, backupPath, fs::copy_options::overwrite_existing);
        std::cout << "Backup created" << std::endl;
        return true;
    } catch (const std::exception& e) {
        std::cerr << "Failed to create backup: " << e.what() << std::endl;
        return false;
    }
}

bool InjectDLL(const std::string& targetExePath, const std::string& dllPath) {
    if (!fs::exists(targetExePath)) {
        std::cerr << "Target EXE not found: " << targetExePath << std::endl;
        return false;
    }
    if (!fs::exists(dllPath)) {
        std::cerr << "DLL not found: " << dllPath << std::endl;
        return false;
    }

    std::cout << "Creating suspended process..." << std::endl;

    STARTUPINFOA si = {0};
    PROCESS_INFORMATION pi = {0};
    si.cb = sizeof(si);

    if (!CreateProcessA(targetExePath.c_str(), NULL, NULL, NULL, FALSE,
                      CREATE_SUSPENDED, NULL, NULL, &si, &pi)) {
        std::cerr << "Failed to create process: " << GetLastError() << std::endl;
        return false;
    }

    std::cout << "Process created (PID: " << pi.dwProcessId << ")" << std::endl;

    LPVOID remoteBuffer = VirtualAllocEx(pi.hProcess, NULL, dllPath.length() + 1,
                                        MEM_COMMIT, PAGE_READWRITE);
    if (!remoteBuffer) {
        std::cerr << "Failed to allocate memory: " << GetLastError() << std::endl;
        TerminateProcess(pi.hProcess, 0);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return false;
    }

    std::cout << "Memory allocated" << std::endl;

    if (!WriteProcessMemory(pi.hProcess, remoteBuffer, (void*)dllPath.c_str(),
                           dllPath.length() + 1, NULL)) {
        std::cerr << "Failed to write memory: " << GetLastError() << std::endl;
        VirtualFreeEx(pi.hProcess, remoteBuffer, 0, MEM_RELEASE);
        TerminateProcess(pi.hProcess, 0);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return false;
    }

    std::cout << "DLL path written" << std::endl;

    HMODULE hKernel32 = GetModuleHandleA("kernel32.dll");
    LPVOID pLoadLibraryA = (LPVOID)GetProcAddress(hKernel32, "LoadLibraryA");

    if (!pLoadLibraryA) {
        std::cerr << "Failed to get LoadLibraryA" << std::endl;
        VirtualFreeEx(pi.hProcess, remoteBuffer, 0, MEM_RELEASE);
        TerminateProcess(pi.hProcess, 0);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return false;
    }

    HANDLE hRemoteThread = CreateRemoteThread(pi.hProcess, NULL, 0,
                                             (LPTHREAD_START_ROUTINE)pLoadLibraryA,
                                             remoteBuffer, 0, NULL);
    if (!hRemoteThread) {
        std::cerr << "Failed to create remote thread: " << GetLastError() << std::endl;
        VirtualFreeEx(pi.hProcess, remoteBuffer, 0, MEM_RELEASE);
        TerminateProcess(pi.hProcess, 0);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return false;
    }

    std::cout << "Remote thread created" << std::endl;

    WaitForSingleObject(hRemoteThread, INFINITE);

    std::cout << "DLL injection succeeded" << std::endl;

    VirtualFreeEx(pi.hProcess, remoteBuffer, 0, MEM_RELEASE);
    CloseHandle(hRemoteThread);
    
    // Resume the main thread to start the application
    std::cout << "Resuming target application..." << std::endl;
    ResumeThread(pi.hThread);
    
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    return true;
}

void CopyConfigAndResources(const std::string& sourceDir, const std::string& targetExePath) {
    try {
        std::string userHome = GetUserHome();
        std::string targetDir = fs::path(targetExePath).parent_path().string();
        
        // 1. Copy .authrc.ini to user home directory
        fs::path sourceConfig = fs::path(sourceDir) / ".authrc.ini";
        fs::path destConfig = fs::path(userHome) / ".authrc.ini";

        if (fs::exists(sourceConfig)) {
            try {
                fs::copy_file(sourceConfig, destConfig, fs::copy_options::overwrite_existing);
                std::cout << "Configuration copied" << std::endl;
            } catch (const std::exception& e) {
                std::cerr << "Warning: Failed to copy config" << std::endl;
            }
        }

        // 2. Copy DLLs to target EXE directory
        // Copy LicHper_inject.dll
        fs::path sourceInjectDll = FindFileIgnoreCase(sourceDir, "LicHper_inject.dll");
        if (!sourceInjectDll.empty()) {
            try {
                fs::path destInjectDll = fs::path(targetDir) / sourceInjectDll.filename();
                fs::copy_file(sourceInjectDll, destInjectDll, fs::copy_options::overwrite_existing);
                std::cout << "Required files copied" << std::endl;
            } catch (const std::exception& e) {
                std::cerr << "Warning: Failed to copy files" << std::endl;
            }
        }
        
        // Copy LicHper.dll
        fs::path sourceDll = FindFileIgnoreCase(sourceDir, "LicHper.dll");
        if (!sourceDll.empty()) {
            try {
                fs::path destDll = fs::path(targetDir) / sourceDll.filename();
                fs::copy_file(sourceDll, destDll, fs::copy_options::overwrite_existing);
            } catch (const std::exception& e) {
            }
        }

        // Copy minhook.x64.dll (required dependency for LicHper.dll)
        fs::path vcpkgBinDir = fs::path(sourceDir).parent_path() / "LicHper" / "vcpkg" / "installed" / "x64-windows" / "bin";
        if (!fs::exists(vcpkgBinDir)) {
            vcpkgBinDir = fs::path(sourceDir) / "minhook.x64.dll"; // fallback to tool directory
        }
        
        fs::path sourceMinhook = FindFileIgnoreCase(vcpkgBinDir, "minhook.x64.dll");
        if (sourceMinhook.empty()) {
            sourceMinhook = FindFileIgnoreCase(sourceDir, "minhook.x64.dll");
        }
        if (!sourceMinhook.empty()) {
            try {
                fs::path destMinhook = fs::path(targetDir) / sourceMinhook.filename();
                fs::copy_file(sourceMinhook, destMinhook, fs::copy_options::overwrite_existing);
            } catch (...) {}
        }

        // 3. Create watermark resource directory
        std::string lichperDir = userHome + "\\.lichper";
        if (!fs::exists(lichperDir)) {
            try {
                fs::create_directories(lichperDir);
            } catch (...) {}
        }

        // 4. Copy watermark resources
        try {
            for (const auto& entry : fs::directory_iterator(sourceDir)) {
                if (entry.is_regular_file()) {
                    std::string filename = entry.path().filename().string();
                    std::string extension = entry.path().extension().string();

                    if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                        extension == ".bmp" || extension == ".gif") {
                        try {
                            fs::path destPath = fs::path(lichperDir) / filename;
                            fs::copy_file(entry.path(), destPath, fs::copy_options::overwrite_existing);
                        } catch (...) {}
                    }
                }
            }
        } catch (...) {}

    } catch (...) {
        std::cerr << "Warning: Error during resource copy" << std::endl;
    }
}

int main(int argc, char* argv[]) {
    std::cout << "========================================" << std::endl;
    std::cout << "      LicHper DLL Injector Tool" << std::endl;
    std::cout << "========================================" << std::endl;
    std::cout << std::endl;

    if (argc < 2) {
        std::cout << "Usage:" << std::endl;
        std::cout << "  Drag and drop an EXE file onto this tool" << std::endl;
        std::cout << "  or run: LicHper_Injector.exe <target.exe>" << std::endl;
        std::cout << std::endl;
        std::cout << "Features:" << std::endl;
        std::cout << "  - Backup original EXE (.exe.bak)" << std::endl;
        std::cout << "  - Inject LicHper_inject.dll with auto-validation" << std::endl;
        std::cout << "  - Copy .authrc.ini configuration" << std::endl;
        std::cout << "  - Copy watermark images" << std::endl;
        std::cout << std::endl;
        system("pause");
        return 0;
    }

    std::string targetExe = argv[1];

    if (!fs::exists(targetExe)) {
        std::cerr << "Error: File not found: " << targetExe << std::endl;
        system("pause");
        return 1;
    }

    if (targetExe.find(".exe") == std::string::npos) {
        std::cerr << "Error: Please drag an .exe file" << std::endl;
        system("pause");
        return 1;
    }

    char toolDir[MAX_PATH] = {0};
    GetModuleFileNameA(NULL, toolDir, MAX_PATH);
    fs::path toolPath(toolDir);
    std::string sourceDir = toolPath.parent_path().string();

    // Find LicHper_inject.dll (case-insensitive)
    fs::path dllPath = FindFileIgnoreCase(sourceDir, "LicHper_inject.dll");
    
    if (dllPath.empty()) {
        std::cerr << "Error: LicHper_inject.dll not found in " << sourceDir << std::endl;
        system("pause");
        return 1;
    }

    std::cout << "Target: " << fs::path(targetExe).filename().string() << std::endl;
    std::cout << std::endl;

    std::cout << "Step 1: Creating backup..." << std::endl;
    if (!BackupOriginalExe(targetExe)) {
        std::cerr << "Backup failed!" << std::endl;
        system("pause");
        return 1;
    }
    std::cout << std::endl;

    std::cout << "Step 2: Copying resources..." << std::endl;
    CopyConfigAndResources(sourceDir, targetExe);
    std::cout << std::endl;

    std::cout << "Step 3: Applying configuration..." << std::endl;
    PEImportInjector injector;
    if (!injector.LoadFile(targetExe)) {
        std::cerr << "Failed to load PE file!" << std::endl;
        system("pause");
        return 1;
    }
    
    if (!injector.AddImport(dllPath.filename().string())) {
        std::cerr << "Failed to add import!" << std::endl;
        system("pause");
        return 1;
    }
    
    if (!injector.SaveFile(targetExe)) {
        std::cerr << "Failed to save file!" << std::endl;
        system("pause");
        return 1;
    }
    
    std::cout << std::endl;
    std::cout << "========================================" << std::endl;
    std::cout << "Success! Configuration completed!" << std::endl;
    std::cout << "Watermark protection is now active." << std::endl;
    std::cout << "========================================" << std::endl;
    std::cout << std::endl;
    system("pause");
    return 0;
}
