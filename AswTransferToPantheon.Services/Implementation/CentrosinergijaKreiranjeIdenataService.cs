using System.Data;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class CentrosinergijaKreiranjeIdenataService
    : ICentrosinergijaKreiranjeIdenataService
{
    private readonly ConnectionStrings connectionStrings;

    public Action<string>? LogAction { get; set; }

    public Action<BadRecordInfo>? ErrorAction { get; set; }

    public CentrosinergijaKreiranjeIdenataService(IOptions<ConnectionStrings> connectionStrings)
    {
        this.connectionStrings = connectionStrings.Value;
    }

    public async Task Execute(CancellationToken token)
    {
        LogAction?.Invoke("CENTROSINERGIJA kreiranje identa - počinje...");

        await using var connection = new SqlConnection(connectionStrings.Transfer);

        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();

        command.CommandText = "dbo._pr_CENTROSINERGIJA_KreiranjeIdenata";

        command.CommandType = CommandType.StoredProcedure;

        command.CommandTimeout = 2000;

        var runIdParameter = command.Parameters.Add("@RunID", SqlDbType.UniqueIdentifier);

        runIdParameter.Direction = ParameterDirection.InputOutput;

        runIdParameter.Value = DBNull.Value;

        await using var reader =
            await command.ExecuteReaderAsync(token);

        if (await reader.ReadAsync(token))
        {
            var runId = reader["RunID"] == DBNull.Value ? string.Empty : reader["RunID"].ToString() ?? string.Empty;

            var primarne = Convert.ToInt32(reader["KreiranePrimarneKlasifikacije"]);

            var sekundarne = Convert.ToInt32(reader["KreiraneSekundarneKlasifikacije"]);

            var identi = Convert.ToInt32(reader["KreiraniIdenti"]);

            var troskovi1 = Convert.ToInt32(reader["KreiraniTroskovi1"]);

            var troskovi2 = Convert.ToInt32(reader["KreiraniTroskovi2"]);

            var akcize = Convert.ToInt32(reader["KreiraneAkcize"]);

            var barkodovi = Convert.ToInt32(reader["KreiraniBarkodovi"]);

            var upozorenja = Convert.ToInt32(reader["BrojUpozorenja"]);

            var greske = Convert.ToInt32(reader["BrojGresaka"]);

            LogAction?.Invoke(
                "CENTROSINERGIJA kreiranje identa - završeno. " +
                $"RunID: {runId}; " +
                $"Primarne klasifikacije: {primarne}; " +
                $"Sekundarne klasifikacije: {sekundarne}; " +
                $"Identi: {identi}; " +
                $"Troškovi 1: {troskovi1}; " +
                $"Troškovi 2: {troskovi2}; " +
                $"Akcize: {akcize}; " +
                $"Barkodovi: {barkodovi}; " +
                $"Upozorenja: {upozorenja}; " +
                $"Greške: {greske}.");
        }
        else
        {
            LogAction?.Invoke(
                "CENTROSINERGIJA kreiranje identa - " +
                "procedura nije vratila zbirni rezultat.");
        }

        if (await reader.NextResultAsync(token))
        {
            var fazaOrdinal = reader.GetOrdinal("Faza");

            var sourceIdOrdinal = reader.GetOrdinal("SourceID");

            var acIdentOrdinal = reader.GetOrdinal("AcIdent");

            var acNameOrdinal = reader.GetOrdinal("AcName");

            var warningOrdinal = reader.GetOrdinal("IsWarning");

            var errorNumberOrdinal = reader.GetOrdinal("ErrorNumber");

            var errorLineOrdinal = reader.GetOrdinal("ErrorLine");

            var errorMessageOrdinal = reader.GetOrdinal("ErrorMessage");

            while (await reader.ReadAsync(token))
            {
                var faza = reader.IsDBNull(fazaOrdinal) ? string.Empty : reader.GetValue(fazaOrdinal)?.ToString() ?? string.Empty;

                var sourceId = reader.IsDBNull(sourceIdOrdinal) ? string.Empty : reader.GetValue(sourceIdOrdinal)?.ToString() ?? string.Empty;

                var acIdent = reader.IsDBNull(acIdentOrdinal) ? string.Empty : reader.GetValue(acIdentOrdinal)?.ToString() ?? string.Empty;

                var acName = reader.IsDBNull(acNameOrdinal) ? string.Empty : reader.GetValue(acNameOrdinal)?.ToString() ?? string.Empty;

                var isWarning = !reader.IsDBNull(warningOrdinal) && Convert.ToBoolean(reader.GetValue(warningOrdinal));

                var errorNumber = reader.IsDBNull(errorNumberOrdinal) ? string.Empty : reader.GetValue(errorNumberOrdinal)?.ToString() ?? string.Empty;

                var errorLine = reader.IsDBNull(errorLineOrdinal) ? string.Empty : reader.GetValue(errorLineOrdinal)?.ToString() ?? string.Empty;

                var errorMessage =
                    reader.IsDBNull(errorMessageOrdinal) ? string.Empty : reader.GetValue(errorMessageOrdinal)?.ToString() ?? string.Empty;

                ErrorAction?.Invoke(
                    new BadRecordInfo
                    {
                        TableName = $"CENTROSINERGIJA - {faza}",

                        Key = $"SourceID={sourceId}; AcIdent={acIdent}",

                        Message = isWarning ? $"UPOZORENJE: {errorMessage}" : errorMessage,

                        Data =
                            $"Faza={faza}; " +
                            $"SourceID={sourceId}; " +
                            $"AcIdent={acIdent}; " +
                            $"Naziv={acName}; " +
                            $"Broj greške={errorNumber}; " +
                            $"Linija={errorLine}",

                        Exception =
                            $"SQL broj greške: {errorNumber}; " +
                            $"SQL linija: {errorLine}; " +
                            errorMessage
                    });
            }
        }

        LogAction?.Invoke("CENTROSINERGIJA kreiranje identa - " + "obrada grešaka završena.");
    }
}