@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

netsh advfirewall firewall delete rule name="Comote Manager Hub TCP 45820" >nul 2>&1
netsh advfirewall firewall add rule name="Comote Manager Hub TCP 45820" dir=in action=allow protocol=TCP localport=45820 profile=any

echo.
echo Comote Manager Hub TCP 45820 firewall rule is enabled.
pause

