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

public sealed class KifDocumentProcessingService : IKifDocumentProcessingService
{
    private readonly ConnectionStrings connectionStrings;
    private readonly Func<SqlCommand, CancellationToken, Task<DbDataReader>> openReader;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public KifDocumentProcessingService(IOptions<ConnectionStrings> connectionStrings)
        : this(connectionStrings, OpenSqlReaderAsync)
    {
    }

    // Omogućava proveru obrade odgovora bez povezivanja na bazu.
    internal KifDocumentProcessingService(
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

    public async Task<KifDocumentRunResult> ExecuteAsync(
        DateTime datum,
        KifDocumentProcessingConfiguration configuration,
        Action<string> log,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(log);
        token.ThrowIfCancellationRequested();

        // Isti datum i podešavanja važe za oba poziva, čak i preko ponoći.
        var settings = new KifDocumentProcessingConfiguration
        {
            Enabled = configuration.Enabled,
            UserId = configuration.UserId,
            SamoProvera = configuration.SamoProvera,
            PrikaziNapredak = configuration.PrikaziNapredak,
            CommandTimeoutSeconds = configuration.CommandTimeoutSeconds
        };
        var result = new KifDocumentRunResult
        {
            Datum = datum.Date,
            SamoProvera = settings.SamoProvera
        };

        var watch = Stopwatch.StartNew();
        var procedure = "";
        var phase = "Provera podešavanja";

        try
        {
            Validate(result.Datum, settings);
            var connectionString = BuildConnectionString();

            procedure = KifDocumentResultReader.DocumentsProcedure;
            phase = "Kreiranje KIF dokumenata";
            WriteLog(log, $"KIF kreiranje dokumenata: period 01.08.2026–{result.Datum:dd.MM.yyyy}; " +
                $"SamoProvera={settings.SamoProvera}.");

            await ExecuteProcedureAsync(
                connectionString, procedure, result, settings, log, token);

            if (!result.DocumentsResponseComplete || result.Summary is null)
                throw new InvalidOperationException("Nedostaje kompletan odgovor kreiranja KIF dokumenata.");

            WriteLog(log,
                $"KIF kreiranje: izabrano {result.Summary.IzabranoKIF}; " +
                $"kreirano {result.Summary.Kreirano}; predloženo {result.Summary.Predlozeno}; " +
                $"grešaka {result.Summary.Gresaka}.");

            token.ThrowIfCancellationRequested();
            procedure = KifDocumentResultReader.PostingProcedure;
            phase = "Knjiženje KIF dokumenata";
            WriteLog(log, $"KIF knjiženje: period 01.08.2026–{result.Datum:dd.MM.yyyy}; " +
                $"SamoProvera={settings.SamoProvera}.");

            // Poziva se i kada nema novih dokumenata: mogu postojati neproknjiženi.
            // Poslovne greške pojedinačnih dokumenata ne blokiraju ostale.
            await ExecuteProcedureAsync(
                connectionString, procedure, result, settings, log, token);

            if (!result.PostingResponseComplete)
                throw new InvalidOperationException("Nedostaje kompletan odgovor KIF knjiženja.");

            WriteLog(log,
                $"KIF knjiženje: knjiženo {result.Postings.Count(x => x.Status == "Knjizen")}; " +
                $"predloženo {result.Postings.Count(x => x.Status == "Predlog")}; " +
                $"preskočeno {result.Postings.Count(x => x.Status == "Preskocen")}; " +
                $"grešaka {result.Postings.Count(x => x.Status == "Greska")}.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Gašenje aplikacije ne pokreće sledeću proceduru niti novi pokušaj.
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            if (procedure == KifDocumentResultReader.DocumentsProcedure)
                result.DocumentsResponseComplete = false;
            else if (procedure == KifDocumentResultReader.PostingProcedure)
                result.PostingResponseComplete = false;

            var sqlException = exception as SqlException;
            var message = exception.Message;
            if (procedure.Length > 0)
                message += " Odgovor ove procedure nije kompletan. " +
                    "Raniji potvrđeni upisi mogu ostati u bazi.";

            result.Errors.Add(new KifProcessingError
            {
                Procedura = !string.IsNullOrWhiteSpace(sqlException?.Procedure)
                    ? sqlException!.Procedure : procedure,
                Faza = phase,
                BrojGreske = sqlException?.Number,
                Linija = sqlException is { LineNumber: > 0 } ? sqlException.LineNumber : null,
                Poruka = message
            });
            WriteLog(log, $"KIF obrada: {phase} — {message}");
        }
        finally
        {
            watch.Stop();
            result.Duration = watch.Elapsed;
        }

        return result;
    }

    private async Task ExecuteProcedureAsync(
        string connectionString,
        string procedure,
        KifDocumentRunResult result,
        KifDocumentProcessingConfiguration settings,
        Action<string> log,
        CancellationToken token)
    {
        var busyRaceLogged = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await WaitForKifAvailabilityAsync(connectionString, log, token);

            var responseStarted = false;
            try
            {
                // Svaka procedura dobija svoju vezu i sama upravlja transakcijama.
                await using var connection = new SqlConnection(connectionString);
                connection.InfoMessage += (_, args) =>
                {
                    foreach (SqlError message in args.Errors)
                        WriteLog(log, message.Message);
                };

                using var command = CreateCommand(connection, procedure, result.Datum, settings);
                await using var reader = await openReader(command, token);
                responseStarted = true;

                if (procedure == KifDocumentResultReader.DocumentsProcedure)
                    await KifDocumentResultReader.ReadDocumentsAsync(reader, result, token);
                else
                    await KifDocumentResultReader.ReadPostingsAsync(reader, result, token);
                return;
            }
            catch (SqlException exception) when (!token.IsCancellationRequested && !responseStarted
                && IsBusyBeforeExecution(exception, procedure))
            {
                // Starije SQL procedure koriste isti broj i za druge neuspehe
                // sp_getapplock. Bez potvrđene zauzetosti ne skrivamo grešku.
                if ((await GetBusyKifResourceAsync(connectionString, token)).Length == 0)
                    throw;
                // APPLOCK_TEST nije rezervacija. Ako drugi poziv zauzme bravu
                // između provere i EXEC, sama procedura sprečava paralelan upis.
                // Ponavlja se samo njeno poznato odbijanje PRE obrade podataka.
                if (!busyRaceLogged)
                {
                    WriteLog(log, $"KIF: {procedure} je odbila početak jer je SQL brava zauzeta. " +
                        "Čeka se oslobađanje; zauzetost se ne šalje kao greška na mail.");
                    busyRaceLogged = true;
                }
            }
            await delay(TimeSpan.FromSeconds(15), token);
        }
    }

    private async Task WaitForKifAvailabilityAsync(
        string connectionString, Action<string> log, CancellationToken token)
    {
        var lastBusyResource = "";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var busyResource = await GetBusyKifResourceAsync(connectionString, token);
            if (busyResource.Length == 0)
            {
                if (lastBusyResource.Length > 0)
                    WriteLog(log, "KIF: SQL brave su slobodne; nastavlja se obrada.");
                return;
            }
            if (busyResource != lastBusyResource)
            {
                WriteLog(log, $"KIF čeka: SQL brava {busyResource} je zauzeta. " +
                    "Nova procedura nije pokrenuta. Provera se ponavlja na 15 sekundi.");
                lastBusyResource = busyResource;
            }
            await delay(TimeSpan.FromSeconds(15), token);
        }
    }

