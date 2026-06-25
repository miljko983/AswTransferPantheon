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
        private readonly IKifTransfer kifTransferService;
        private Dictionary<string, DateTime> executionTimes = null!;
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        public Action<string> LogAction { get; set; }

        public Action<Exception, string> LogErrorAction { get; set; }

        public TaskSchedulerService(IOptions<SchedulerConfiguration> schedulerConfiguration, IKifTransfer kifTransferService)
        {
            this.schedulerConfiguration = schedulerConfiguration;
            this.kifTransferService = kifTransferService;
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
            }
            catch (Exception exc)
            {
                LogErrorAction?.Invoke(exc, $"Error executing periodic task {pt.Name}.");
            }
            finally
            {
                _ = SchedulePeriodicTask(pt, false);
            }
        }

        private async Task ExecuteTasks(PeriodicTask pt)
        {
            foreach (var task in pt.Tasks)
            {
                await GetTask(task, pt.BatchSize);
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
            var nextTime = GetDailyStartTime(dtc.Start);
            if (firstTime && lastExecution.Date != DateTime.Now.Date && nextTime <= DateTime.Now)
            {
                nextTime = DateTime.Now;
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

            try
            {
                var difference = nextTime.Subtract(DateTime.Now);
                await Task.Delay(difference, cancellationTokenSource.Token);
                await ExecuteTasks(dtc, dtc.BatchSize);

                lock (TaskLock)
                {
                    executionTimes[dtc.Name] = DateTime.Now;
                    SaveExecutionTimes();
                }

                _ = ScheduleDailyTask(dtc, false);
            }
            catch (OperationCanceledException)
            {
                // execution is cancelled, just stop.
            }
        }

        private async Task ExecuteTasks(DailyTask dailyTask, int batchSize)
        {
            foreach (var task in dailyTask.Tasks)
            {
                await GetTask(task, batchSize);
            }
        }

        private static DateTime GetDailyStartTime(TimeSpan start)
        {
            var nextTime = DateTime.Now.Date.Add(start);
            while (nextTime < DateTime.Now)
            {
                nextTime.AddDays(1);
            }

            return nextTime;
        }

        private Task GetTask(TaskType type, int batchSize)
        {
            switch (type)
            {
                case TaskType.Artikli:
                    return TransferArtikli(batchSize);
                case TaskType.Kif:
                    return TransferKif(batchSize);
                    // za svaki type
                default:
                    return Task.CompletedTask;
            }
        }

        private async Task TransferKif(int batchSize)
        {
            await kifTransferService.Transfer(batchSize, cancellationTokenSource.Token);   //ovde ide logika
        }

        private async Task TransferArtikli(int batchSize)
        {
            // prebaci artikle i td.
            // ucitaj artikle
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
    }
}
