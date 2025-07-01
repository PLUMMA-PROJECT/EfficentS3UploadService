# Check if the script is running as Administrator
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "⚠️  This script requires administrator privileges. It will now restart with the necessary rights..." -ForegroundColor Yellow

    # Prepare the command to restart the script with elevation
    $scriptPath = $MyInvocation.MyCommand.Definition
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    Start-Process powershell -Verb runAs -ArgumentList $arguments
    exit
}

# Service name
$serviceName = "EfficentS3UploadService"

# Check if the service exists
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($null -eq $service) {
    Write-Host "⚠️ The service '$serviceName' is not installed."
    exit 0
}

# If the service is running, stop it
if ($service.Status -eq 'Running') {
    Write-Host "🛑 Stopping the service '$serviceName'..."
    Stop-Service -Name $serviceName -Force -ErrorAction Stop
    Write-Host "Service stopped."
}

# Uninstall the service
Write-Host "❌ Uninstalling the service '$serviceName'..."
sc.exe delete $serviceName

if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ Service uninstalled successfully."
} else {
    Write-Error "❌ Error during service uninstallation."
}

Read-Host "Press ENTER to exit..."