    private async Task<string> GetBusyKifResourceAsync(
        string connectionString, CancellationToken token)
    {
        // Provera vidi brave i drugih aplikacija/SSMS sesija u CENTROSINERGIJI.
        // Ne zauzima niti oslobađa njihove brave.
        await using var connection = new SqlConnection(connectionString);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = """
            SELECT
                CONVERT(int, APPLOCK_TEST(N'public', N'WMS_KIF_Move',
                    N'Exclusive', N'Session')) AS CreationAvailable,
                CONVERT(int, APPLOCK_TEST(N'public', N'KIF_Move_Knjizenje',
                    N'Exclusive', N'Session')) AS PostingAvailable;
            """;
        await using var reader = await openReader(command, token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0) || reader.IsDBNull(1))
            throw new InvalidOperationException("Nije dobijen rezultat provere KIF SQL brava.");
        var creationAvailable = reader.GetInt32(0);
        var postingAvailable = reader.GetInt32(1);
        if (creationAvailable is not (0 or 1) || postingAvailable is not (0 or 1)
            || await reader.ReadAsync(token) || await reader.NextResultAsync(token))
            throw new InvalidOperationException("Neispravan rezultat provere KIF SQL brava.");
        if (postingAvailable == 0) return "KIF_Move_Knjizenje";
        return creationAvailable == 0 ? "WMS_KIF_Move" : "";
    }

    private static bool IsBusyBeforeExecution(SqlException exception, string procedure)
    {
        var posting = procedure == KifDocumentResultReader.PostingProcedure;
        var creation = procedure == KifDocumentResultReader.DocumentsProcedure;
        if (!posting && !creation) return false;
        var number = posting ? 51602 : 51305;
        var message = posting ? "KIF knjizenje je vec pokrenuto." : "KIF uvoz je vec pokrenut.";
        var name = procedure.StartsWith("dbo.", StringComparison.Ordinal)
            ? procedure[4..] : procedure;
        var errors = exception.Errors.Cast<SqlError>().ToArray();
        bool Matches(SqlError error) => error.Number == number && error.Message == message
            && (error.Procedure == name || error.Procedure == procedure);
        return errors.Any(Matches)
            && errors.All(error => Matches(error) || error.Number == 3621);
    }

    private static async Task<DbDataReader> OpenSqlReaderAsync(
        SqlCommand command, CancellationToken token)
    {
        await command.Connection!.OpenAsync(token);
        return await command.ExecuteReaderAsync(token);
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

    private static SqlCommand CreateCommand(
        SqlConnection connection,
        string procedure,
        DateTime datum,
        KifDocumentProcessingConfiguration settings)
    {
        var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = procedure;
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        // Obrada od 01.08.2026. do dana pokretanja, uključujući taj dan.
        command.Parameters.Add("@DatumOd", SqlDbType.Date).Value = new DateTime(2026, 8, 1);
        command.Parameters.Add("@DatumDo", SqlDbType.Date).Value = datum.Date;
        command.Parameters.Add("@anUserID", SqlDbType.Int).Value = settings.UserId;
        command.Parameters.Add("@SamoProvera", SqlDbType.Bit).Value = settings.SamoProvera;

        if (procedure == KifDocumentResultReader.DocumentsProcedure)
            command.Parameters.Add("@KIFID", SqlDbType.Int).Value = DBNull.Value;
        else
        {
            command.Parameters.Add("@acKey", SqlDbType.VarChar, 13).Value = DBNull.Value;
            command.Parameters.Add("@PrikaziNapredak", SqlDbType.Bit).Value = settings.PrikaziNapredak;
        }

        return command;
    }

    private static void Validate(DateTime datum, KifDocumentProcessingConfiguration settings)
    {
        if (!settings.Enabled)
            throw new InvalidOperationException("KIF kreiranje i knjiženje nisu uključeni u konfiguraciji.");
        if (settings.UserId <= 0)
            throw new InvalidOperationException("KIF UserId mora biti veći od nule.");
        if (settings.CommandTimeoutSeconds < 0)
            throw new InvalidOperationException("KIF CommandTimeoutSeconds mora biti 0 (bez ograničenja) ili veći od nule.");
        if (datum < new DateTime(1753, 1, 1) || datum >= DateTime.MaxValue.Date)
            throw new InvalidOperationException("Datum KIF obrade nije podržan.");
    }

    private static void WriteLog(Action<string> log, string message)
    {
        try
        {
            log(message);
        }
        catch (Exception exception)
        {
            // Greška prikaza/logovanja ne sme prekinuti SQL obradu.
            Debug.WriteLine($"KIF log nije upisan: {exception.Message}");
        }
    }
}
