using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Enums;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AswTransferToPantheon.Services.Implementation
{
    public class TaskSchedulerService : ITaskSchedulerService
    {
        private const string ExecutionTimesPath = "executiontimes.json";
        private static readonly object TaskLock = new object();
        private readonly IOptions<SchedulerConfiguration> schedulerConfiguration;
        private readonly IKifTransferService kifTransferService;
        private Dictionary<string, DateTime> executionTimes = null!;
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly IArtikliTransferService artikliTransferService;
        private readonly ITransferFileLogger transferFileLogger;
        public Action<string> LogAction { get; set; }

        public Action<Exception, string> LogErrorAction { get; set; }

        public TaskSchedulerService(IOptions<SchedulerConfiguration> schedulerConfiguration, 
                                    IKifTransferService kifTransferService, 
                                    IArtikliTransferService artikliTransferService)
        {
            this.schedulerConfiguration = schedulerConfiguration;
            this.kifTransferService = kifTransferService;
            this.artikliTransferService = artikliTransferService;
            this.transferFileLogger = transferFileLogger;
        }

        public Task ScheduleTasks()
        {
            _ = ScheduleDailyTasks(schedulerConfiguration.Value.DailyTasks);
            _ = SchedulePeriodicTasks(schedulerConfiguration.Value.PeriodicTasks);
            return Task.CompletedTask;
        }

        private Task SchedulePeriodicTasks(List<PeriodicTask> periodicTasks)
        {
            foreach (var pt in periodicTasks)
            {
                _ = SchedulePeriodicTask(pt, true);
            }

            return Task.CompletedTask;
        }

        private async Task SchedulePeriodicTask(PeriodicTask pt, bool firstCall)
        {
            if (cancellationTokenSource.IsCancellationRequested)
            {
                LogAction?.Invoke("Task execution cancelled.");
                return;
            }

            var groupName = Path.Combine("PeriodicTasks", pt.Name);

            DateTime nextTime;

            if (firstCall && pt.ExecuteOnStartup)
            {
                nextTime = DateTime.Now;
            }
            else 
            {
                nextTime = GetPeriodicStartTime(pt.Start, pt.End, pt.PeriodInMinutes);
            }

            try
            {
                var difference = nextTime.Subtract(DateTime.Now);
                LogAction?.Invoke($"Scheduled periodic task {pt.Name} for {nextTime}.");
                await Task.Delay(difference, cancellationTokenSource.Token);
                LogAction?.Invoke($"Executing tasks for {pt.Name}...");
                await ExecuteTasks(pt);
                LogAction?.Invoke($"Executed tasks for {pt.Name}.");
            }
            catch (OperationCanceledException)
            {
                // execution is cancelled, just stop.
                transferFileLogger.Info(groupName, "Scheduler", $"Periodic task {pt.Name} cancelled.");
            }
            catch (Exception exc)
            {
                LogError(groupName, "Scheduler", $"Error executing periodic task {pt.Name}.", exc);
            }
            finally
            {
                _ = SchedulePeriodicTask(pt, false);
            }
        }

        private async Task ExecuteTasks(PeriodicTask pt)
        {
            var groupName = Path.Combine("PeriodicTasks", pt.Name);

            foreach (var task in pt.Tasks)
            {
                await GetTask(task, pt.BatchSize, groupName);
            }
        }

        private DateTime GetPeriodicStartTime(TimeSpan start, TimeSpan? end, int periodInMinutes)
        {
            var startDate = DateTime.Now.Date.Add(start);
            var endDate = DateTime.Now.Date.Add(end ?? TimeSpan.FromHours(24));
            DateTime nextPeriod = startDate;
            while (nextPeriod < DateTime.Now)
            {
                nextPeriod = nextPeriod.AddMinutes(periodInMinutes);
                if (nextPeriod > endDate)
                {
                    startDate = startDate.AddDays(1);
                    endDate = endDate.AddDays(1);
                    nextPeriod = startDate;
                }
            }

            return nextPeriod;
        }

        private Task ScheduleDailyTasks(List<DailyTask> dailyTasks)
        {
            LogAction?.Invoke($"Scheduling daily tasks. Count: {dailyTasks.Count}.");
            LoadExecutionTimes();
            foreach (var dtc in dailyTasks)
            {
                _ = ScheduleDailyTask(dtc, true);
            }

            LogAction?.Invoke($"Daily tasks scheduled.");
            return Task.CompletedTask;
        }

        private Task ScheduleDailyTask(DailyTask dtc, bool firstTime)
        {            
            DateTime lastExecution;
            executionTimes.TryGetValue(dtc.Name, out lastExecution);

            DateTime nextTime;

            if (firstTime && dtc.ExecuteOnStartup)
            {
                nextTime = DateTime.Now;
            }
            else
            {
                nextTime = GetDailyStartTime(dtc.Start);
            }

            _ = ScheduleNextDaily(dtc, nextTime);
            return Task.CompletedTask;
        }

        private async Task ScheduleNextDaily(DailyTask dtc, DateTime nextTime)
        {
            if (cancellationTokenSource.IsCancellationRequested)
            {
                LogAction?.Invoke("Task execution cancelled.");
                return;
            }

            var groupName = Path.Combine("DailyTasks", dtc.Name);

            try
            {
                var difference = nextTime.Subtract(DateTime.Now);

                transferFileLogger.Info(groupName, "Scheduler", $"Scheduled daily task {dtc.Name} for {nextTime}.");

                await Task.Delay(difference, cancellationTokenSource.Token);

                transferFileLogger.Info(groupName, "Scheduler", $"Executing daily task {dtc.Name}.");

                await ExecuteTasks(dtc, dtc.BatchSize);

                lock (TaskLock)
                {
                    executionTimes[dtc.Name] = DateTime.Now;
                    SaveExecutionTimes();
                }

                transferFileLogger.Info(groupName, "Scheduler", $"Executed daily task {dtc.Name}.");
            }
            catch (OperationCanceledException)
            {
                transferFileLogger.Info(groupName, "Scheduler", $"Daily task {dtc.Name} cancelled.");
            }
            catch (Exception exception)
            {
                LogError(groupName, "Scheduler", $"Error executing daily task {dtc.Name}.", exception);
            }
            finally
            {
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    _ = ScheduleDailyTask(dtc, false);
                }
            }
        }

        private async Task ExecuteTasks(DailyTask dailyTask, int batchSize)
        {
            var groupName = Path.Combine("DailyTasks", dailyTask.Name);

            foreach (var task in dailyTask.Tasks)
            {
                await GetTask(task, batchSize, groupName);
            }

            foreach (var task in dailyTask.ParallelTasks)
            {
                await GetTask(task, batchSize, groupName);
            }
        }

        private static DateTime GetDailyStartTime(TimeSpan start)
        {
            var nextTime = DateTime.Now.Date.Add(start);

            while (nextTime < DateTime.Now)
            {
                nextTime = nextTime.AddDays(1);
            }

            return nextTime;
        }

        private Task GetTask(TaskType type, int batchSize, string groupName)
        {
            switch (type)
            {
                case TaskType.Artikli:
                    return TransferArtikli(batchSize, groupName, nameof(TaskType.Artikli));

                case TaskType.Kif:
                    return TransferKif(batchSize, groupName, nameof(TaskType.Kif));

                default:
                    return Task.CompletedTask;
            }
        }

        private async Task TransferKif(int batchSize, string groupName, string taskName)
        {
            kifTransferService.LogAction = CreateLogAction(groupName, taskName);

            try
            {
                transferFileLogger.Info(groupName, taskName, $"START {taskName}. BatchSize: {batchSize}");

                await kifTransferService.Transfer(
                    batchSize,
                    cancellationTokenSource.Token);

                transferFileLogger.Info(groupName, taskName, $"END {taskName}.");
            }
            catch (Exception exception)
            {
                LogError(groupName, taskName, $"Greška u tasku {taskName}.", exception);
                throw;
            }
        }

        private async Task TransferArtikli(int batchSize, string groupName, string taskName)
        {
            artikliTransferService.LogAction = CreateLogAction(groupName, taskName);

            try
            {
                transferFileLogger.Info(groupName, taskName, $"START {taskName}. BatchSize: {batchSize}");

                await artikliTransferService.TransferArtikliPaket(batchSize, cancellationTokenSource.Token);

                transferFileLogger.Info(groupName, taskName, $"END {taskName}.");
            }
            catch (Exception exception)
            {
                LogError(groupName, taskName, $"Greška u tasku {taskName}.", exception);
                throw;
            }
        }

        private void SaveExecutionTimes()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string json = JsonSerializer.Serialize(executionTimes, options);
            File.WriteAllText(ExecutionTimesPath, json);
        }

        private void LoadExecutionTimes()
        {
            lock (TaskLock)
            {
                if (executionTimes != null)
                {
                    return;
                }

                if (!File.Exists(ExecutionTimesPath))
                {
                    executionTimes = new Dictionary<string, DateTime>();
                    return;
                }

                string json = File.ReadAllText(ExecutionTimesPath);

                executionTimes = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json)
                       ?? new Dictionary<string, DateTime>();
            }
        }

        public void CancelTasks()
        {
            cancellationTokenSource.Cancel();
        }

        private Action<string> CreateLogAction(string groupName, string taskName)
        {
            return message =>
            {
                LogAction?.Invoke(message);
                transferFileLogger.Info(groupName, taskName, message);
            };
        }

        private void LogError(string groupName, string taskName, string message, Exception exception)
        {
            LogErrorAction?.Invoke(exception, message);
            transferFileLogger.Error(groupName, taskName, message, exception);
        }
    }
}
