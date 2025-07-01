# Check if the script is running as Administrator
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "⚠️  This script requires administrator privileges. It will now restart with the necessary rights..." -ForegroundColor Yellow

    $scriptPath = $MyInvocation.MyCommand.Definition
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    Start-Process powershell -Verb runAs -ArgumentList $arguments
    exit
}

# Prompt for parameters
$region = Read-Host "🌍 Enter the AWS region (e.g., eu-south-1)"
$bucket = Read-Host "🪣 Enter the S3 bucket name"
$accessKey = Read-Host "🔑 Enter the AWS Access Key"
$secretKey = Read-Host "🔐 Enter the AWS Secret Key"
$folder = Read-Host "📁 Enter the folder to monitor (e.g., C:\TEMP)"
$publishDir = Read-Host "📦 Enter the publish directory where the service executable will reside (e.g., C:\Services\EfficentS3UploadService)"

# Project path and csproj file
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Get-ChildItem $projectDir -Filter *.csproj | Select-Object -First 1

# Create appsettings.json
$appSettings = @{
    Logging = @{
        LogLevel = @{
            Default = "Information"
            "Microsoft.Hosting.Lifetime" = "Information"
        }
    }
    AWS = @{
        Region = $region
        BucketName = $bucket
        AccessKey = $accessKey
        SecretKey = $secretKey
    }
    FOLDER = @{
        Path = $folder
    }
    LOGS =@{
        LogsFile = "EfficentS3UploadService.log"
    }
}

$appSettingsPath = "$projectDir\appsettings.json"
$appSettings | ConvertTo-Json -Depth 4 | Set-Content $appSettingsPath -Encoding UTF8
Write-Host "✅ appsettings.json created at $appSettingsPath"

# Ask if user wants to build
$shouldBuild = Read-Host "🔨 Do you want to build and publish the project? (Y/N)"
$exePath = Join-Path $publishDir "EfficentS3UploadService.exe"

if ($shouldBuild -match '^[Yy]') {
    if (-not $projectFile) {
        Write-Error "❌ .csproj file not found in the folder $projectDir"
        Read-Host "Press ENTER to exit..."
        exit 1
    }

    Write-Host "⚙️ Building the project in Release mode..."
    dotnet build
    dotnet publish $projectFile.FullName -c Release -o $publishDir -p:PublishSingleFile=true

    if ($LASTEXITCODE -ne 0) {
        Write-Error "❌ Error during publish. Check the .NET build."
        Read-Host "Press ENTER to exit..."
        exit 1
    }

    # ✅ Usa l'exe dalla cartella di publish
    $exePath = Join-Path $publishDir "EfficentS3UploadService.exe"

} else {
    # ✅ Usa l'exe dalla stessa cartella dello script
    $exePath = Join-Path $projectDir "EfficentS3UploadService.exe"

    if (-not (Test-Path $exePath)) {
        Write-Error "❌ Executable not found at $exePath and build was skipped. Cannot proceed."
        Read-Host "Press ENTER to exit..."
        exit 1
    } else {
        Write-Host "📦 Skipping build. Using existing executable at $exePath"
    }
}

# Copy appsettings.json to the published folder
Copy-Item $appSettingsPath (Join-Path $publishDir "appsettings.json") -Force
Write-Host "📄 appsettings.json copied to $publishDir"

# Install the service
$serviceName = "EfficentS3UploadService"
Write-Host "⚙️ Installing Windows service: $serviceName"

sc.exe create $serviceName binPath= "`"$exePath`"" start= auto

if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ Service installed successfully."
    Start-Service $serviceName
    Write-Host "🚀 Service started."
} else {
    Write-Error "❌ Error during service installation. The service may already exist or the exe path may be invalid."
}

Read-Host "Press ENTER to exit..."
