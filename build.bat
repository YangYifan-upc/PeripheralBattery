@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0"
set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"

echo ========================================
echo   PeripheralBattery V0.2.0  构建脚本
echo ========================================
echo.

rem 1) 检查 .NET SDK（没有就用官方脚本装到用户目录，不需要管理员）
if not exist "%ROOT%tools\dotnet-install.ps1" (
    echo [1/3] 下载官方 SDK 安装脚本 ...
    if not exist "%ROOT%tools" mkdir "%ROOT%tools"
    powershell -NoProfile -Command "Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%ROOT%tools\dotnet-install.ps1'"
    if errorlevel 1 goto :fail
)
if not exist "%DOTNET%" (
    echo [1/3] 未找到 .NET SDK，正在安装到 %USERPROFILE%\.dotnet ...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\dotnet-install.ps1" -Channel 8.0 -InstallDir "%USERPROFILE%\.dotnet" -NoPath
    if errorlevel 1 goto :fail
)
"%DOTNET%" --list-sdks >nul 2>&1 || goto :fail
echo [1/3] SDK 就绪

rem 2) 构建探测程序（框架依赖，体积小，用于诊断）
echo [2/3] 构建诊断工具 Probe ...
"%DOTNET%" publish "%ROOT%src\Probe\Probe.csproj" -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:DebugType=none -o "%ROOT%dist\tools" || goto :fail

rem 3) 构建主程序（自包含单文件）
echo [3/3] 构建主程序 PeripheralBattery.exe ...
"%DOTNET%" publish "%ROOT%src\App\PeripheralBattery.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none -o "%ROOT%dist" || goto :fail

copy /Y "%ROOT%docs\使用说明.md" "%ROOT%dist\" >nul

echo.
echo 构建完成：%ROOT%dist\PeripheralBattery.exe
echo 直接双击即可运行。
exit /b 0

:fail
echo.
echo [失败] 构建未完成。常见原因：
echo   1. 没有联网（首次构建需要下载 Windows 桌面运行时）
echo   2. exe 正在运行，文件被占用 —— 先在托盘退出程序再重试
exit /b 1
