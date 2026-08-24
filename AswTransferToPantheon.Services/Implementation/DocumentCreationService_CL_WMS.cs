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

    public DocumentCreationService_CL_WMS(IOptions<ConnectionStrings> connectionStrings)
    {
        this.connectionStrings = connectionStrings.Value;
    }

    public async Task Execute(string? orgJedLike, bool usePriceCalculation, int? maxDocuments, CancellationToken token)
    {
        LogAction?.Invoke("Kreiranje CL_WMS dokumenata - počinje...");

        await using var connection = new SqlConnection(connectionStrings.Transfer);

        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();

        command.CommandText = "dbo._pr_KreiranjeDokumenata_CL_WMS";

        command.CommandType = CommandType.StoredProcedure;

        command.CommandTimeout = 600;

        command.Parameters.Add("@OrgJedLike", SqlDbType.NVarChar, 20).Value = (object?)orgJedLike ?? DBNull.Value;

        command.Parameters.Add("@UsePriceCalculation", SqlDbType.Bit).Value = usePriceCalculation;

        command.Parameters.Add("@MaxDocuments", SqlDbType.Int).Value = (object?)maxDocuments ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(token);


        /* PRVI RESULT SET - ZBIRNI REZULTAT */

        if (await reader.ReadAsync(token))
        {
            var successfulOrdinal = reader.GetOrdinal("UspesnoKreirano");

            var failedOrdinal = reader.GetOrdinal("NeuspesnoKreirano");

            var successful = reader.IsDBNull(successfulOrdinal) ? 0 : reader.GetInt32(successfulOrdinal);

            var failed = reader.IsDBNull(failedOrdinal) ? 0 : reader.GetInt32(failedOrdinal);

            LogAction?.Invoke($"Kreiranje CL_WMS dokumenata - završeno. " + $"Uspešno: {successful}, neuspešno: {failed}.");
        }


        /* DRUGI RESULT SET - KREIRANI DOKUMENTI */

        if (await reader.NextResultAsync(token))
        {
            var docTypeOrdinal = reader.GetOrdinal("AcDocType");

            var documentNameOrdinal = reader.GetOrdinal("DocumentName");

            var numberFromOrdinal = reader.GetOrdinal("NumberFrom");

            var numberToOrdinal = reader.GetOrdinal("NumberTo");

            while (await reader.ReadAsync(token))
            {
                var documentInfo =
                    new CreatedDocumentInfo
                    {
                        AcDocType =
                            reader.IsDBNull(docTypeOrdinal)
                                ? string.Empty
                                : reader.GetValue(docTypeOrdinal)?
                                    .ToString()
                                    ?? string.Empty,

                        DocumentName =
                            reader.IsDBNull(documentNameOrdinal)
                                ? string.Empty
                                : reader.GetValue(documentNameOrdinal)?
                                    .ToString()
                                    ?? string.Empty,

                        NumberFrom = reader.IsDBNull(numberFromOrdinal)
                            ? string.Empty
                            : reader.GetValue(numberFromOrdinal)?.ToString() ?? string.Empty,

                        NumberTo = reader.IsDBNull(numberToOrdinal)
                            ? string.Empty
                            : reader.GetValue(numberToOrdinal)?.ToString() ?? string.Empty
                    };

                CreatedDocumentAction?.Invoke(documentInfo);
            }
        }
    }
}