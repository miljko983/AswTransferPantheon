using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Helpers;

namespace AswTransferToPantheon.Services.Implementation;

public partial class TaskSchedulerService
{
    private const string BoTaskName = "BODokumenti";

    private readonly SemaphoreSlim boExecutionGate = new(1, 1);
    private int boSchedulesStarted;

    private async Task ScheduleBoDocumentTasks(
        List<BoDocumentTaskConfiguration> configurations)
    {
        // Sprečava ponovno pokretanje istih BO rasporeda.
        if (Interlocked.Exchange(ref boSchedulesStarted, 1) != 0)
            return;

        var names = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var configuration in configurations)
        {
            if (cancellationTokenSource.IsCancellationRequested)
                return;

            try
            {
                if (configuration is null)
                    throw new ArgumentException(
                        "BO raspored ne sme biti null.");

                if (!configuration.Enabled)
                    continue;

                BoDocumentSchedule.Validate(configuration);

                if (!names.Add(configuration.Name))
                {
                    throw new ArgumentException(
                        $"BO raspored '{configuration.Name}' " +
                        "je naveden više puta.");
                }

                _ = ScheduleBoDocumentTask(configuration);
            }
            catch (Exception exception)
            {
                var group = Path.Combine(
                    "BODocumentTasks",
                    "Configuration");

                transferFileLogger.Error(
                    group,
                    BoTaskName,
                    "Neispravan BO raspored.",
                    exception);

                LogErrorAction?.Invoke(
                    exception,
                    "Neispravan BO raspored.");

                await SendBoNotification(
                    group,
                    new BoDocumentRunResult
                    {
                        Datum = GetBoNow().Date,
                        Status = "ConfigurationError",
                        ExecutionException = exception,
                        SamoProvera = configuration?.SamoProvera ?? false
                    },
                    cancellationTokenSource.Token);
            }
        }
    }

    private async Task ScheduleBoDocumentTask(BoDocumentTaskConfiguration configuration)
    {
        var token = cancellationTokenSource.Token;

        var group = Path.Combine("BODocumentTasks", configuration.Name);

        var log = CreateLogAction(group, BoTaskName);

        var firstCall = true;
        DateTime? lastAttemptAt = null;

        while (!token.IsCancellationRequested)
        {
            DateTime? executionDate = null;

            try
            {
                var next = BoDocumentSchedule.NextRun(
                    configuration,
                    GetBoNow(),
                    firstCall,
                    lastAttemptAt);

                firstCall = false;

                log(
                    $"BO raspored {configuration.Name}: " +
                    $"sledeće izvršavanje {next:dd.MM.yyyy HH:mm:ss}.");

                await WaitForBoTime(next, token);

                // BO obrade se ne izvršavaju paralelno.
                await boExecutionGate.WaitAsync(token);

                try
                {
                    token.ThrowIfCancellationRequested();

                    var now = GetBoNow();

                    // Posle čekanja ponovo proveravamo datum i termin.
                    if (now < next
                        || now.Date != next.Date
                        || !BoDocumentSchedule.IsAllowedDay(
                            configuration, now))
                    {
                        continue;
                    }

                    if (lastAttemptAt.HasValue
                        && (next <= lastAttemptAt.Value
                            || (!configuration.PeriodInMinutes.HasValue
                                && now.Date == lastAttemptAt.Value.Date)))
                    {
                        continue;
                    }

                    // Pamti se vreme pokušaja i kada obrada prijavi grešku.
                    lastAttemptAt = now;
                    executionDate = now.Date;

                    await ExecuteBoDocuments(
                        configuration,
                        executionDate.Value,
                        group,
                        token);
                }
                finally
                {
                    boExecutionGate.Release();
                }
            }
            catch (OperationCanceledException)
                when (token.IsCancellationRequested)
            {
                transferFileLogger.Info(
                    group,
                    BoTaskName,
                    "BO raspored je zaustavljen.");

                return;
            }
            catch (Exception exception)
            {
                var errorAt = GetBoNow();

                // Ako poziv nije započet, evidentiramo vreme greške rasporeda.
                if (!executionDate.HasValue)
                    lastAttemptAt = errorAt;

                transferFileLogger.Error(
                    group,
                    BoTaskName,
                    "Greška BO rasporeda.",
                    exception);

                LogErrorAction?.Invoke(
                    exception,
                    "Greška BO rasporeda.");

                await SendBoNotification(
                    group,
                    new BoDocumentRunResult
                    {
                        Datum = executionDate ?? errorAt.Date,
                        Status = "Failed",
                        ExecutionException = exception,
                        SamoProvera = configuration.SamoProvera
                    },
                    token);
            }
        }
    }

    private DateTime GetBoNow() => DateTime.Now;

    private async Task WaitForBoTime(
        DateTime next,
        CancellationToken token)
    {
        while (next > GetBoNow())
        {
            var delay = next - GetBoNow();

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(
                    delay > TimeSpan.FromMinutes(1)
                        ? TimeSpan.FromMinutes(1)
                        : delay,
                    token);
            }
        }

        token.ThrowIfCancellationRequested();
    }

    private async Task ExecuteBoDocuments(
        BoDocumentTaskConfiguration configuration,
        DateTime datum,
        string group,
        CancellationToken token)
    {
        var log = CreateLogAction(group, BoTaskName);

        log(
            $"BO početak: datum {datum:dd.MM.yyyy}, " +
            $"SamoProvera={configuration.SamoProvera}, " +
            $"paket {configuration.BatchSize}.");

        var result = await boDocumentImportService.ExecuteAsync(
            datum,
            configuration,
            log,
            token);

        if (result.ExecutionException is { } exception)
        {
            transferFileLogger.Error(
                group,
                BoTaskName,
                $"BO ImportId={result.ImportId}: " +
                "poziv nije potpuno završen.",
                exception);

            LogErrorAction?.Invoke(
                exception,
                $"BO obrada za {datum:dd.MM.yyyy} prijavila je grešku.");
        }

        foreach (var error in result.Errors)
        {
            transferFileLogger.BadRecord(
                group,
                BoTaskName,
                "_tb_BOImportGreske",
                $"ImportId={result.ImportId}; " +
                $"Dokument={error.Dokument}; Ident={error.Ident}",
                $"Faza={error.Faza}; Broj={error.BrojGreske}; " +
                error.Poruka,
                new InvalidOperationException(
                    $"{error.Poruka} " +
                    $"({error.Procedura}, linija {error.Linija})"));
        }

        var outcome = !result.ResponseComplete
            ? "bez potvrđenog završetka"
            : result.HasErrors
                ? "završeno sa prijavljenim problemima"
                : "završeno";

        var errorCount = result.Summary?.BrojGresaka?.ToString()
            ?? (result.Errors.Count > 0
                ? result.Errors.Count.ToString()
                : "nepoznato");

        log(
            $"BO {outcome}: datum {datum:dd.MM.yyyy}, " +
            $"ImportId={result.ImportId?.ToString() ?? "-"}, " +
            $"status {result.Status}, " +
            $"kreirano dokumenata " +
            $"{result.Summary?.KreiranoDokumenata?.ToString() ?? "nepoznato"}, " +
            $"stavki " +
            $"{result.Summary?.KreiranoStavki?.ToString() ?? "nepoznato"}, " +
            $"knjiženo " +
            $"{result.Summary?.KnjizenoDokumenata?.ToString() ?? "nepoznato"}, " +
            $"grešaka {errorCount}.");

        await SendBoNotification(group, result, token);
    }

    private async Task SendBoNotification(
        string group,
        BoDocumentRunResult result,
        CancellationToken token)
    {
        try
        {
            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(token);

            deadline.CancelAfter(TimeSpan.FromSeconds(30));

            await emailNotificationService.SendBoDocumentRunEmail(
                group,
                BoTaskName,
                result,
                deadline.Token);
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            // Namerno zaustavljanje aplikacije.
        }
        catch (Exception exception)
        {
            // Greška slanja maila ne pokreće ponovo uvoz.
            transferFileLogger.Error(
                group,
                BoTaskName,
                "Nije poslato BO email obaveštenje.",
                exception);

            if (!token.IsCancellationRequested)
            {
                LogErrorAction?.Invoke(
                    exception,
                    "Nije poslato BO email obaveštenje.");
            }
        }
    }
}