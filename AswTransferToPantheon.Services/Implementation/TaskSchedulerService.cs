using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Enums;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;
using System.Text.Json;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Helpers;

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
        private readonly IEmailNotificationService emailNotificationService;
        private readonly IVlpIzvSveTransferService vlpIzvSveTransferService;
        private readonly IDocumentCreationService_CL_WMS documentCreationService_CL_WMS;
        private readonly INaloziTransferService naloziTransferService;
        public Action<string> LogAction { get; set; }

        public Action<Exception, string> LogErrorAction { get; set; }

        public TaskSchedulerService(IOptions<SchedulerConfiguration> schedulerConfiguration,
                                    IKifTransferService kifTransferService,
                                    IArtikliTransferService artikliTransferService,
                                    IVlpIzvSveTransferService vlpIzvSveTransferService,
                                    INaloziTransferService naloziTransferService,
                                    ITransferFileLogger transferFileLogger,
                                    IEmailNotificationService emailNotificationService,
                                    IDocumentCreationService_CL_WMS documentCreationService_CL_WMS)
        {
            this.schedulerConfiguration = schedulerConfiguration;
            this.kifTransferService = kifTransferService;
            this.artikliTransferService = artikliTransferService;
            this.vlpIzvSveTransferService = vlpIzvSveTransferService;
            this.naloziTransferService = naloziTransferService;
            this.transferFileLogger = transferFileLogger;
            this.emailNotificationService = emailNotificationService;
            this.documentCreationService_CL_WMS = documentCreationService_CL_WMS;
        }

        public Task ScheduleTasks()
        {
            _ = ScheduleDailyTasks(schedulerConfiguration.Value.DailyTasks);
            _ = SchedulePeriodicTasks(schedulerConfiguration.Value.PeriodicTasks);
            _ = ScheduleNaloziTasks(schedulerConfiguration.Value.NaloziTasks);
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
                transferFileLogger.Info(groupName, "Scheduler", $"Periodic task {pt.Name} cancelled.");
            }
            catch (CriticalTransferException exc)
            {
                transferFileLogger.Info(
                    groupName,
                    "Scheduler",
                    $"Periodic task {pt.Name} stopped because of critical error. Next schedule will continue normally.");
            }
            catch (Exception exc)
            {
                if (TransferErrorHelper.IsCriticalError(exc))
                {
                    await LogCritical(
                        groupName,
                        "Scheduler",
                        $"Critical error executing periodic task {pt.Name}.",
                        exc);
                }
                else
                {
                    LogError(
                        groupName,
                        "Scheduler",
                        $"Error executing periodic task {pt.Name}.",
                        exc);
                }
            }
            finally
            {
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    _ = SchedulePeriodicTask(pt, false);
                }
            }
        }

        private Task ScheduleNaloziTasks(List<NaloziTaskConfiguration> naloziTasks)
        {
            LogAction?.Invoke($"Scheduling Nalozi tasks. Count: {naloziTasks.Count}.");

            foreach (var naloziTask in naloziTasks)
            {
                _ = ScheduleNaloziTask(naloziTask, true);
            }

            LogAction?.Invoke("Nalozi tasks scheduled.");

            return Task.CompletedTask;
        }

        private async Task ScheduleNaloziTask(NaloziTaskConfiguration naloziTask, bool firstCall)
        {
            if (cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            var groupName = Path.Combine("NaloziTasks", naloziTask.Name);

            DateTime nextTime;

            if (firstCall && naloziTask.ExecuteOnStartup)
            {
                nextTime = DateTime.Now;
            }
            else
            {
                nextTime = GetPeriodicStartTime(naloziTask.Start, naloziTask.End, naloziTask.PeriodInMinutes);
            }

            try
            {
                var difference = nextTime.Subtract(DateTime.Now);

                transferFileLogger.Info(groupName, "Scheduler", $"Scheduled Nalozi task {naloziTask.Name} for {nextTime}.");

                await Task.Delay(difference, cancellationTokenSource.Token);

                transferFileLogger.Info(groupName, "Scheduler", $"Executing Nalozi task {naloziTask.Name}.");

                await TransferNalozi(naloziTask);

                transferFileLogger.Info(groupName, "Scheduler", $"Executed Nalozi task {naloziTask.Name}.");
            }
            catch (OperationCanceledException)
            {
                transferFileLogger.Info(groupName, "Scheduler", $"Nalozi task {naloziTask.Name} cancelled.");
            }
            catch (CriticalTransferException)
            {
                transferFileLogger.Info(groupName, "Scheduler", $"Nalozi task {naloziTask.Name} stopped بسبب critical error.");
            }
            catch (Exception exception)
            {
                if (TransferErrorHelper.IsCriticalError(exception))
                {
                    await LogCritical(groupName, naloziTask.Name, $"Critical error executing Nalozi task {naloziTask.Name}.", exception);
                }
                else
                {
                    LogError(groupName, naloziTask.Name, $"Error executing Nalozi task {naloziTask.Name}.", exception);
                }
            }
            finally
            {
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    _ = ScheduleNaloziTask(naloziTask, false);
                }
            }
        }

        private async Task ExecuteTasks(PeriodicTask pt)
        {
            var groupName = Path.Combine("PeriodicTasks", pt.Name);

            foreach (var task in pt.Tasks)
            {
                await GetTask(task, pt.BatchSize, groupName, pt.DaysBack);
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

        /* private async Task ScheduleNextDaily(DailyTask dtc, DateTime nextTime)
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
             catch (CriticalTransferException exc)
             {
                 transferFileLogger.Info(
                     groupName,
                     "Scheduler",
                     $"Daily task {dtc.Name} stopped because of critical error. Next schedule will continue normally.");
             }
             catch (Exception exception)
             {
                 if (TransferErrorHelper.IsCriticalError(exception))
                 {
                     await LogCritical(
                         groupName,
                         "Scheduler",
                         $"Critical error executing daily task {dtc.Name}.",
                         exception);
                 }
                 else
                 {
                     LogError(
                         groupName,
                         "Scheduler",
                         $"Error executing daily task {dtc.Name}.",
                         exception);
                 }
             }
             finally
             {
                 if (!cancellationTokenSource.IsCancellationRequested)
                 {
                     _ = ScheduleDailyTask(dtc, false);
                 }
             }
         }*/

        private async Task ScheduleNextDaily(
    DailyTask dtc,
    DateTime nextTime)
        {
            if (cancellationTokenSource.IsCancellationRequested)
            {
                LogAction?.Invoke("Task execution cancelled.");
                return;
            }

            var groupName = Path.Combine(
                "DailyTasks",
                dtc.Name);

            try
            {
                var difference = nextTime - DateTime.Now;

                var scheduledMessage =
                    $"Scheduled daily task {dtc.Name} for {nextTime}.";

                LogAction?.Invoke(scheduledMessage);

                transferFileLogger.Info(
                    groupName,
                    "Scheduler",
                    scheduledMessage);

                if (difference > TimeSpan.Zero)
                {
                    await Task.Delay(
                        difference,
                        cancellationTokenSource.Token);
                }

                var executingMessage =
                    $"Executing daily task {dtc.Name}.";

                LogAction?.Invoke(executingMessage);

                transferFileLogger.Info(
                    groupName,
                    "Scheduler",
                    executingMessage);

                await ExecuteTasks(dtc, dtc.BatchSize);

                lock (TaskLock)
                {
                    executionTimes[dtc.Name] = DateTime.Now;
                    SaveExecutionTimes();
                }

                var executedMessage =
                    $"Executed daily task {dtc.Name}.";

                LogAction?.Invoke(executedMessage);

                transferFileLogger.Info(
                    groupName,
                    "Scheduler",
                    executedMessage);
            }
            catch (OperationCanceledException)
            {
                transferFileLogger.Info(
                    groupName,
                    "Scheduler",
                    $"Daily task {dtc.Name} cancelled.");
            }
            catch (CriticalTransferException)
            {
                transferFileLogger.Info(
                    groupName,
                    "Scheduler",
                    $"Daily task {dtc.Name} stopped because of critical error.");
            }
            catch (Exception exception)
            {
                if (TransferErrorHelper.IsCriticalError(exception))
                {
                    await LogCritical(
                        groupName,
                        "Scheduler",
                        $"Critical error executing daily task {dtc.Name}.",
                        exception);
                }
                else
                {
                    LogError(
                        groupName,
                        "Scheduler",
                        $"Error executing daily task {dtc.Name}.",
                        exception);
                }
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
                await GetTask(task, batchSize, groupName, 0, dailyTask.ExecuteDocumentCreation);
            }

            foreach (var task in dailyTask.ParallelTasks)
            {
                await GetTask(task, batchSize, groupName, 0, dailyTask.ExecuteDocumentCreation);
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

        private Task GetTask(TaskType type, int batchSize, string groupName, int daysBack = 0, bool executeDocumentCreation = false)
        {
            switch (type)
            {
                case TaskType.Artikli: return TransferArtikli(batchSize, groupName, nameof(TaskType.Artikli), executeDocumentCreation);
                case TaskType.Kif: return TransferKif(batchSize, groupName, nameof(TaskType.Kif));
                case TaskType.VLPIzvSve: return TransferVlpIzvSve(batchSize,  daysBack,  groupName,  nameof(TaskType.VLPIzvSve));
                default: return Task.CompletedTask;
            }
        }

        private async Task TransferNalozi(NaloziTaskConfiguration naloziTask)
        {
            var groupName = Path.Combine("NaloziTasks", naloziTask.Name);

            var taskName = naloziTask.Name;

            var badRecords = new List<BadRecordInfo>();

            naloziTransferService.LogAction = CreateLogAction(groupName, taskName);

            naloziTransferService.BadRecordAction =
                (table, key, data, message, exception) =>
                {
                    transferFileLogger.BadRecord(
                        groupName,
                        taskName,
                        table,
                        key,
                        data,
                        exception);

                    badRecords.Add(new BadRecordInfo
                    {
                        TableName = table,
                        Key = key,
                        Data = data,
                        Message = message,
                        Exception = exception.Message
                    });
                };

            try
            {
                transferFileLogger.Info(
                    groupName,
                    taskName,
                    $"START {taskName}. " +
                    $"BatchSize: {naloziTask.BatchSize}, " +
                    $"DatumOd: {naloziTask.DateFrom:yyyy-MM-dd}");

                await naloziTransferService.Transfer(naloziTask.BatchSize, naloziTask.DateFrom, cancellationTokenSource.Token);

                if (badRecords.Count > 0)
                {
                    await emailNotificationService.SendBadRecordsSummaryEmail(groupName, taskName, badRecords, cancellationTokenSource.Token);
                }

                transferFileLogger.Info(groupName, taskName, $"END {taskName}.");
            }
            catch (Exception exception)
            {
                if (TransferErrorHelper.IsCriticalError(exception))
                {
                    var message = $"Kritična greška u tasku {taskName}. " + "Trenutno izvršavanje se prekida do sledećeg termina.";

                    await LogCritical(groupName, taskName, message, exception);

                    throw new CriticalTransferException(message, exception);
                }

                throw;
            }
        }

        private async Task TransferKif(int batchSize, string groupName, string taskName)
        {
            var badRecords = new List<BadRecordInfo>();

            kifTransferService.LogAction = CreateLogAction(groupName, taskName);

            kifTransferService.BadRecordAction = (table, key, data, message, ex) =>
            {
                transferFileLogger.BadRecord(groupName, taskName, table, key, data, ex);

                badRecords.Add(new BadRecordInfo
                {
                    TableName = table,
                    Key = key,
                    Message = message,
                    Data = data,
                    Exception = ex.Message
                });
            };

            try
            {
                transferFileLogger.Info(groupName, taskName, $"START {taskName}. BatchSize: {batchSize}");

                await kifTransferService.Transfer(
                    batchSize,
                    cancellationTokenSource.Token);

                if (badRecords.Count > 0)
                {
                    await emailNotificationService.SendBadRecordsSummaryEmail(
                        groupName,
                        taskName,
                        badRecords,
                        cancellationTokenSource.Token);
                }

                transferFileLogger.Info(groupName, taskName, $"END {taskName}.");
            }
            catch (Exception exception)
            {
                if (TransferErrorHelper.IsCriticalError(exception))
                {
                    var message = $"Kritična greška u tasku {taskName}. Trenutno izvršavanje se prekida do sledećeg zakazanog termina.";

                    await LogCritical(groupName, taskName, message, exception);

                    throw new CriticalTransferException(message, exception);
                }

                throw;
            }
        }
        private async Task TransferArtikli(int batchSize, string groupName, string taskName, bool executeDocumentCreation)
        {
            var badRecords = new List<BadRecordInfo>();
            var createdArticles = new List<CreatedArticleInfo>();

            artikliTransferService.LogAction =
                CreateLogAction(groupName, taskName);

            artikliTransferService.BadRecordAction =
                (table, key, data, message, ex) =>
                {
                    transferFileLogger.BadRecord(
                        groupName,
                        taskName,
                        table,
                        key,
                        data,
                        ex);

                    badRecords.Add(new BadRecordInfo
                    {
                        TableName = table,
                        Key = key,
                        Message = message,
                        Data = data,
                        Exception = ex.Message
                    });
                };

            artikliTransferService.CreatedArticleAction =
                article =>
                {
                    createdArticles.Add(article);
                };

            try
            {
                transferFileLogger.Info(
                    groupName,
                    taskName,
                    $"START {taskName}. BatchSize: {batchSize}");

                await artikliTransferService.TransferArtikliPaket(
                    batchSize,
                    cancellationTokenSource.Token);
                if (badRecords.Count == 0)
                {
                    transferFileLogger.Info(
                        groupName,
                        "KreiranjeDokumenata_CL_WMS",
                        "Artikli i cenovnik su završeni. Počinje kreiranje CL_WMS dokumenata.");

                    if (executeDocumentCreation && badRecords.Count == 0)
                    {
                        await ExecuteKreiranjeDokumenataClWms(groupName);
                    }
                    else if (!executeDocumentCreation)
                    {
                        transferFileLogger.Info(
                            groupName,
                            "KreiranjeDokumenata_CL_WMS",
                            "Kreiranje CL_WMS dokumenata je isključeno u konfiguraciji.");
                    }
                }
                else
                {
                    transferFileLogger.Info(
                        groupName,
                        "KreiranjeDokumenata_CL_WMS",
                        "Kreiranje dokumenata je preskočeno jer postoje neispravni redovi u transferu artikala.");
                }

                if (createdArticles.Count > 0)
                {
                    await emailNotificationService.SendCreatedArticlesSummaryEmail(
                            groupName,
                            taskName,
                            createdArticles,
                            cancellationTokenSource.Token);
                }

                if (badRecords.Count > 0)
                {
                    await emailNotificationService
                        .SendBadRecordsSummaryEmail(
                            groupName,
                            taskName,
                            badRecords,
                            cancellationTokenSource.Token);
                }

                transferFileLogger.Info(
                    groupName,
                    taskName,
                    $"END {taskName}.");
            }
            catch (Exception exception)
            {
                if (TransferErrorHelper.IsCriticalError(exception))
                {
                    var message =
                        $"Kritična greška u tasku {taskName}. " +
                        "Trenutno izvršavanje se prekida do sledećeg termina.";

                    await LogCritical(
                        groupName,
                        taskName,
                        message,
                        exception);

                    throw new CriticalTransferException(
                        message,
                        exception);
                }

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

            _ = Task.Run(async () =>
            {
                try
                {
                    await emailNotificationService.SendTaskErrorEmail(groupName, taskName, message, exception,cancellationTokenSource.Token);
                }
                catch (Exception emailException)
                {
                    transferFileLogger.Error(
                        groupName,
                        taskName,
                        "Greška pri slanju task error email obaveštenja.",
                        emailException);
                }
            });
        }

        private async Task LogCritical(string groupName, string taskName, string message, Exception exception)
        {
            var criticalMessage = $"CRITICAL: {message}";

            LogErrorAction?.Invoke(exception, criticalMessage);

            transferFileLogger.Error(groupName, taskName, criticalMessage, exception);

            try
            {
                await emailNotificationService.SendTaskErrorEmail(
                    groupName,
                    taskName,
                    criticalMessage,
                    exception,
                    CancellationToken.None,
                    "Task.CriticalError");
            }
            catch (Exception emailException)
            {
                transferFileLogger.Error(
                    groupName,
                    taskName,
                    "Greška pri slanju critical error email obaveštenja.",
                    emailException);
            }
        }

        private async Task ExecuteKreiranjeDokumenataClWms(string groupName)
        {
            const string taskName = "KreiranjeDokumenata_CL_WMS";

            var createdDocuments = new List<CreatedDocumentInfo>();

            var creationErrors = new List<BadRecordInfo>();

            documentCreationService_CL_WMS.LogAction = CreateLogAction(groupName, taskName);

            documentCreationService_CL_WMS.CreatedDocumentAction =
                document =>
                {
                    createdDocuments.Add(document);
                };

            documentCreationService_CL_WMS.CreationErrorAction =
                error =>
                {
                    creationErrors.Add(error);

                    transferFileLogger.BadRecord(groupName, taskName, error.TableName, error.Key, error.Data, new InvalidOperationException(error.Exception));
                };

            await documentCreationService_CL_WMS.Execute(orgJedLike: null, usePriceCalculation: true, maxDocuments: 2000, token: cancellationTokenSource.Token);

            if (createdDocuments.Count > 0)
            {
                await emailNotificationService.SendCreatedDocumentsSummaryEmail(groupName, taskName, createdDocuments, cancellationTokenSource.Token);
            }

            if (creationErrors.Count > 0)
            {
                await emailNotificationService.SendDocumentCreationErrorsSummaryEmail(groupName, taskName, creationErrors, cancellationTokenSource.Token);
            }
        }

        private async Task TransferVlpIzvSve(int batchSize, int daysBack, string groupName, string taskName)
        {
            var badRecords = new List<BadRecordInfo>();

            vlpIzvSveTransferService.LogAction = CreateLogAction(groupName, taskName);

            vlpIzvSveTransferService.BadRecordAction = (table, key, data, message, ex) =>
                {
                    transferFileLogger.BadRecord(groupName, taskName, table, key, data, ex);

                    badRecords.Add(new BadRecordInfo
                    {
                        TableName = table,
                        Key = key,
                        Message = message,
                        Data = data,
                        Exception = ex.Message
                    });
                };

            try
            {
                transferFileLogger.Info(groupName, taskName, $"START {taskName}. " + $"BatchSize: {batchSize}, DaysBack: {daysBack}");

                await vlpIzvSveTransferService.Transfer(batchSize, daysBack, cancellationTokenSource.Token);

                if (badRecords.Count > 0)
                {
                    await emailNotificationService.SendBadRecordsSummaryEmail(groupName, taskName, badRecords, cancellationTokenSource.Token);
                }                

                transferFileLogger.Info(groupName, taskName, $"END {taskName}.");
            }
            catch (Exception exception)
            {
                if (TransferErrorHelper.IsCriticalError(exception))
                {
                    var message =
                        $"Kritična greška u tasku {taskName}. " +
                        "Trenutno izvršavanje se prekida do sledećeg zakazanog termina.";

                    await LogCritical(groupName, taskName, message, exception);

                    throw new CriticalTransferException(message, exception);
                }

                throw;
            }
        }
       

    }
}
