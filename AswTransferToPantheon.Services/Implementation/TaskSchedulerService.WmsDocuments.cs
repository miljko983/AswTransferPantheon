using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Enums;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Helpers;
using AswTransferToPantheon.Services.Interfaces;

namespace AswTransferToPantheon.Services.Implementation;

public partial class TaskSchedulerService
{
    private readonly IWmsDocumentProcessingService wmsDocumentProcessingService;
    private readonly SemaphoreSlim naloziWmsExecutionGate = new(1, 1);
    private readonly SemaphoreSlim vlpWmsExecutionGate = new(1, 1);
    private readonly TimeSpan wmsEmailTimeout = TimeSpan.FromSeconds(30);

    private async Task TransferNaloziWithWmsAsync(NaloziTaskConfiguration naloziTask)
    {
        var token = cancellationTokenSource.Token;
        var taskName = naloziTask.Name;
        var groupName = System.IO.Path.Combine("NaloziTasks", taskName);
        if (!await naloziWmsExecutionGate.WaitAsync(0, token))
        {
            WriteWmsLog(groupName, taskName,
                "NALOZI/KUF prolaz je već u toku ili čeka knjiženje. Novi poziv je preskočen.");
            return;
        }

        var activeTaskName = taskName;
        try
        {
            var batchSize = naloziTask.BatchSize;
            var dateFrom = naloziTask.DateFrom;
            if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(naloziTask.BatchSize));
            var badRecords = new List<BadRecordInfo>();
            var kufBadRecords = new List<BadRecordInfo>();

            naloziTransferService.LogAction = message => WriteWmsLog(groupName, taskName, message);
            naloziTransferService.BadRecordAction = (table, key, data, message, exception) =>
                CaptureWmsBadRecord(badRecords, groupName, taskName, table, key, data, message, exception);
            kufTransferService.LogAction = message => WriteWmsLog(groupName, nameof(TaskType.Kuf), message);
            kufTransferService.BadRecordAction = (table, key, data, message, exception) =>
                CaptureWmsBadRecord(kufBadRecords, groupName, nameof(TaskType.Kuf), table, key, data, message, exception);
            naloziTransferService.KufTransferAction = async (ids, callbackToken) =>
            {
                activeTaskName = nameof(TaskType.Kuf);
                await kufTransferService.TransferForNalozi(ids, batchSize, callbackToken);
                activeTaskName = taskName;
            };

            WriteWmsLog(groupName, taskName,
                $"START {taskName}. BatchSize: {batchSize}, DatumOd: {dateFrom:yyyy-MM-dd}");
            await naloziTransferService.Transfer(batchSize, dateFrom, token);
            token.ThrowIfCancellationRequested();
            WriteWmsLog(groupName, taskName, "NALOZI i povezani KUF/VLPKUF: prenos svih paketa je završen.");

            // Samo jednom posle CELOG prenosa, nikada unutar KufTransferAction.
            // Pozivamo i kada nema novih redova: mogu postojati raniji neobrađeni nalozi.
            activeTaskName = "KufObrada";
            var result = await ExecuteWmsDocumentProcessingAsync("KUF", groupName, token);
            await SendWmsProcessingNotificationsAsync(groupName, result, token);

            if (kufBadRecords.Count > 0)
                await SendWmsNotificationSafelyAsync(groupName, nameof(TaskType.Kuf), "Kuf.BadRecords",
                    mailToken => emailNotificationService.SendBadRecordsSummaryEmail(
                        groupName, nameof(TaskType.Kuf), kufBadRecords, mailToken), token);
            if (badRecords.Count > 0)
                await SendWmsNotificationSafelyAsync(groupName, taskName, $"{taskName}.BadRecords",
                    mailToken => emailNotificationService.SendBadRecordsSummaryEmail(
                        groupName, taskName, badRecords, mailToken), token);

            WriteWmsLog(groupName, taskName, result is not null && !result.ResponseComplete
                ? $"END {taskName}: prenos završen; KUF knjiženje nema potvrđen kompletan završetak."
                : $"END {taskName}.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            exception.Data[FailedTaskNameKey] = activeTaskName;
            await ReportWmsCriticalTransferAsync(groupName, activeTaskName, exception, token);
            throw;
        }
        finally
        {
            naloziTransferService.KufTransferAction = null;
            naloziTransferService.LogAction = null;
            naloziTransferService.BadRecordAction = null;
            kufTransferService.LogAction = null;
            kufTransferService.BadRecordAction = null;
            naloziWmsExecutionGate.Release();
        }
    }

