@echo off
echo 🚀 Preparing Windows Release...
echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯

:: Clean up old publish files if they exist
if exist publish_win (
    echo 🧹 Cleaning old publish folder...
    rd /s /q publish_win
)

echo 🛠️ Building WebAPI for Windows (x64)...
dotnet publish UnsecuredAPIKeys.WebAPI/UnsecuredAPIKeys.WebAPI.csproj -c Release -o ./publish_win --runtime win-x64 --self-contained false

if %errorlevel% neq 0 (
    echo ❌ Error: Build failed.
    pause
    exit /b
)

echo.
echo ✅ SUCCESS! Windows build is ready in the \publish_win folder.
echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯
pause
