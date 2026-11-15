using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Helpers;
using AswTransferToPantheon.Services.Interfaces;

namespace AswTransferToPantheon.Services.Implementation;

public partial class TaskSchedulerService
{
    private const string KifProcessingTaskName = "KifObrada";
    private readonly IKifDocumentProcessingService kifDocumentProcessingService;
    private readonly SemaphoreSlim kifExecutionGate = new(1, 1);

    private async Task TransferKifWithDocumentsAsync(
        int batchSize, string groupName, string taskName)
    {
        var token = cancellationTokenSource.Token;
        await kifExecutionGate.WaitAsync(token);

        try
        {
            var badRecords = new List<BadRecordInfo>();
            kifTransferService.LogAction = message => WriteKifLog(groupName, taskName, message);
            kifTransferService.BadRecordAction = (table, key, data, message, exception) =>
            {
                badRecords.Add(new BadRecordInfo
                {
                    TableName = table,
                    Key = key,
                    Data = data,
                    Message = message,
                    Exception = exception.Message
                });
                WriteKifBadRecord(groupName, taskName, table, key, data, exception);
            };

            try
            {
                WriteKifLog(groupName, taskName, $"START {taskName}. BatchSize: {batchSize}");
                await kifTransferService.Transfer(batchSize, token);
                token.ThrowIfCancellationRequested();
                WriteKifLog(groupName, taskName, "KIF prenos je završen.");

                // Transfer je završio sve pakete, SEO, aktivaciju identa i VLP dopunu.
                // Ne uslovljavamo nastavak brojem novih KIF-ova ili pojedinačnih grešaka.
                var result = await ExecuteKifDocumentProcessingAsync(groupName, token);

                // SQL obrada je završena pre slanja obaveštenja.
                if (badRecords.Count > 0)
                    await SendKifNotificationSafelyAsync(
                        groupName, taskName, $"{taskName}.BadRecords",
                        emailToken => emailNotificationService.SendBadRecordsSummaryEmail(
                            groupName, taskName, badRecords, emailToken), token);

                if (result is not null)
                {
                    if (result.HasErrors)
                        await SendKifNotificationSafelyAsync(
                            groupName, KifProcessingTaskName, "KifObrada.Errors",
                            emailToken => emailNotificationService.SendKifDocumentErrorsEmail(
                                groupName, KifProcessingTaskName, result, emailToken), token);

                    if (result.HasNewWork)
                        await SendKifNotificationSafelyAsync(
                            groupName, KifProcessingTaskName, "KifObrada.Completed",
                            emailToken => emailNotificationService.SendKifDocumentSuccessEmail(
                                groupName, KifProcessingTaskName, result, emailToken), token);
                }

                WriteKifLog(groupName, taskName, $"END {taskName}.");
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            catch (Exception exception)
            {
                if (!TransferErrorHelper.IsCriticalError(exception))
                    throw;

                const string message =
                    "Kritična greška u tasku Kif. Trenutno izvršavanje se prekida do sledećeg termina.";
                WriteKifProblem(groupName, taskName, "CRITICAL: " + message, exception);
                await SendKifNotificationSafelyAsync(
                    groupName, taskName, "Task.CriticalError",
                    emailToken => emailNotificationService.SendTaskErrorEmail(
                        groupName, taskName, message, exception, emailToken, "Task.CriticalError"), token);
                throw new CriticalTransferException(message, exception);
            }
            finally
            {
                kifTransferService.LogAction = null;
                kifTransferService.BadRecordAction = null;
            }
        }
        finally
        {
            kifExecutionGate.Release();
        }
    }

    private async Task<KifDocumentRunResult?> ExecuteKifDocumentProcessingAsync(
        string groupName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var settings = schedulerConfiguration.Value.KIFDocumentProcessing;
        if (settings is not { Enabled: true })
        {
            WriteKifLog(groupName, KifProcessingTaskName,
                "KIF kreiranje dokumenata i knjiženje isključeni su u konfiguraciji.");
            return null;
        }

        // Današnji datum uzimamo jednom, posle kompletnog prenosa.
        var datum = DateTime.Now.Date;
        KifDocumentRunResult result;
        try
        {
            result = await kifDocumentProcessingService.ExecuteAsync(
                datum, settings,
                message => WriteKifLog(groupName, KifProcessingTaskName, message), token);

            if (result is null || result.Datum.Date != datum)
                throw new InvalidOperationException(
                    "KIF servis nije vratio rezultat za traženi datum.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            result = new KifDocumentRunResult
            {
                Datum = datum,
                SamoProvera = settings.SamoProvera
            };
            result.Errors.Add(new KifProcessingError
            {
                Faza = "Poziv KIF obrade",
                Poruka = exception.Message +
                    " Nije dobijen kompletan rezultat; raniji potvrđeni upisi mogu ostati u bazi."
            });
            WriteKifProblem(groupName, KifProcessingTaskName,
                "KIF kreiranje i knjiženje nisu vratili očekivani rezultat.", exception);
        }

        token.ThrowIfCancellationRequested();

        if (result.HasErrors && result.Errors.Count == 0)
            result.Errors.Add(new KifProcessingError
            {
                Faza = "Provera odgovora",
                Poruka = "KIF obrada nema potvrđen uspešan završetak. Proverite rezultate procedura."
            });

        foreach (var error in result.Errors)
        {
            var key = $"Datum={datum:yyyy-MM-dd}; KIFID={error.KifId?.ToString() ?? "-"}; " +
                $"acKey={error.AcKey}";
            var details = $"Procedura={error.Procedura}; Faza={error.Faza}; " +
                $"Vrsta={error.AcDocType}; Broj={error.BrojGreske}; Linija={error.Linija}; " +
                error.Poruka;
            WriteKifBadRecord(groupName, KifProcessingTaskName,
                "KIF_Obrada", key, details, new InvalidOperationException(details));
            WriteKifLog(groupName, KifProcessingTaskName, $"KIF problem: {key}; {details}");
        }

        var created = result.Documents.Count(x => x.Status == "Kreiran");
        var posted = result.Postings.Count(x => x.Status == "Knjizen");
        var complete = result.DocumentsResponseComplete && result.PostingResponseComplete;
        var outcome = !complete ? "bez potvrđenog završetka"
            : result.HasErrors ? "završena sa greškama" : "završena";
        var createdText = result.DocumentsResponseComplete
            ? created.ToString() : $"nepoznato (primljeno uspešnih rezultata: {created})";
        var postedText = result.PostingResponseComplete
            ? posted.ToString() : $"nepoznato (primljeno uspešnih rezultata: {posted})";

        WriteKifLog(groupName, KifProcessingTaskName,
            $"KIF obrada {outcome}: datum {datum:dd.MM.yyyy}; SamoProvera={result.SamoProvera}; " +
            $"kreirano {createdText}; knjiženo {postedText}; prijavljenih problema {result.Errors.Count}.");

        return result;
    }

    private async Task SendKifNotificationSafelyAsync(
        string groupName, string taskName, string notificationKey,
        Func<CancellationToken, Task> send, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await send(deadline.Token).WaitAsync(deadline.Token);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            // Sledeće obaveštenje se pokušava zasebno; SQL se ne ponavlja.
            WriteKifProblem(groupName, taskName,
                $"Nije poslato KIF obaveštenje {notificationKey}.", exception);
        }
    }

    private void WriteKifLog(string groupName, string taskName, string message)
    {
        try { transferFileLogger.Info(groupName, taskName, message); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
        try { LogAction?.Invoke(message); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
    }

    private void WriteKifProblem(
        string groupName, string taskName, string message, Exception exception)
    {
        try { transferFileLogger.Error(groupName, taskName, message, exception); }
        catch (Exception logException) { System.Diagnostics.Debug.WriteLine(logException); }
        try { LogErrorAction?.Invoke(exception, message); }
        catch (Exception logException) { System.Diagnostics.Debug.WriteLine(logException); }
    }

    private void WriteKifBadRecord(
        string groupName, string taskName, string table, string key, string data, Exception exception)
    {
        try { transferFileLogger.BadRecord(groupName, taskName, table, key, data, exception); }
        catch (Exception logException)
        {
            WriteKifProblem(groupName, taskName, "Nije upisan KIF red u log.", logException);
        }
    }
}
