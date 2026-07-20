param(
    [int]$Port = 45820,
    [string]$Password = "comote-test-2026",
    [string]$ClientName = "Local-Test-PC"
)

$root = Split-Path -Parent $PSScriptRoot
Start-Process dotnet -ArgumentList @(
    "run", "--project", (Join-Path $root "Viewer\Viewer.csproj"), "--",
    "--manager-hub", "--port", $Port, "--password", $Password
)
Start-Sleep -Seconds 3
Start-Process dotnet -ArgumentList @(
    "run", "--project", (Join-Path $root "Host\Host.csproj"), "--",
    "--manager-client", "--manager", "127.0.0.1", "--port", $Port,
    "--password", $Password, "--name", $ClientName
)

Write-Host "Comote Manager와 로컬 Client를 시작했습니다."
Write-Host "Port: $Port / Client: $ClientName"
