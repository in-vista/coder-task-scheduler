using AutoUpdater.Interfaces;
using AutoUpdater.Models;
using AutoUpdater.Services;
using AutoUpdater.Workers;
using GeeksCoreLibrary.Core.Extensions;
using GeeksCoreLibrary.Core.Models;
using Microsoft.AspNetCore.Http;
using Serilog;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService((options) =>
    {
        options.ServiceName = "WTS Auto Updater";
    })
    .ConfigureAppConfiguration((hostingContext, config) =>
    {
        config.SetBasePath(AppContext.BaseDirectory);
        config.Sources
            .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
            .Where(x => x.Path == "appsettings.json");

        // We need to build here already, so that we can read the base directory for secrets.
        hostingContext.Configuration = config.Build();

        var secretsBasePath = hostingContext.Configuration.GetSection("Updater").GetValue<string>("SecretsBaseDirectory");

        config.AddJsonFile($"{secretsBasePath}updater-appsettings-secrets.json", false, false)
            .AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json", true, true);
    })
    .ConfigureServices((hostContext, services) =>
    {
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(hostContext.Configuration)
            .CreateLogger();
        services.AddLogging(builder => { builder.AddSerilog(); });

        services.Configure<GclSettings>(hostContext.Configuration.GetSection("Gcl"));
        services.Configure<UpdateSettings>(hostContext.Configuration.GetSection("Updater"));
        services.AddHostedService<UpdateWorker>();
        
        #pragma warning disable CA1416
        services.AddSingleton<IUpdateService, UpdateService>();
        #pragma warning restore CA1416
        
        services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
        services.AddGclServices(hostContext.Configuration, false, false, false);
    })
    .Build();

await host.RunAsync();