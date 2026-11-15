using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace AswTransferToPantheon.Services.Implementation;

public sealed partial class EmailNotificationService
{
    private const string KifBadRecordsNotification = "Kif.BadRecords";
    private readonly SemaphoreSlim kifBadRecordsMailGate = new(1, 1);
    private readonly TimeProvider emailHistoryClock = TimeProvider.System;
    private readonly TimeSpan emailHistoryTimeout = TimeSpan.FromSeconds(5);

    internal EmailNotificationService(
        IOptions<EmailConfiguration> configuration,
        EmailRecipientCache emailRecipientCache,
        EmailErrorHistoryService emailErrorHistory,
        ITransferFileLogger emailHistoryLogger,
        TimeProvider clock,
        TimeSpan historyTimeout)
        : this(configuration, emailRecipientCache, emailErrorHistory, emailHistoryLogger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (historyTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(historyTimeout));

        emailHistoryClock = clock;
        emailHistoryTimeout = historyTimeout;
    }

    private async Task SendKifBadRecordsWithHistoryAsync(
        string groupName,
        string taskName,
        string subject,
        List<BadRecordInfo> badRecords,
        CancellationToken token)
    {
        // Serijalizuje citanje, slanje i upis istorije u ovoj instanci aplikacije.
        await kifBadRecordsMailGate.WaitAsync(token);
        try
        {
            // Originalni redovi ostaju dostupni transferu i zapisivanju u log.
            var snapshot = badRecords.Select(record => new BadRecordInfo
            {
                TableName = record.TableName ?? "",
                Key = record.Key ?? "",
                Message = record.Message ?? "",
                Exception = record.Exception ?? "",
                Data = record.Data ?? ""
            }).ToList();

            List<BadRecordInfo> toSend;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var notificationDate = emailHistoryClock.GetLocalNow().Date;
                try
                {
                    toSend = await WithEmailHistoryTimeoutAsync(
                        historyToken => emailErrorHistory.GetRecordsToSendAsync(
                            "Transfer", "Kif", KifBadRecordsNotification,
                            snapshot, notificationDate, historyToken),
                        token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogEmailHistoryError(groupName, taskName,
                        "Kif.BadRecords: istorija nije procitana; " +
                        "mail se salje sa svim neispravnim redovima.", exception);
                    toSend = snapshot;
                }

                token.ThrowIfCancellationRequested();
                // Ako je tokom citanja prosla ponoc, proveriti za novi dan.
                if (emailHistoryClock.GetLocalNow().Date == notificationDate)
                    break;
            }

            if (toSend.Count == 0)
            {
                LogEmailHistoryInfo(groupName, taskName,
                    $"Kif.BadRecords: mail preskocen; svih {snapshot.Count} " +
                    "gresaka vec je slato ranijih dana.");
                return;
            }

            if (toSend.Count < snapshot.Count)
            {
                LogEmailHistoryInfo(groupName, taskName,
                    $"Kif.BadRecords: za mail {toSend.Count} gresaka; " +
                    $"izostavljeno ponovljenih iz ranijih dana {snapshot.Count - toSend.Count}.");
            }

            var body = BuildBadRecordsSummaryBody(groupName, taskName, toSend);
            token.ThrowIfCancellationRequested();
            var sent = await SendEmail(subject, body, taskName, token);
            if (!sent)
                return;

            // Belezi stvarni dan uspesnog slanja, ne dan pokretanja transfera.
            var sentAt = emailHistoryClock.GetLocalNow();
            try
            {
                // Mail je vec prihvacen. Pokusati kratak upis i ako se aplikacija gasi.
                await WithEmailHistoryTimeoutAsync(
                    async historyToken =>
                    {
                        await emailErrorHistory.RecordSuccessfulSendAsync(
                            "Transfer", "Kif", KifBadRecordsNotification,
                            toSend, sentAt, historyToken);
                        return true;
                    },
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogEmailHistoryError(groupName, taskName,
                    "Kif.BadRecords: mail je poslat, ali istorija slanja nije sacuvana. " +
                    "Ista greska moze ponovo stici u sledecem prolazu.", exception);
            }
        }
        finally
        {
            kifBadRecordsMailGate.Release();
        }
    }

    private async Task<T> WithEmailHistoryTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(emailHistoryTimeout);
        var pending = operation(timeout.Token);
        try
        {
            return await pending.WaitAsync(timeout.Token);
        }
        finally
        {
            // Neki mrezni pozivi mogu zavrsiti tek posle lokalnog timeout-a.
            // Njihov eventualni izuzetak mora biti preuzet.
            if (!pending.IsCompleted)
            {
                _ = pending.ContinueWith(
                    static task => { _ = task.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private void LogEmailHistoryInfo(string groupName, string taskName, string message)
    {
        try
        {
            emailHistoryLogger.Info(groupName, taskName, message);
        }
        catch
        {
            // Nedostupan log ne sme zaustaviti slanje maila.
        }
    }

    private void LogEmailHistoryError(
        string groupName, string taskName, string message, Exception exception)
    {
        try
        {
            emailHistoryLogger.Error(groupName, taskName, message, exception);
        }
        catch
        {
            // Ne slati dodatni mail o gresci istorije da se ne pravi novi krug obavestenja.
        }
    }
}

