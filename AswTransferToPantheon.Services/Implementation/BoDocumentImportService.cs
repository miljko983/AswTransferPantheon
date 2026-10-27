using System;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Helpers;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class BoDocumentImportService(
    IOptions<ConnectionStrings> connectionStrings)
    : IBoDocumentImportService
{
    public async Task<BoDocumentRunResult> ExecuteAsync(
        DateTime datum,
        BoDocumentTaskConfiguration configuration,
        Action<string> log,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BoDocumentSchedule.Validate(configuration);

        var result = new BoDocumentRunResult
        {
            Datum = datum.Date,
            SamoProvera = configuration.SamoProvera
        };

        var watch = Stopwatch.StartNew();

        try
        {
            // Procedura sama upravlja transakcijama.
            // Ova veza se posle korišćenja ne vraća u pool.
            await using var connection =
                new SqlConnection(BuildConnectionString());

            connection.InfoMessage += (_, args) =>
            {
                foreach (SqlError message in args.Errors)
                {
                    result.ImportId ??=
                        GetStartedImportId(message.Message);

                    log(message.Message);
                }
            };

            await connection.OpenAsync(token);

            using var command = CreateCommand(
                connection,
                datum.Date,
                configuration);

            await using (var reader =
                await command.ExecuteReaderAsync(token))
            {
                await BoDocumentResultReader.ReadAsync(
                    reader,
                    result,
                    token);
            }

            if (command.Parameters["@ImportId"].Value is long importId)
            {
                if (result.ImportId.HasValue
                    && result.ImportId != importId)
                {
                    throw new InvalidOperationException(
                        "Izlazni BO ImportId se razlikuje od rezultata.");
                }

                result.ImportId = importId;
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            result.ExecutionException = exception;
            result.ResponseComplete = false;
            result.Status = "Failed";

            // Posle greške proveravamo evidenciju ovog konkretnog uvoza.
            if (result.ImportId.HasValue && !configuration.SamoProvera)
            {
                await RecoverLedgerAsync(result, log, token);
            }
        }

        watch.Stop();
        result.Duration = watch.Elapsed;

        return result;
    }

    private static long? GetStartedImportId(string message)
    {
        // Poruka o grešci može pominjati neki raniji uvoz.
        // ID preuzimamo samo iz poruke o početku tekućeg uvoza.
        var match = Regex.Match(
            message,
            @"^Pokrenut BO uvoz\. ImportId=([0-9]+)\.",
            RegexOptions.CultureInvariant);

        return match.Success
            && long.TryParse(
                match.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var id)
            && id > 0
                ? id
                : null;
    }

    private string BuildConnectionString()
    {
        return new SqlConnectionStringBuilder(
            connectionStrings.Value.Transfer)
        {
            InitialCatalog = "CENTROSINERGIJA",
            Pooling = false,
            Enlist = false,
            MultipleActiveResultSets = false
        }.ConnectionString;
    }

    private static SqlCommand CreateCommand(
        SqlConnection connection,
        DateTime datum,
        BoDocumentTaskConfiguration configuration)
    {
        var command = connection.CreateCommand();

        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "dbo._pr_KreirajDokumenteIzBO_Paketi";
        command.CommandTimeout = configuration.CommandTimeoutSeconds;
        command.Parameters.Add("@Datum", SqlDbType.Date).Value = datum.Date;
        command.Parameters.Add("@anUserID", SqlDbType.Int).Value = (object?)configuration.UserId ?? DBNull.Value;
        command.Parameters.Add("@SamoProvera", SqlDbType.Bit).Value = configuration.SamoProvera;
        command.Parameters.Add("@PrikaziVreme", SqlDbType.Bit).Value = configuration.PrikaziVreme;
        command.Parameters.Add("@ObracunNaKraju", SqlDbType.Bit).Value = configuration.ObracunNaKraju;
        command.Parameters.Add("@VelicinaPaketa", SqlDbType.Int).Value = configuration.BatchSize;
        command.Parameters.Add("@ImportId", SqlDbType.BigInt).Direction = ParameterDirection.Output;

        return command;
    }

    private async Task RecoverLedgerAsync(
        BoDocumentRunResult result,
        Action<string> log,
        CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);

            deadline.CancelAfter(TimeSpan.FromSeconds(30));

            await using var connection = new SqlConnection(BuildConnectionString());

            await connection.OpenAsync(deadline.Token);

            using var command = connection.CreateCommand();

            command.CommandTimeout = 30;

            command.Parameters.Add("@ImportId", SqlDbType.BigInt).Value = result.ImportId!.Value;

            command.CommandText = """
                SELECT
                    ImportId,
                    Datum,
                    Status,
                    Nalog,
                    BrojDokumenata AS KreiranoDokumenata,
                    BrojStavki AS KreiranoStavki,
                    BrojGresaka
                FROM dbo._tb_BOImportKontrola
                WHERE ImportId = @ImportId;

                SELECT
                    d.ImportId,
                    d.Dokument,
                    k.Datum,
                    m.acDocType AS VrstaDokumenta,
                    d.acKey AS RobniDokument,
                    k.VrstaNaloga,
                    d.BrojStavki,
                    k.Nalog
                FROM dbo._tb_BOImportDokumenti AS d
                INNER JOIN dbo._tb_BOImportKontrola AS k
                    ON k.ImportId = d.ImportId
                LEFT JOIN dbo.tHE_Move AS m
                    ON m.acKey = d.acKey
                WHERE d.ImportId = @ImportId
                ORDER BY d.Dokument;

                SELECT
                    GreskaId,
                    ImportId,
                    Datum,
                    Dokument,
                    IzvorniBroj,
                    DocumentType,
                    Ident,
                    Faza,
                    BrojGreske,
                    Poruka,
                    Procedura,
                    Linija,
                    Vreme
                FROM dbo._tb_BOImportGreske
                WHERE ImportId = @ImportId
                ORDER BY GreskaId;
                """;

            var recovered = new BoDocumentRunResult
            {
                Datum = result.Datum,
                ImportId = result.ImportId
            };

            await using var reader =
                await command.ExecuteReaderAsync(deadline.Token);

            await BoDocumentResultReader.ReadAsync(
                reader,
                recovered,
                deadline.Token);

            result.Summary = recovered.Summary;
            result.Status = recovered.Status;

            result.Documents.Clear();
            result.Documents.AddRange(recovered.Documents);

            result.Errors.Clear();
            result.Errors.AddRange(recovered.Errors);

            result.RecoveredFromLedger = true;

            log(
                $"BO ImportId={result.ImportId}: " +
                "pročitana trajna evidencija posle greške.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            result.DiagnosticsError = exception.Message;

            log(
                $"BO ImportId={result.ImportId}: " +
                "nije moguće pročitati trajnu evidenciju: " +
                exception.Message);
        }
    }
}