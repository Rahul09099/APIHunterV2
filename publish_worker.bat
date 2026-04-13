@echo off
SET IMAGE_NAME=rahul09099/apihunter-worker

echo 🚀 Starting APIHunterV2 Worker Publish Process...
echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯

:: Check if Docker is running
docker info >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ Error: Docker is not running. Please start Docker Desktop first.
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
    echo ❌ Error: Push failed. Make sure you are logged in with 'docker login'.
    pause
    exit /b
)

echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯
echo 🎉 SUCCESS! Your Ghost Node image is now live.
echo 📢 Give this name to your subscribers: %IMAGE_NAME%:latest
echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯
pause
