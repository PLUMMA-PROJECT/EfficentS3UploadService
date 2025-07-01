using SampleService;
using Serilog;
IConfiguration config = new ConfigurationBuilder()
          .AddJsonFile("appsettings.json")
          .Build();
String _LogFilePath = config["LOGS:LogsFile"];
Log.Logger = new LoggerConfiguration()
    .WriteTo.File(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _LogFilePath)
    )
    .CreateLogger();

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => {
        options.ServiceName = "EfficentS3UploadService";
    })
    .UseSerilog()
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<Worker>();
    })
    .Build();

host.Run();
