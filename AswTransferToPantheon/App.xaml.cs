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
        private int _closingEmailStarted;
        public App()
        {
            _host = Host.CreateDefaultBuilder().ConfigureAppConfiguration((context, configuration) =>
            {
                configuration.SetBasePath(AppContext.BaseDirectory);
                configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<MainWindow>();
                services.AddTransient<MainWindowViewModel>();
                
                services.Configure<ConnectionStrings>(context.Configuration.GetSection(nameof(ConnectionStrings)));
                services.Configure<SchedulerConfiguration>(context.Configuration.GetSection("Scheduler"));
                services.Configure<LoggingConfiguration>(context.Configuration.GetSection("Logging"));
                services.Configure<EmailConfiguration>(context.Configuration.GetSection("Email"));
                services.AddTransient<INaloziTransferService, NaloziTransferService>();
                

                services.AddTransient<IKifTransferService, KifTransferService>();
                services.AddTransient<IArtikliTransferService, ArtikliTransferService>();
                services.AddTransient<IVlpIzvSveTransferService, VlpIzvSveTransferService>();
                services.AddSingleton<ITransferFileLogger, TransferFileLogger>();
                services.AddSingleton<EmailRecipientCache>();
                services.AddHostedService<EmailRecipientRefreshService>();
                services.AddSingleton<IEmailNotificationService, EmailNotificationService>();
                services.AddSingleton<ITaskSchedulerService, TaskSchedulerService>();
                services.AddTransient<IDocumentCreationService_CL_WMS, DocumentCreationService_CL_WMS>();
                services.AddTransient<ICentrosinergijaKreiranjeIdenataService, CentrosinergijaKreiranjeIdenataService>();
                services.AddTransient<INaloziTransferService, NaloziTransferService>();
                services.AddTransient<IKufTransferService, KufTransferService>();


            })
            .Build();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            await _host.StartAsync();

            try
            {
                var emailService = _host.Services.GetRequiredService<IEmailNotificationService>();
                await emailService.SendTestEmail(CancellationToken.None);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    exception.Message,
                    "Greška pri slanju test maila",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);
        }

        protected override void OnSessionEnding(
    SessionEndingCancelEventArgs e)
        {
            base.OnSessionEnding(e);

            if (e.Cancel)
            {
                return;
            }

            var reason =
                e.ReasonSessionEnding == ReasonSessionEnding.Logoff
                    ? "Odjava korisnika iz Windowsa."
                    : "Gašenje ili restart Windowsa.";

            SendClosingEmailOnce(reason);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                SendClosingEmailOnce(
                    "Zatvaranje aplikacije na X ili drugi zahtev za izlazak.");

                using var timeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(1));

                var token = timeout.Token;

                Task.Run(async () =>
                {
                    _host.Services
                        .GetService<ITaskSchedulerService>()
                        ?.CancelTasks();

                    await _host.StopAsync(token);
                }, token)
                .WaitAsync(token)
                .GetAwaiter()
                .GetResult();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Greška pri zaustavljanju aplikacije: {exception}");
            }
            finally
            {
                try
                {
                    _host.Dispose();
                }
                finally
                {
                    base.OnExit(e);
                }
            }
        }

        private void SendClosingEmailOnce(string reason)
        {
            if (Interlocked.Exchange(ref _closingEmailStarted, 1) != 0)
            {
                return;
            }

            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(3));

            var token = timeout.Token;

            try
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var emailService = _host.Services
                            .GetRequiredService<IEmailNotificationService>();

                        await emailService.SendApplicationClosingEmail(
                            reason,
                            token);
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            _host.Services
                                .GetService<ITransferFileLogger>()
                                ?.Error(
                                    "Application",
                                    "Shutdown",
                                    "Neuspešno slanje obaveštenja o zatvaranju.",
                                    exception);
                        }
                        catch (Exception logException)
                        {
                            System.Diagnostics.Trace.WriteLine(logException);
                        }
                    }
                }, token)
                .WaitAsync(token)
                .GetAwaiter()
                .GetResult();
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine(
                    "Isteklo je vreme čekanja na mejl o zatvaranju.");
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine(exception);
            }
        }
    }

}
