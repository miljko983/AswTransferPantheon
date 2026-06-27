using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using System.Text.Json;

namespace AswTransferToPantheon.Services.Implementation
{
    public sealed class ArtikliTransferService : IArtikliTransferService
    {
        private readonly ConnectionStrings connectionStrings;

        public ArtikliTransferService(IOptions<ConnectionStrings> connectionStrings)
        {
            this.connectionStrings = connectionStrings.Value;
        }

        public async Task TransferArtikliPaket(int batchSize, CancellationToken token)
        {
            await TransferArtikli(batchSize, token);

            // Kasnije redom:
            // await TransferArtikliDobavljaci(batchSize, token);
            // await TransferArtikliOsobine(batchSize, token);
            // await TransferBarkodovi(batchSize, token);
            // await TransferRobneGrupe(batchSize, token);
        }

        private async Task TransferArtikli(int batchSize, CancellationToken token)
        {
            /* long lastId = 0;

             while (!token.IsCancellationRequested)
             {
                 var artikli = await ReadArtikliBatch(lastId, batchSize, token);

                 if (artikli.Count == 0)
                 {
                     break;
                 }

                 // Za sada samo čitamo.
                 // Sledeći korak će biti: upis u SQL tmp tabelu.

                 lastId = artikli[^1].Id;
             }*/
            long lastId = 0;

            var artikli = await ReadArtikliBatch(lastId, batchSize, token);

            if (artikli.Count == 0)
            {
                return;
            }

            await SaveArtikliToTestFile(artikli, token);
        }

        private static async Task SaveArtikliToTestFile(List<Artikal> artikli, CancellationToken token)
        {
            const string folderPath = @"D:\Sasha\Centrosinergija\TestFile";
            const string filePath = @"D:\Sasha\Centrosinergija\TestFile\artikli-prvih-1000.json";

            Directory.CreateDirectory(folderPath);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, artikli, options, token);
        }

        private async Task<List<Artikal>> ReadArtikliBatch(long lastId, int batchSize, CancellationToken token)
        {
            const string sql = """
            SELECT
                ID,
                SIFRA,
                NAZIV,
                PREVOD,
                ROBNAGRUPA,
                TARIFNAGRUPA,
                FAZNIPOREZ,
                TIP,
                SKRACENINAZIV,
                VRSTA,
                STATUS,
                VREMESTATUSA,
                KORISNIK
            FROM IIS.ARTIKLI
            WHERE ID > :lastId
            ORDER BY ID
            FETCH NEXT :batchSize ROWS ONLY
            """;

            var result = new List<Artikal>(batchSize);

            await using var connection = new OracleConnection(BuildOracleConnectionString());
            try
            {
                await connection.OpenAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Oracle konekcija nije uspela.", ex);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.BindByName = true;

            command.Parameters.Add("lastId", OracleDbType.Int64).Value = lastId;
            command.Parameters.Add("batchSize", OracleDbType.Int32).Value = batchSize;

            await using var reader = await command.ExecuteReaderAsync(token);

            while (await reader.ReadAsync(token))
            {
                result.Add(new Artikal
                {
                    Id = GetInt64(reader, "ID"),
                    Sifra = GetString(reader, "SIFRA"),
                    Naziv = GetString(reader, "NAZIV"),
                    Prevod = GetString(reader, "PREVOD"),
                    RobnaGrupa = GetString(reader, "ROBNAGRUPA"),
                    TarifnaGrupa = GetString(reader, "TARIFNAGRUPA"),
                    FazniPorez = GetString(reader, "FAZNIPOREZ"),
                    Tip = GetString(reader, "TIP"),
                    SkraceniNaziv = GetString(reader, "SKRACENINAZIV"),
                    Vrsta = GetString(reader, "VRSTA"),
                    Status = GetString(reader, "STATUS"),
                    VremeStatusa = GetDateTime(reader, "VREMESTATUSA"),
                    Korisnik = GetString(reader, "KORISNIK")
                });
            }

            return result;
        }

        private static string? GetString(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static DateTime? GetDateTime(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
        }

        private static long GetInt64(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return Convert.ToInt64(reader.GetValue(ordinal));
        }

        private string BuildOracleConnectionString()
        {
            var builder = new OracleConnectionStringBuilder
            {
                UserID = "panta",
                Password = connectionStrings.AswPassword,
                DataSource = "(DESCRIPTION =(ADDRESS_LIST =(ADDRESS = (PROTOCOL = TCP)(HOST = 10.164.3.17)(PORT = 1521)))(CONNECT_DATA =(SID = log)))"
            };

            return builder.ConnectionString;
        }
    }
}
