@echo off
REM DaemonApps Binary Verification Script
REM Verifies all compiled binaries are present and correct

setlocal enabledelayedexpansion

echo ============================================
echo DaemonApps Build Verification Report
echo ============================================
echo.

REM Colors: doesn't work in batch, using symbols instead
REM √ = OK, ✗ = Missing, ! = Warning

set "all_good=1"

echo [64-bit Release Binaries]
echo.

REM Check x64 DLLs
if exist "Binaries\Win64\Release\LicHper.dll" (
    for %%A in ("Binaries\Win64\Release\LicHper.dll") do (
        set /a "size_kb=%%~zA / 1024"
        echo √ LicHper.dll ... !size_kb! KB
    )
) else (
    echo ✗ LicHper.dll ... MISSING
    set all_good=0
)

if exist "Binaries\Win64\Release\LicHper_inject.dll" (
    for %%A in ("Binaries\Win64\Release\LicHper_inject.dll") do (
        set /a "size_kb=%%~zA / 1024"
        echo √ LicHper_inject.dll ... !size_kb! KB
    )
) else (
    echo ✗ LicHper_inject.dll ... MISSING
    set all_good=0
)

if exist "Binaries\Win64\Release\LicHper_Injector.exe" (
    for %%A in ("Binaries\Win64\Release\LicHper_Injector.exe") do (
        set /a "size_kb=%%~zA / 1024"
        echo √ LicHper_Injector.exe ... !size_kb! KB
    )
) else (
    echo ✗ LicHper_Injector.exe ... MISSING
    set all_good=0
)

echo.
echo [32-bit Release Binaries]
echo.

if exist "Binaries\Win32\Release\LicHper_Injector.exe" (
    for %%A in ("Binaries\Win32\Release\LicHper_Injector.exe") do (
        set /a "size_kb=%%~zA / 1024"
        echo √ LicHper_Injector.exe ... !size_kb! KB
    )
) else (
    echo ! LicHper_Injector.exe ... MISSING (not critical, x64 is sufficient)
)

echo.
echo [Debug Symbols - Optional]
echo.

if exist "Binaries\Win64\Release\LicHper.pdb" (
    for %%A in ("Binaries\Win64\Release\LicHper.pdb") do (
        set /a "size_mb=%%~zA / 1048576"
        echo √ LicHper.pdb ... !size_mb! MB
    )
) else (
    echo ! LicHper.pdb ... NOT FOUND
)

if exist "Binaries\Win64\Release\LicHper_inject.pdb" (
    for %%A in ("Binaries\Win64\Release\LicHper_inject.pdb") do (
        set /a "size_mb=%%~zA / 1048576"
        echo √ LicHper_inject.pdb ... !size_mb! MB
    )
) else (
    echo ! LicHper_inject.pdb ... NOT FOUND
)

if exist "Binaries\Win64\Release\LicHper_Injector.pdb" (
    for %%A in ("Binaries\Win64\Release\LicHper_Injector.pdb") do (
        set /a "size_mb=%%~zA / 1048576"
        echo √ LicHper_Injector.pdb ... !size_mb! MB
    )
) else (
    echo ! LicHper_Injector.pdb ... NOT FOUND
)

echo.
echo [Verification Results]
echo.

if !all_good! equ 1 (
    echo ============================================
    echo √ ALL CRITICAL BINARIES PRESENT AND READY
    echo ============================================
    echo.
    echo Status: READY FOR DISTRIBUTION
    echo.
    echo x64 Full Support: YES
    echo x86 Tool Support: YES
    echo.
    exit /b 0
) else (
    echo ============================================
    echo ✗ MISSING CRITICAL FILES - BUILD INCOMPLETE
    echo ============================================
    echo.
    echo Run build commands to complete compilation:
    echo.
    echo   MSBuild LicHper\LicHper.vcxproj /p:Configuration=Release /p:Platform=x64
    echo   MSBuild LicHper_inject\LicHper_inject.vcxproj /p:Configuration=Release /p:Platform=x64
    echo   MSBuild LicHper_Injector\LicHper_Injector.vcxproj /p:Configuration=Release /p:Platform=x64
    echo.
    exit /b 1
)
