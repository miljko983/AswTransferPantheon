using AswTransferToPantheon.Commands.Base;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Services.Interfaces;
using AswTransferToPantheon.ViewModels.Base;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;

namespace AswTransferToPantheon.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly IOptions<ConnectionStrings> connectionStrings;
        private readonly ITaskSchedulerService taskService;
        private string transferDb;
        private string sourceDb;
        private Dispatcher dispatcher = App.Current.Dispatcher;

        public ICommand LoadCommand => new AsyncRelayCommand(OnLoaded, onException: HandleError);

        public string TransferDb { get => transferDb; private set => SetProperty(ref transferDb, value); }
        public string SourceDb { get => sourceDb; private set => SetProperty(ref sourceDb, value); }

        public ObservableCollection<LogMessage> Messages { get; } = new ObservableCollection<LogMessage>();

        public MainWindowViewModel(IOptions<ConnectionStrings> connectionStrings, ITaskSchedulerService taskService)
        {
            this.connectionStrings = connectionStrings;
            this.taskService = taskService;
            taskService.LogAction += LogInfo;
            taskService.LogErrorAction += LogError;
        }

        private void LogError(Exception exception, string message)
        {
            dispatcher.Invoke(() =>
            {
                Messages.Add(new LogMessage
                {
                    IsError = true,
                    Message = $"{message}: {exception.Message}"
                });
            });
        }

        private void LogInfo(string message)
        {
            dispatcher.Invoke(() =>
            {
                Messages.Add(new LogMessage
                {
                    IsError = false,
                    Message = message
                });
            });
        }

        private async Task OnLoaded()
        {
            var connectionStringBuilder = new SqlConnectionStringBuilder(connectionStrings.Value.Transfer);
            TransferDb = $"{connectionStringBuilder.DataSource}: {connectionStringBuilder.InitialCatalog}";
            var oracleCsb = new OracleConnectionStringBuilder(connectionStrings.Value.Asw);
            SourceDb = oracleCsb.DataSource;
            await RunTasks();
        }

        private async Task RunTasks()
        {
            await taskService.ScheduleTasks();
        }

        internal void CleanUp()
        {
            taskService.CancelTasks();
        }

        public class LogMessage
        {
            public DateTime DateTime { get; } = DateTime.Now;
            public string Message { get; set; }

            public bool IsError { get; set; }
        }
    }
}