    private async Task TransferVlpWithWmsAsync(int batchSize, int daysBack, string groupName, string taskName)
    {
        var token = cancellationTokenSource.Token;
        if (!await vlpWmsExecutionGate.WaitAsync(0, token))
        {
            WriteWmsLog(groupName, taskName,
                "VLP prolaz je već u toku ili čeka knjiženje. Novi poziv je preskočen.");
            return;
        }
        try
        {
            if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
            var badRecords = new List<BadRecordInfo>();
            vlpIzvSveTransferService.LogAction = message => WriteWmsLog(groupName, taskName, message);
            vlpIzvSveTransferService.BadRecordAction = (table, key, data, message, exception) =>
                CaptureWmsBadRecord(badRecords, groupName, taskName, table, key, data, message, exception);

            WriteWmsLog(groupName, taskName, $"START {taskName}. BatchSize: {batchSize}, DaysBack: {daysBack}");
            await vlpIzvSveTransferService.Transfer(batchSize, daysBack, token);
            token.ThrowIfCancellationRequested();
            WriteWmsLog(groupName, taskName, "VLPIzvSve: prenos svih paketa je završen.");

            var result = await ExecuteWmsDocumentProcessingAsync("VLP", groupName, token);
            await SendWmsProcessingNotificationsAsync(groupName, result, token);
            if (badRecords.Count > 0)
                await SendWmsNotificationSafelyAsync(groupName, taskName, $"{taskName}.BadRecords",
                    mailToken => emailNotificationService.SendBadRecordsSummaryEmail(
                        groupName, taskName, badRecords, mailToken), token);

            WriteWmsLog(groupName, taskName, result is not null && !result.ResponseComplete
                ? $"END {taskName}: prenos završen; VLP knjiženje nema potvrđen kompletan završetak."
                : $"END {taskName}.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            await ReportWmsCriticalTransferAsync(groupName, taskName, exception, token);
            throw;
        }
        finally
        {
            vlpIzvSveTransferService.LogAction = null;
            vlpIzvSveTransferService.BadRecordAction = null;
            vlpWmsExecutionGate.Release();
        }
    }

    private async Task<WmsDocumentRunResult?> ExecuteWmsDocumentProcessingAsync(
        string kind, string groupName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var taskName = kind == "KUF" ? "KufObrada" : "VlpObrada";
        var configured = kind == "KUF"
            ? schedulerConfiguration.Value.KUFDocumentProcessing
            : schedulerConfiguration.Value.VLPDocumentProcessing;
        if (configured is not { Enabled: true })
        {
            WriteWmsLog(groupName, taskName, $"{kind} knjiženje isključeno je u konfiguraciji.");
            return null;
        }

        var settings = new WmsDocumentProcessingConfiguration
        {
            Enabled = configured.Enabled,
            DateFrom = configured.DateFrom.Date,
            UserId = configured.UserId,
            SamoProvera = configured.SamoProvera,
            CommandTimeoutSeconds = configured.CommandTimeoutSeconds
        };
        // Datum uzimamo jednom, tek nakon završetka celog odgovarajućeg prenosa.
        var datum = DateTime.Now.Date;
        WmsDocumentRunResult result;
        try
        {
            result = await wmsDocumentProcessingService.ExecuteAsync(kind, datum, settings,
                message => WriteWmsLog(groupName, taskName, message), token);
            if (result is null || result.Vrsta != kind || result.DatumOd != settings.DateFrom
                || result.DatumDo != datum || result.SamoProvera != settings.SamoProvera)
                throw new InvalidOperationException($"{kind} servis nije vratio rezultat za traženi period i režim.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            result = new WmsDocumentRunResult
            {
                Vrsta = kind,
                DatumOd = settings.DateFrom,
                DatumDo = datum,
                SamoProvera = settings.SamoProvera
            };
            result.Errors.Add(new WmsProcessingError
            {
                Procedura = WmsDocumentResultReader.ProcedureName,
                Faza = $"Poziv {kind} obrade",
                Poruka = exception.Message + " Nije dobijen kompletan rezultat. " +
                    "Ranije potvrđeni upisi mogu ostati u bazi."
            });
            WriteWmsProblem(groupName, taskName, $"{kind} obrada nije vratila očekivani rezultat.", exception);
        }

        token.ThrowIfCancellationRequested();
        if (result.Summary is null) result.ResponseComplete = false;
        if (result.HasErrors && result.Errors.Count == 0)
            result.Errors.Add(new WmsProcessingError
            {
                Procedura = WmsDocumentResultReader.ProcedureName,
                Faza = "Provera odgovora",
                Poruka = $"{kind} obrada nema potvrđen uspešan završetak ili nedostaju detalji prijavljenih problema."
            });
        foreach (var error in result.Errors)
        {
            var key = $"Period={result.DatumOd:yyyy-MM-dd}–{result.DatumDo:yyyy-MM-dd}; " +
                $"NalogID={error.NalogId}; acKey={error.AcKey}";
            var details = $"Procedura={error.Procedura}; Faza={error.Faza}; " +
                $"IzvornaVrsta={error.VrstaNaloga}; IzvorniBroj={error.Broj}; " +
                $"VrstaNaloga={error.AcDocType}; Broj={error.BrojGreske}; Linija={error.Linija}; {error.Poruka}";
            WriteWmsBadRecord(groupName, taskName, kind + "_Obrada", key, details, new InvalidOperationException(details));
            WriteWmsLog(groupName, taskName, $"{kind} problem: {key}; {details}");
        }

        var complete = result.ResponseComplete && result.Summary is not null;
        var created = result.Journals.Count(x => x.Status == "Kreiran");
        var createdText = complete ? created.ToString() : $"nepoznato (primljeno kreiranih naloga: {created})";
        var outcome = !complete ? "bez potvrđenog završetka" : result.HasErrors ? "završena sa greškama" : "završena";
        WriteWmsLog(groupName, taskName,
            $"{kind} obrada {outcome}: period {result.DatumOd:dd.MM.yyyy}–{result.DatumDo:dd.MM.yyyy}; " +
            $"SamoProvera={result.SamoProvera}; kreirano naloga {createdText}; prijavljenih problema {result.Errors.Count}.");
        return result;
    }

    private async Task SendWmsProcessingNotificationsAsync(
        string groupName, WmsDocumentRunResult? result, CancellationToken token)
    {
        if (result is null) return;
        var taskName = result.Vrsta == "KUF" ? "KufObrada" : "VlpObrada";
        // Greške i uspešni rezultati šalju se nezavisno. SMTP ne ponavlja SQL obradu.
        if (result.HasErrors)
            await SendWmsNotificationSafelyAsync(groupName, taskName, taskName + ".Errors",
                mailToken => emailNotificationService.SendWmsDocumentErrorsEmail(groupName, taskName, result, mailToken), token);
        if (result.HasNewWork)
            await SendWmsNotificationSafelyAsync(groupName, taskName, taskName + ".Completed",
                mailToken => emailNotificationService.SendWmsDocumentSuccessEmail(groupName, taskName, result, mailToken), token);
    }

    private async Task SendWmsNotificationSafelyAsync(
        string groupName, string taskName, string key, Func<CancellationToken, Task> send, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(wmsEmailTimeout);
            await send(deadline.Token).WaitAsync(deadline.Token);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            WriteWmsProblem(groupName, taskName, $"Nije poslato obaveštenje {key}.", exception);
        }
    }

    private async Task ReportWmsCriticalTransferAsync(
        string groupName, string taskName, Exception exception, CancellationToken token)
    {
        if (!TransferErrorHelper.IsCriticalError(exception)) return;
        var message = $"Kritična greška u tasku {taskName}. " +
            "Trenutno izvršavanje se prekida do sledećeg termina; naredna WMS obrada nije pokrenuta.";
        WriteWmsProblem(groupName, taskName, "CRITICAL: " + message, exception);
        await SendWmsNotificationSafelyAsync(groupName, taskName, "Task.CriticalError",
            mailToken => emailNotificationService.SendTaskErrorEmail(
                groupName, taskName, message, exception, mailToken, "Task.CriticalError"), token);
        throw new CriticalTransferException(message, exception);
    }

    private void CaptureWmsBadRecord(List<BadRecordInfo> records, string groupName,
        string taskName, string table, string key, string data, string message, Exception exception)
    {
        records.Add(new BadRecordInfo
        {
            TableName = table,
            Key = key,
            Data = data,
            Message = message,
            Exception = exception.Message
        });
        WriteWmsBadRecord(groupName, taskName, table, key, data, exception);
    }

    private void WriteWmsLog(string groupName, string taskName, string message)
    {
        try { transferFileLogger.Info(groupName, taskName, message); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
        try { LogAction?.Invoke(message); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
    }

    private void WriteWmsProblem(string groupName, string taskName, string message, Exception exception)
    {
        try { transferFileLogger.Error(groupName, taskName, message, exception); }
        catch (Exception logException) { System.Diagnostics.Debug.WriteLine(logException); }
        try { LogErrorAction?.Invoke(exception, message); }
        catch (Exception logException) { System.Diagnostics.Debug.WriteLine(logException); }
    }

    private void WriteWmsBadRecord(string groupName, string taskName,
        string table, string key, string data, Exception exception)
    {
        try { transferFileLogger.BadRecord(groupName, taskName, table, key, data, exception); }
        catch (Exception logException)
        { WriteWmsProblem(groupName, taskName, "Nije upisan neispravan red u log.", logException); }
    }
}
