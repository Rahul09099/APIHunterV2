@echo off
SET IMAGE_NAME=rahul09099/apihunter-worker

echo 🚀 Starting APIHunterV2 Worker Publish Process...
echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯

:: Check if Docker is running
docker ps >nul
if %errorlevel% neq 0 (
    echo.
    echo ❌ ERROR: Docker daemon is not responding.
    echo.
    echo ℹ️  Check your Docker Desktop tray icon (bottom right^):
    echo 1. If it's orange/animated, wait for it to turn GREEN.
    echo 2. If it's already green, try restarting Docker Desktop.
    echo.
    pause
    exit /b
)

echo 🛠️ Building Ghost Node image...
docker build -t %IMAGE_NAME%:latest -f Dockerfile.worker .

if %errorlevel% neq 0 (
    echo ❌ Error: Build failed.
    pause
    exit /b
)

echo ✅ Build successful!
echo.
echo ☁️ Pushing image to registry...
docker push %IMAGE_NAME%:latest

if %errorlevel% neq 0 (
    echo ❌ Error: Push failed. Make sure you are logged in with 'docker login'^(or check your credentials^).
    pause
    exit /b
)

echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯
echo 🎉 SUCCESS! Your Ghost Node image is now live.
echo 📢 Give this name to your subscribers: %IMAGE_NAME%:latest
echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯
pause
