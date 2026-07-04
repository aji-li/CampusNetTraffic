using CampusNetTraffic.Services;
using CampusNetTraffic.TrafficService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CampusNetTraffic Traffic Service";
});
builder.Services.AddHostedService<TrafficPipeWorker>();

await builder.Build().RunAsync();
