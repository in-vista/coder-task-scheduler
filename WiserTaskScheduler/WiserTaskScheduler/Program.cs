using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Extensions;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.Ftps.Handlers;
using GeeksCoreLibrary.Modules.Payments.Interfaces;
using Microsoft.Extensions.Configuration;
using Serilog;
using SlackNet.AspNetCore;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Core.Workers;

namespace WiserTaskScheduler
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            
            using var scope = host.Services.CreateScope();
            var databaseHelpersService = scope.ServiceProvider.GetRequiredService<IDatabaseHelpersService>();
            await databaseHelpersService.CheckAndUpdateTablesAsync(new List<string> {WiserTableNames.WtsLogs, WiserTableNames.WtsServices});
            
            await host.RunAsync();
        }

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseWindowsService((options) =>
                {
                    options.ServiceName = "Wiser Task Scheduler";
                })
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.Sources
                        .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
                        .Where(x => x.Path == "appsettings.json");

                    // We need to build here already, so that we can read the base directory for secrets.
                    hostingContext.Configuration = config.Build();

                    var secretsBasePath = hostingContext.Configuration.GetSection("Wts").GetValue<string>("SecretsBaseDirectory");

                    config.AddJsonFile($"{secretsBasePath}wts-appsettings-secrets.json", false, false)
                        .AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json", true, true);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    ConfigureSettings(hostContext.Configuration, services);
                    ConfigureHostedServices(services, hostContext);
                    ConfigureWtsServices(services, hostContext);

                    Log.Logger = new LoggerConfiguration()
                        .ReadFrom.Configuration(hostContext.Configuration)
                        .CreateLogger();
                    services.AddLogging(builder => { builder.AddSerilog(); });
                });

        private static void ConfigureSettings(IConfiguration configuration, IServiceCollection services)
        {
            services.Configure<GclSettings>(configuration.GetSection("Gcl"));
            services.Configure<WtsSettings>(configuration.GetSection("Wts"));
        }

        private static void ConfigureHostedServices(IServiceCollection services, HostBuilderContext hostContext)
        {
            services.AddHostedService<MainWorker>();
            services.AddHostedService<CleanupWorker>();
            var runUpdateParentsWorker = hostContext.Configuration.GetValue<bool>("Wts:RunUpdateParentsWorker", true);
            if (runUpdateParentsWorker)
            {
                services.AddHostedService<UpdateParentsWorker>();
            }
        }

        private static void ConfigureWtsServices(IServiceCollection services, HostBuilderContext hostContext)
        {
            services.AddScoped<ConfigurationsWorker>();

            services.AddGclServices(hostContext.Configuration, false, false, false);
            
            services.AddScoped<FtpsHandler>();
            services.AddScoped<SftpHandler>();
            
            // If there is a bot token provided for Slack add the service. 
            var slackBotToken = hostContext.Configuration.GetSection("Wts").GetSection("SlackSettings").GetValue<string>("BotToken");
            if (!String.IsNullOrWhiteSpace(slackBotToken))
            {
                services.AddSingleton(new SlackEndpointConfiguration());
                services.AddSlackNet(c => c.UseApiToken(slackBotToken));
            }

            // Configure automatic scanning of classes for dependency injection.
            services.Scan(scan => scan
                // We start out with all types in the current assembly.
                .FromCallingAssembly()
                // AddClasses starts out with all public, non-abstract types in this assembly.
                // These types are then filtered by the delegate passed to the method.
                // In this case, we filter out only the classes that are assignable to ITransientService.
                .AddClasses(classes => classes.AssignableTo<ITransientService>())
                // We then specify what type we want to register these classes as.
                // In this case, we want to register the types as all of its implemented interfaces.
                // So if a type implements 3 interfaces; A, B, C, we'd end up with three separate registrations.
                .AsImplementedInterfaces()
                // And lastly, we specify the lifetime of these registrations.
                .WithTransientLifetime()
                // Here we start again, with a new full set of classes from the assembly above.
                // This time, filtering out only the classes assignable to IScopedService.
                .AddClasses(classes => classes.AssignableTo<IScopedService>())
                .AsImplementedInterfaces()
                .WithScopedLifetime()
                // Here we start again, with a new full set of classes from the assembly above.
                // This time, filtering out only the classes assignable to IScopedService.
                .AddClasses(classes => classes.AssignableTo<ISingletonService>())
                .AsImplementedInterfaces()
                .WithSingletonLifetime()

                // Payment service providers need to be added with their own type, otherwise the factory won't work.
                .AddClasses(classes => classes.AssignableTo<IPaymentServiceProviderService>())
                .AsSelf()
                .WithScopedLifetime());
        }
    }
}
