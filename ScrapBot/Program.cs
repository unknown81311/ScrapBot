using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ScrapBot;

internal static class Program {
    internal static async Task Main(string[] args) {
        var host = Host.CreateDefaultBuilder(args)
                       .ConfigureServices((ctx, services) => {
                           services.AddOptions<Steam.Options>()
                                   .Configure(options => {
                                       options.MaxReconnectDelaySeconds = (int)TimeSpan.FromMinutes(4).TotalSeconds;
                                       options.PICSRefreshDelaySeconds = 2;
                                   })
                                   .BindConfiguration("Steam")
                                   .ValidateDataAnnotations()
                                   .ValidateOnStart();

                           services.AddHostedService<Steam.Service>();
                       })
                       .Build();

        await host.RunAsync();
    }
}