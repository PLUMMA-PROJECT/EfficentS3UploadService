# EfficentS3UploadService

**EfficentS3UploadService** is a Windows service written in .NET 8 that monitors a local folder and automatically uploads files to an Amazon S3 bucket. It is designed to be efficient, configurable, and easy to install as a system service.

---

## ✨ Features

- ✅ Real-time monitoring of a local directory  
- ☁️ Automatic upload to AWS S3  
- 🛠️ Simple configuration via `appsettings.json`  
- 🔄 Configurable scan interval  
- 📜 Event logging  
- ⚙️ Installable as a Windows service via PowerShell  

---

## 🧰 Requirements

- [.NET SDK 8.0](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)  
- AWS access with a configured S3 bucket  
- Operating system: Windows 10/11 or Windows Server  

---

## ⚙️ Configuration

In the `appsettings.json` file, set the parameters:

```json
{
  "LOGS": {
    "LogsFile": "EfficentS3UploadService.log"
  },
  "FOLDER": {
    "Path": "M:\\"
  },
  "Logging": {
    "LogLevel": {
      "Microsoft.Hosting.Lifetime": "Information",
      "Default": "Information"
    }
  },
  "AWS": {
    "Region": "eu-west-1",
    "SecretKey": "------------------+FwH7hjVwAahOrGTdwoT",
    "BucketName": "autocad-calzoni",
    "AccessKey": "---------------------",
    "MQTT_endpoint": "--------------ats.iot.eu-west-1.amazonaws.com",
    "MQTT_region": "eu-west-1"
  }
}


```

🏗️ Build e Publish
Cole the project locally
```bash
git clone https://github.com/PLUMMA-PROJECT/EfficentS3UploadService.git
cd EfficentS3UploadService
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -o ./publish
```

L'eseguibile sarà creato nella cartella ./publish.

🖥️ Installazione del servizio Windows
Apri PowerShell

```bash
cd .\EfficentS3UploadService\
.\install-service.ps1
```

🖥️ Disnstallazione del servizio Windows
```bash
.\uninstall-service.ps1
```

▶️ Avvio/Stop del servizio
```bash
Start-Service -Name "EfficentS3UploadService"
Stop-Service -Name "EfficentS3UploadService"
```


In automount di una unità VHD  C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -command "Mount-DiskImage -ImagePath C:\TEMP\acadwin.vhd –PassThru | Get-Disk | Get-Partition | Set-Partition -NewDriveLetter M"
vedi https://woshub.com/auto-mount-vhd-at-startup/
