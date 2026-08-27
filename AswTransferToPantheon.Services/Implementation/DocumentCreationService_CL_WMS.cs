using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class DocumentCreationService_CL_WMS : IDocumentCreationService_CL_WMS
{
    private readonly ConnectionStrings connectionStrings;

    public Action<string>? LogAction { get; set; }
    public Action<CreatedDocumentInfo>? CreatedDocumentAction { get; set; }
    public Action<BadRecordInfo>? CreationErrorAction { get; set; }

    public DocumentCreationService_CL_WMS(IOptions<ConnectionStrings> connectionStrings)
    {
        this.connectionStrings = connectionStrings.Value;
    }

    public async Task Execute(string? orgJedLike, bool usePriceCalculation, int? maxDocuments,
    CancellationToken token)
    {
        Guid? runId = null;

        LogAction?.Invoke("Kreiranje CL_WMS dokumenata - počinje...");

        await using var connection = new SqlConnection(connectionStrings.Transfer);

        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();

        command.CommandText = "dbo._pr_KreiranjeDokumenata_CL_WMS";

        command.CommandType = CommandType.StoredProcedure;

        command.CommandTimeout = 2000;

        command.Parameters.Add("@OrgJedLike", SqlDbType.NVarChar, 20).Value = (object?)orgJedLike ?? DBNull.Value;

        command.Parameters.Add("@UsePriceCalculation", SqlDbType.Bit).Value = usePriceCalculation;

        command.Parameters.Add("@MaxDocuments", SqlDbType.Int).Value = (object?)maxDocuments ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(token);

        if (await reader.ReadAsync(token))
        {
            var runIdOrdinal = reader.GetOrdinal("RunID");

            if (!reader.IsDBNull(runIdOrdinal))
            {
                runId = reader.GetGuid(runIdOrdinal);
            }

            var successful = reader.IsDBNull(reader.GetOrdinal("UspesnoKreirano")) ? 0 : reader.GetInt32(reader.GetOrdinal("UspesnoKreirano"));

            var failed = reader.IsDBNull(reader.GetOrdinal("NeuspesnoKreirano"))? 0 : reader.GetInt32(reader.GetOrdinal("NeuspesnoKreirano"));

            LogAction?.Invoke($"Kreiranje CL_WMS dokumenata - završeno. " + $"Uspešno: {successful}, neuspešno: {failed}.");
        }

        if (await reader.NextResultAsync(token))
        {
            var docTypeOrdinal = reader.GetOrdinal("AcDocType");

            var documentNameOrdinal = reader.GetOrdinal("DocumentName");

            var numberFromOrdinal = reader.GetOrdinal("NumberFrom");

            var numberToOrdinal = reader.GetOrdinal("NumberTo");

            while (await reader.ReadAsync(token))
            {
                CreatedDocumentAction?.Invoke(new CreatedDocumentInfo
                    {
                        AcDocType = reader.IsDBNull(docTypeOrdinal) ? string.Empty : reader.GetValue(docTypeOrdinal) ?.ToString() ?? string.Empty,

                        DocumentName = reader.IsDBNull(documentNameOrdinal) ? string.Empty : reader.GetValue(documentNameOrdinal) ?.ToString() ?? string.Empty,

                        NumberFrom = reader.IsDBNull(numberFromOrdinal) ? string.Empty : reader.GetValue(numberFromOrdinal) ?.ToString() ?? string.Empty,

                        NumberTo = reader.IsDBNull(numberToOrdinal) ? string.Empty : reader.GetValue(numberToOrdinal) ?.ToString() ?? string.Empty
                    });
            }
        }

        await reader.DisposeAsync();

        if (runId.HasValue)
        {
            await ReadCreationErrors(connection, runId.Value, token);
        }
    }

    private async Task ReadCreationErrors(SqlConnection connection, Guid runId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = """
        SELECT
            VLPZaglavljeID,
            OrgJed,
            Dokument,
            Broj,
            DatumDokumenta,
            ACKey,
            Poruka,
            Detalji
        FROM dbo._tb_GreskeKreiranjaDokumenata_CLWMS
        WHERE RunID = @RunID
        ORDER BY VLPZaglavljeID;
        """;

        command.Parameters.Add(
            "@RunID",
            SqlDbType.UniqueIdentifier).Value = runId;

        await using var reader =
            await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            var vlpId = reader["VLPZaglavljeID"]?.ToString() ?? string.Empty;
            var orgJed = reader["OrgJed"]?.ToString() ?? string.Empty;
            var dokument = reader["Dokument"]?.ToString() ?? string.Empty;
            var broj = reader["Broj"]?.ToString() ?? string.Empty;
            var datum = reader["DatumDokumenta"]?.ToString() ?? string.Empty;
            var acKey = reader["ACKey"]?.ToString() ?? string.Empty;
            var poruka = reader["Poruka"]?.ToString() ?? string.Empty;
            var detalji = reader["Detalji"]?.ToString() ?? string.Empty;

            CreationErrorAction?.Invoke(
                new BadRecordInfo
                {
                    TableName = "KreiranjeDokumenata_CL_WMS",
                    Key = $"VLP ID={vlpId}; ACKey={acKey}",
                    Message = poruka,
                    Data =
                        $"OrgJed={orgJed}; " +
                        $"Dokument={dokument}; " +
                        $"Broj={broj}; " +
                        $"Datum={datum}",
                    Exception = detalji
                }
            );
        }
    }
}