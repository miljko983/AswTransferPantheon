using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using System.Data;

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
            await TransferArtikliDobavljaci(batchSize, token);

            // Kasnije redom:
            // await TransferArtikliOsobine(batchSize, token);
            // await TransferBarkodovi(batchSize, token);
            // await TransferRobneGrupe(batchSize, token);
            // Kasnije redom:
            // await TransferArtikliDobavljaci(batchSize, token);
            // await TransferArtikliOsobine(batchSize, token);
            // await TransferBarkodovi(batchSize, token);
            // await TransferRobneGrupe(batchSize, token);
        }

        

        private async Task TransferArtikli(int batchSize, CancellationToken token)
        {
            long lastId = 0;

            while (!token.IsCancellationRequested)
            {
                var artikli = await ReadArtikliBatch(lastId, batchSize, token);

                if (artikli.Count == 0)
                {
                    break;
                }

                await SaveArtikliToTmpTable(artikli, token);

                lastId = artikli[^1].Id;
            }
            /*    
               long lastId = 0;

               var artikli = await ReadArtikliBatch(lastId, batchSize, token);

               if (artikli.Count == 0)
               {
                   return;
               }

               await SaveArtikliToTmpTable(artikli, token);
               */
            //await SaveArtikliToTestFile(artikli, token);
        }

        private async Task TransferArtikliDobavljaci(int batchSize, CancellationToken token)
        {
            long lastId = 0;

            while (!token.IsCancellationRequested)
            {
                var artikliDobavljaci = await ReadArtikliDobavljaciBatch(lastId, batchSize, token);

                if (artikliDobavljaci.Count == 0)
                {
                    break;
                }

                await SaveArtikliDobavljaciToTmpTable(artikliDobavljaci, token);

                lastId = artikliDobavljaci[^1].Id;
            }
        }

        

        private async Task SaveArtikliToTmpTable(List<Artikal> artikli, CancellationToken token)
        {
            await using var connection = new SqlConnection(connectionStrings.Transfer);
            await connection.OpenAsync(token);

            await using var transaction = await connection.BeginTransactionAsync(token);

            try
            {
                await ClearArtikliTmp(connection, (SqlTransaction)transaction, token);
                await BulkInsertArtikliTmp(connection, (SqlTransaction)transaction, artikli, token);
                await MergeArtikli(connection, (SqlTransaction)transaction, token);

                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }

        private async Task SaveArtikliDobavljaciToTmpTable(List<ArtikalDobavljac> artikliDobavljaci, CancellationToken token)
        {
            await using var connection = new SqlConnection(connectionStrings.Transfer);
            await connection.OpenAsync(token);

            await using var transaction = await connection.BeginTransactionAsync(token);

            try
            {
                await ClearArtikliDobavljaciTmp(connection, (SqlTransaction)transaction, token);
                await BulkInsertArtikliDobavljaciTmp(connection, (SqlTransaction)transaction, artikliDobavljaci, token);
                await MergeArtikliDobavljaci(connection, (SqlTransaction)transaction, token);

                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }

        private async Task BulkInsertArtikliDobavljaciTmp(SqlConnection connection, SqlTransaction transaction, List<ArtikalDobavljac> artikliDobavljaci, CancellationToken token)
        {
            var table = CreateArtikliDobavljaciDataTable(artikliDobavljaci);

            using var bulkCopy = new SqlBulkCopy(
                connection,
                SqlBulkCopyOptions.CheckConstraints,
                transaction);

            bulkCopy.DestinationTableName = "dbo._tb_ARTIKLIDOBAVLJACI_TMP";
            bulkCopy.BatchSize = artikliDobavljaci.Count;
            bulkCopy.BulkCopyTimeout = 60;

            bulkCopy.ColumnMappings.Add("ID", "ID");
            bulkCopy.ColumnMappings.Add("IDARTIKLA", "IDARTIKLA");
            bulkCopy.ColumnMappings.Add("KOMITENTTIP", "KOMITENTTIP");
            bulkCopy.ColumnMappings.Add("KOMITENT", "KOMITENT");
            bulkCopy.ColumnMappings.Add("REGIJA", "REGIJA");
            bulkCopy.ColumnMappings.Add("PRIORITET", "PRIORITET");
            bulkCopy.ColumnMappings.Add("VREMEOD", "VREMEOD");
            bulkCopy.ColumnMappings.Add("VREMEDO", "VREMEDO");

            await bulkCopy.WriteToServerAsync(table, token);
        }

        private static DataTable CreateArtikliDobavljaciDataTable(List<ArtikalDobavljac> artikliDobavljaci)
        {
            var table = new DataTable();

            table.Columns.Add("ID", typeof(decimal));
            table.Columns.Add("IDARTIKLA", typeof(decimal));
            table.Columns.Add("KOMITENTTIP", typeof(string));
            table.Columns.Add("KOMITENT", typeof(decimal));
            table.Columns.Add("REGIJA", typeof(string));
            table.Columns.Add("PRIORITET", typeof(decimal));
            table.Columns.Add("VREMEOD", typeof(DateTime));
            table.Columns.Add("VREMEDO", typeof(DateTime));

            foreach (var item in artikliDobavljaci)
            {
                table.Rows.Add(
                    Convert.ToDecimal(item.Id),
                    Convert.ToDecimal(item.IdArtikla),
                    Required(item.KomitentTip, item.Id, nameof(item.KomitentTip)),
                    Convert.ToDecimal(item.Komitent),
                    DbValue(item.Regija),
                    Convert.ToDecimal(item.Prioritet),
                    item.VremeOd,
                    item.VremeDo);
            }

            return table;
        }

        private async Task ClearArtikliDobavljaciTmp(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "TRUNCATE TABLE dbo._tb_ARTIKLIDOBAVLJACI_TMP;";

            await command.ExecuteNonQueryAsync(token);
        }

        private static async Task MergeArtikli(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "dbo._pr_MergeArtikli";
            command.CommandType = CommandType.StoredProcedure;
            command.CommandTimeout = 120;

            await command.ExecuteNonQueryAsync(token);
        }

        private static async Task MergeArtikliDobavljaci(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "dbo._pr_MergeArtikliDobavljaci";
            command.CommandType = CommandType.StoredProcedure;
            command.CommandTimeout = 120;

            await command.ExecuteNonQueryAsync(token);
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

        private async Task<List<ArtikalDobavljac>> ReadArtikliDobavljaciBatch(long lastId, int batchSize, CancellationToken token)
        {
            const string sql = """
            SELECT
                ID,
                ARTIKAL,
                KOMITENTTIP,
                KOMITENT,
                REGIJA,
                PRIORITET,
                VREMEOD,
                VREMEDO
            FROM IIS.ARTIKLIDOBAVLJACI
            WHERE ID > :lastId
              AND KOMITENT LIKE '9%'
              AND VREMEDO > SYSDATE
              AND ARTIKAL IN (
                  SELECT ID
                  FROM IIS.ARTIKLI
                  WHERE TIP = 'R'
              )
              AND LENGTH(KOMITENT) = 6
            ORDER BY ID
            FETCH NEXT :batchSize ROWS ONLY
            """;

            var result = new List<ArtikalDobavljac>(batchSize);

            await using var connection = new OracleConnection(BuildOracleConnectionString());
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.BindByName = true;

            command.Parameters.Add("lastId", OracleDbType.Int64).Value = lastId;
            command.Parameters.Add("batchSize", OracleDbType.Int32).Value = batchSize;

            await using var reader = await command.ExecuteReaderAsync(token);

            while (await reader.ReadAsync(token))
            {
                result.Add(new ArtikalDobavljac
                {
                    Id = GetInt64(reader, "ID"),
                    IdArtikla = GetInt64(reader, "ARTIKAL"),
                    KomitentTip = GetString(reader, "KOMITENTTIP"),
                    
                    Komitent = GetInt64(reader, "KOMITENT"),
                    Prioritet = GetRequiredInt32(reader, "PRIORITET"),
                    VremeOd = GetRequiredDateTime(reader, "VREMEOD"),
                    VremeDo = GetRequiredDateTime(reader, "VREMEDO")
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
        private static int GetRequiredInt32(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);

            if (reader.IsDBNull(ordinal))
            {
                throw new InvalidOperationException($"Oracle kolona {columnName} je NULL, a obavezna je.");
            }

            return Convert.ToInt32(reader.GetValue(ordinal));
        }

        private static DateTime GetRequiredDateTime(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);

            if (reader.IsDBNull(ordinal))
            {
                throw new InvalidOperationException($"Oracle kolona {columnName} je NULL, a obavezna je.");
            }

            return reader.GetDateTime(ordinal);
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

        private static async Task ClearArtikliTmp(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "TRUNCATE TABLE dbo.ARTIKLI_TMP;";

            await command.ExecuteNonQueryAsync(token);
        }

        private static async Task BulkInsertArtikliTmp(SqlConnection connection, SqlTransaction transaction, List<Artikal> artikli, CancellationToken token)
        {
            var table = CreateArtikliDataTable(artikli);

            using var bulkCopy = new SqlBulkCopy(
                connection,
                SqlBulkCopyOptions.CheckConstraints,
                transaction);

            bulkCopy.DestinationTableName = "dbo.ARTIKLI_TMP";
            bulkCopy.BatchSize = artikli.Count;
            bulkCopy.BulkCopyTimeout = 60;

            bulkCopy.ColumnMappings.Add("ID", "ID");
            bulkCopy.ColumnMappings.Add("SIFRA", "SIFRA");
            bulkCopy.ColumnMappings.Add("NAZIV", "NAZIV");
            bulkCopy.ColumnMappings.Add("PREVOD", "PREVOD");
            bulkCopy.ColumnMappings.Add("ROBNAGRUPA", "ROBNAGRUPA");
            bulkCopy.ColumnMappings.Add("TARIFNAGRUPA", "TARIFNAGRUPA");
            bulkCopy.ColumnMappings.Add("FAZNIPOREZ", "FAZNIPOREZ");
            bulkCopy.ColumnMappings.Add("TIP", "TIP");
            bulkCopy.ColumnMappings.Add("SKRACENINAZIV", "SKRACENINAZIV");
            bulkCopy.ColumnMappings.Add("VRSTA", "VRSTA");
            bulkCopy.ColumnMappings.Add("STATUS", "STATUS");
            bulkCopy.ColumnMappings.Add("VREMESTATUSA", "VREMESTATUSA");
            bulkCopy.ColumnMappings.Add("KORISNIK", "KORISNIK");

            await bulkCopy.WriteToServerAsync(table, token);
        }

        private static DataTable CreateArtikliDataTable(List<Artikal> artikli)
        {
            var table = new DataTable();

            table.Columns.Add("ID", typeof(decimal));
            table.Columns.Add("SIFRA", typeof(string));
            table.Columns.Add("NAZIV", typeof(string));
            table.Columns.Add("PREVOD", typeof(string));
            table.Columns.Add("ROBNAGRUPA", typeof(string));
            table.Columns.Add("TARIFNAGRUPA", typeof(string));
            table.Columns.Add("FAZNIPOREZ", typeof(string));
            table.Columns.Add("TIP", typeof(string));
            table.Columns.Add("SKRACENINAZIV", typeof(string));
            table.Columns.Add("VRSTA", typeof(string));
            table.Columns.Add("STATUS", typeof(string));
            table.Columns.Add("VREMESTATUSA", typeof(DateTime));
            table.Columns.Add("KORISNIK", typeof(string));

            foreach (var artikal in artikli)
            {
                table.Rows.Add(
                    Convert.ToDecimal(artikal.Id),
                    Required(artikal.Sifra, artikal.Id, nameof(artikal.Sifra)),
                    Required(artikal.Naziv, artikal.Id, nameof(artikal.Naziv)),
                    DbValue(artikal.Prevod),
                    Required(artikal.RobnaGrupa, artikal.Id, nameof(artikal.RobnaGrupa)),
                    Required(artikal.TarifnaGrupa, artikal.Id, nameof(artikal.TarifnaGrupa)),
                    Required(artikal.FazniPorez, artikal.Id, nameof(artikal.FazniPorez)),
                    Required(artikal.Tip, artikal.Id, nameof(artikal.Tip)),
                    Required(artikal.SkraceniNaziv, artikal.Id, nameof(artikal.SkraceniNaziv)),
                    DbValue(artikal.Vrsta),
                    DbValue(artikal.Status),
                    DbValue(artikal.VremeStatusa),
                    DbValue(artikal.Korisnik));
            }

            return table;
        }

        private static object DbValue<T>(T? value)
        {
            return value is null ? DBNull.Value : value;
        }

        private static string Required(string? value, long id, string propertyName)
        {
            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Artikal ID {id} nema vrednost za obavezno polje {propertyName}.");
            }

            return value;
        }
    }
}
