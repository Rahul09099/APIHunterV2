@echo off
echo 🚀 Synchronizing with GitHub...
echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯

echo 🔍 Checking status...
git status

echo.
echo 📦 Adding changes...
git add .

echo 📝 Committing changes...
git commit -m "Deployment optimization: Docker fixes, .dockerignore, .wslconfig, and Admin Management features"

echo ☁️  Pushing to GitHub...
git push

if %errorlevel% neq 0 (
    echo.
    echo ❌ ERROR: Push failed. Check your internet or git credentials.
    pause
    exit /b
)

echo.
echo ✅ SUCCESS! Code is now live on GitHub and ready for Render auto-deploy.
echo ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯
pause
