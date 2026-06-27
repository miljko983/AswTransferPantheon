using System.Windows;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Services.Implementation;
using AswTransferToPantheon.Services.Interfaces;
using AswTransferToPantheon.ViewModels;
using AswTransferToPantheon.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AswTransferToPantheon
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly IHost _host;

        public App()
        {
            _host = Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration((context, configuration) =>
        {
            configuration.SetBasePath(AppContext.BaseDirectory);
            configuration.AddJsonFile(
                "appsettings.json",
                optional: false,
                reloadOnChange: true);
        })
        .ConfigureServices((context, services) =>
        {
            services.AddSingleton<MainWindow>();
            services.AddTransient<MainWindowViewModel>();

            services.Configure<ConnectionStrings>(
                context.Configuration.GetSection(nameof(ConnectionStrings)));

            services.Configure<SchedulerConfiguration>(
                context.Configuration.GetSection("Scheduler"));

            services.AddSingleton<ITaskSchedulerService, TaskSchedulerService>();
            services.AddTransient<IKifTransferService, KifTransferService>();
            services.AddTransient<IArtikliTransferService, ArtikliTransferService>();
        })
        .Build();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            await _host.StartAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            await _host.StopAsync();
            _host.Dispose();

            base.OnExit(e);
        }
    }

}
