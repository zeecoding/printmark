using PrintMark.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PrintMark Capture Daemon";
});

builder.Services.AddHostedService<SpoolCaptureWorker>();

var host = builder.Build();
host.Run();