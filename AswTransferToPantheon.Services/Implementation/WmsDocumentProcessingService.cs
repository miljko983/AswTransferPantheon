using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Helpers;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class WmsDocumentProcessingService : IWmsDocumentProcessingService
{
    // KUF i VLP dele istu SQL bravu i ne pokreću se paralelno u ovoj aplikaciji.
    private static readonly SemaphoreSlim ProcessingGate = new(1, 1);
    private const string LockResource = "WMS_KUF_VLP_Nalozi";
    private static readonly TimeSpan LockCheckInterval = TimeSpan.FromSeconds(15);

    private readonly ConnectionStrings connectionStrings;
    private readonly Func<SqlCommand, CancellationToken, Task<DbDataReader>> openReader;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public WmsDocumentProcessingService(IOptions<ConnectionStrings> connectionStrings)
        : this(connectionStrings, OpenSqlReaderAsync)
    {
    }

    // Provera ponašanja bez pozivanja poslovne baze.
    internal WmsDocumentProcessingService(
        IOptions<ConnectionStrings> connectionStrings,
        Func<SqlCommand, CancellationToken, Task<DbDataReader>> openReader,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(connectionStrings);
        ArgumentNullException.ThrowIfNull(openReader);
        this.connectionStrings = connectionStrings.Value;
        this.openReader = openReader;
        this.delay = delay ?? Task.Delay;
    }

    public async Task<WmsDocumentRunResult> ExecuteAsync(
        string vrsta,
        DateTime datum,
        WmsDocumentProcessingConfiguration configuration,
        Action<string> log,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(log);
        token.ThrowIfCancellationRequested();

        // Opseg i režim ostaju isti i ako čekanje/obrada potraju preko ponoći.
        var settings = new WmsDocumentProcessingConfiguration
        {
            Enabled = configuration.Enabled,
            DateFrom = configuration.DateFrom.Date,
            UserId = configuration.UserId,
            SamoProvera = configuration.SamoProvera,
            CommandTimeoutSeconds = configuration.CommandTimeoutSeconds
        };
        var result = new WmsDocumentRunResult
        {
            Vrsta = (vrsta ?? "").Trim().ToUpperInvariant(),
            DatumOd = settings.DateFrom,
            DatumDo = datum.Date,
            SamoProvera = settings.SamoProvera
        };

        var watch = Stopwatch.StartNew();
        var gateHeld = false;
        var procedureAttempted = false;
        var phase = "Provera podešavanja";

        try
        {
            Validate(result, settings);
            var connectionString = BuildConnectionString();
            phase = "Čekanje KUF/VLP obrade u aplikaciji";
            gateHeld = ProcessingGate.Wait(0);
            if (!gateHeld)
            {
                WriteLog(log, $"{result.Vrsta} čeka završetak prethodne KUF/VLP obrade u aplikaciji.");
                await ProcessingGate.WaitAsync(token);
                gateHeld = true;
            }

            WriteLog(log, $"{result.Vrsta} knjiženje: period {result.DatumOd:dd.MM.yyyy}–" +
                $"{result.DatumDo:dd.MM.yyyy}; SamoProvera={settings.SamoProvera}.");

            var busyRaceLogged = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                phase = "Provera SQL brave";
                await WaitForAvailabilityAsync(connectionString, result.Vrsta, log, token);
                phase = $"Knjiženje {result.Vrsta} naloga";
                var responseStarted = false;
                try
                {
                    // Procedura sama potvrđuje svaki nalog. Nema spoljne transakcije.
                    // Zatvaranje nepulovane veze oslobađa njene Session brave.
                    await using var connection = new SqlConnection(connectionString);
                    using var command = CreateCommand(connection, result, settings);
                    procedureAttempted = true;
                    await using var reader = await openReader(command, token);
                    responseStarted = true;
                    await WmsDocumentResultReader.ReadAsync(reader, result, token);
                }
                catch (SqlException exception) when (!token.IsCancellationRequested
                    && !responseStarted && IsBusyBeforeExecution(exception))
                {
                    // APPLOCK_TEST nije rezervacija. Procedura može odbiti početak
                    // ako je druga sesija zauzela bravu između provere i poziva.
                    // Isti SQL broj postoji i za druge probleme sa bravom.
                    procedureAttempted = false;
                    phase = "Provera SQL brave posle odbijenog početka";
                    if (await IsAvailableAsync(connectionString, token))
                        throw;

                    if (!busyRaceLogged)
                    {
                        WriteLog(log, $"{result.Vrsta}: SQL brava {LockResource} je zauzeta. " +
                            "Procedura nije započela obradu; čeka se oslobađanje brave.");
                        busyRaceLogged = true;
                    }
                    await delay(LockCheckInterval, token);
                    continue;
                }
                break;
            }

            if (!result.ResponseComplete || result.Summary is null)
                throw new InvalidOperationException("Nedostaje kompletan odgovor WMS knjiženja.");

            var summary = result.Summary;
            WriteLog(log, $"{result.Vrsta} obrada završena: period {result.DatumOd:dd.MM.yyyy}–" +
                $"{result.DatumDo:dd.MM.yyyy}; ImportId={summary.ImportId}; " +
                $"izabrano {summary.IzabranoNaloga}; obrađeno {summary.ObradjenoNaloga}; " +
                $"kreirano naloga {summary.KreiranoNaloga}; predloženo {summary.PredlozenoNaloga}; " +
                $"preskočeno {summary.PreskocenoNaloga}; grešaka {summary.BrojGresaka}; " +
                $"naloga sa razlikom {summary.NalogaSaRazlikom}.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            result.ResponseComplete = false;
            var suffix = procedureAttempted
                ? " Završetak poziva nije potvrđen. Ranije potvrđeni upisi mogu ostati u bazi. " +
                  "Ovaj poziv se ne ponavlja automatski."
                : " Obrada podataka ovim pozivom nije pokrenuta.";

            if (exception is SqlException sqlException)
            {
                var errors = sqlException.Errors.Cast<SqlError>()
                    .Where(error => error.Number != 3621)
                    .DistinctBy(error => (error.Number, error.Procedure, error.LineNumber, error.Message))
                    .ToArray();
                if (errors.Length == 0)
                    errors = sqlException.Errors.Cast<SqlError>().ToArray();
                foreach (var error in errors)
                    result.Errors.Add(new WmsProcessingError
                    {
                        Procedura = string.IsNullOrWhiteSpace(error.Procedure)
                            ? WmsDocumentResultReader.ProcedureName : error.Procedure,
                        Faza = phase,
                        BrojGreske = error.Number,
                        Linija = error.LineNumber > 0 ? error.LineNumber : null,
                        Poruka = error.Message + suffix
                    });
            }
            else
                result.Errors.Add(new WmsProcessingError
                {
                    Procedura = WmsDocumentResultReader.ProcedureName,
                    Faza = phase,
                    Poruka = exception.Message + suffix
                });

            WriteLog(log, $"{result.Vrsta} obrada bez potvrđenog završetka: {phase} — " +
                exception.Message + suffix);
        }
        finally
        {
            if (gateHeld) ProcessingGate.Release();
            watch.Stop();
            result.Duration = watch.Elapsed;
        }

        return result;
    }

    private async Task WaitForAvailabilityAsync(
        string connectionString, string vrsta, Action<string> log, CancellationToken token)
    {
        var waitingLogged = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (await IsAvailableAsync(connectionString, token))
            {
                if (waitingLogged)
                    WriteLog(log, $"{vrsta}: SQL brava je slobodna; nastavlja se obrada.");
                return;
            }
            if (!waitingLogged)
            {
                WriteLog(log, $"{vrsta} čeka: SQL brava {LockResource} je zauzeta. " +
                    "Nova procedura nije pokrenuta. Provera se ponavlja na 15 sekundi.");
                waitingLogged = true;
            }
            await delay(LockCheckInterval, token);
        }
    }

    private async Task<bool> IsAvailableAsync(string connectionString, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = """
            SELECT CONVERT(int, APPLOCK_TEST(N'public', N'WMS_KUF_VLP_Nalozi',
                N'Exclusive', N'Session')) AS Available;
            """;
        await using var reader = await openReader(command, token);
        if (!await reader.ReadAsync(token) || reader.FieldCount != 1 || reader.IsDBNull(0))
            throw new InvalidOperationException("Nedostaje rezultat provere WMS SQL brave.");
        var available = reader.GetInt32(0);
        if (available is not (0 or 1) || await reader.ReadAsync(token)
            || await reader.NextResultAsync(token))
            throw new InvalidOperationException("Neispravan rezultat provere WMS SQL brave.");
        return available == 1;
    }

    private static bool IsBusyBeforeExecution(SqlException exception)
    {
        const string procedure = WmsDocumentResultReader.ProcedureName;
        var errors = exception.Errors.Cast<SqlError>().ToArray();
        bool Matches(SqlError error) => error.Number == 51208
            && error.Message == "Uvoz WMS naloga je vec pokrenut."
            && (error.Procedure == procedure || error.Procedure == procedure[4..]);
        return errors.Any(Matches)
            && errors.All(error => Matches(error) || error.Number == 3621);
    }

    private static SqlCommand CreateCommand(SqlConnection connection,
        WmsDocumentRunResult result, WmsDocumentProcessingConfiguration settings)
    {
        var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = WmsDocumentResultReader.ProcedureName;
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        command.Parameters.Add("@acVrsta", SqlDbType.VarChar, 3).Value = result.Vrsta;
        command.Parameters.Add("@DatumOd", SqlDbType.Date).Value = result.DatumOd;
        command.Parameters.Add("@DatumDo", SqlDbType.Date).Value = result.DatumDo;
        var nalog = command.Parameters.Add("@NalogID", SqlDbType.Decimal);
        nalog.Precision = 38;
        nalog.Scale = 0;
        nalog.Value = DBNull.Value;
        command.Parameters.Add("@anUserID", SqlDbType.Int).Value = settings.UserId;
        command.Parameters.Add("@SamoProvera", SqlDbType.Bit).Value = settings.SamoProvera;
        // Detaljni SELECT rezultati sadrže i ImportId i brojače; OUTPUT parametri
        // procedure imaju podrazumevane vrednosti i ovde nisu potrebni.
        command.Parameters.Add("@AresMode", SqlDbType.Bit).Value = false;
        return command;
    }

    private string BuildConnectionString()
    {
        if (string.IsNullOrWhiteSpace(connectionStrings.Transfer))
            throw new InvalidOperationException("Nedostaje Transfer konekcija.");
        return new SqlConnectionStringBuilder(connectionStrings.Transfer)
        {
            InitialCatalog = "CENTROSINERGIJA",
            Pooling = false,
            Enlist = false,
            MultipleActiveResultSets = false
        }.ConnectionString;
    }

    private static void Validate(WmsDocumentRunResult result,
        WmsDocumentProcessingConfiguration settings)
    {
        if (result.Vrsta is not ("KUF" or "VLP"))
            throw new InvalidOperationException("Vrsta WMS obrade mora biti KUF ili VLP.");
        if (!settings.Enabled)
            throw new InvalidOperationException($"{result.Vrsta} knjiženje nije uključeno u konfiguraciji.");
        if (settings.UserId <= 0)
            throw new InvalidOperationException("WMS UserId mora biti veći od nule.");
        if (settings.CommandTimeoutSeconds < 0)
            throw new InvalidOperationException("WMS CommandTimeoutSeconds mora biti 0 (bez ograničenja) ili veći od nule.");
        if (result.DatumOd < new DateTime(1753, 1, 1)
            || result.DatumDo >= DateTime.MaxValue.Date || result.DatumOd > result.DatumDo)
            throw new InvalidOperationException("Neispravan period WMS obrade: DateFrom mora biti pre ili na dan izvršavanja.");
    }

    private static async Task<DbDataReader> OpenSqlReaderAsync(SqlCommand command, CancellationToken token)
    {
        await command.Connection!.OpenAsync(token);
        return await command.ExecuteReaderAsync(token);
    }

    private static void WriteLog(Action<string> log, string message)
    {
        try { log(message); }
        catch (Exception exception)
        {
            Debug.WriteLine($"WMS log nije upisan: {exception.Message}");
        }
    }
}
