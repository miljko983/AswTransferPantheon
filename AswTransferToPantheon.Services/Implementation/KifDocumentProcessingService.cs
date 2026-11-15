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

    public KifDocumentProcessingService(IOptions<ConnectionStrings> connectionStrings)
        : this(connectionStrings, OpenSqlReaderAsync)
    {
    }

    // Omogućava proveru obrade odgovora bez povezivanja na bazu.
    internal KifDocumentProcessingService(
        IOptions<ConnectionStrings> connectionStrings,
        Func<SqlCommand, CancellationToken, Task<DbDataReader>> openReader)
    {
        ArgumentNullException.ThrowIfNull(connectionStrings);
        ArgumentNullException.ThrowIfNull(openReader);
        this.connectionStrings = connectionStrings.Value;
        this.openReader = openReader;
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
            WriteLog(log, $"KIF kreiranje dokumenata: datum {result.Datum:dd.MM.yyyy}; " +
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
            WriteLog(log, $"KIF knjiženje: datum {result.Datum:dd.MM.yyyy}; " +
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
        token.ThrowIfCancellationRequested();

        // Svaka procedura dobija svoju vezu i sama upravlja transakcijama.
        await using var connection = new SqlConnection(connectionString);
        connection.InfoMessage += (_, args) =>
        {
            foreach (SqlError message in args.Errors)
                WriteLog(log, message.Message);
        };

        using var command = CreateCommand(connection, procedure, result.Datum, settings);
        await using var reader = await openReader(command, token);

        if (procedure == KifDocumentResultReader.DocumentsProcedure)
            await KifDocumentResultReader.ReadDocumentsAsync(reader, result, token);
        else
            await KifDocumentResultReader.ReadPostingsAsync(reader, result, token);
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

        // Eksplicitno zadajemo oba datuma: ne koristimo podrazumevani 01.08.
        command.Parameters.Add("@DatumOd", SqlDbType.Date).Value = new DateTime(2026, 8, 1);//datum.Date;
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
        if (settings.CommandTimeoutSeconds <= 0)
            throw new InvalidOperationException("KIF CommandTimeoutSeconds mora biti veći od nule.");
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
