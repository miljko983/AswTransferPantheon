using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Diagnostics;

namespace AswTransferToPantheon.Services.Implementation
{
    public sealed class ArtikliTransferService : IArtikliTransferService
    {
        private readonly ConnectionStrings connectionStrings;

        public Action<string> LogAction { get; set; }
        public ArtikliTransferService(IOptions<ConnectionStrings> connectionStrings)
        {
            this.connectionStrings = connectionStrings.Value;
        }

        public async Task TransferArtikliPaket(int batchSize, CancellationToken token)
        {
            await ExecuteWithLogging("Artikli", () => TransferArtikli(batchSize, token));
            await ExecuteWithLogging("Artikli dobavljači",() => TransferArtikliDobavljaci(batchSize, token));
            await ExecuteWithLogging("Artikli osobine",() => TransferArtikliOsobine(batchSize, token));
            await ExecuteWithLogging("Barkodovi", () => TransferBarkodovi(batchSize, token));

            // Kasnije redom:
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

        private static async Task MergeBarkodovi(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "dbo._pr_MergeBarkodovi";
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

        //HELPERI
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
        private static int? GetInt32(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));
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


        private static object DbLongAsDecimalValue(long? value)
        {
            return value is null ? DBNull.Value : Convert.ToDecimal(value.Value);
        }

        private static object DbIntAsDecimalValue(int? value)
        {
            return value is null ? DBNull.Value : Convert.ToDecimal(value.Value);
        }

        private async Task ExecuteWithLogging(string name, Func<Task> action)
        {
            LogAction?.Invoke($"{name} - počinje...");

            var stopwatch = Stopwatch.StartNew();

            try
            {
                await action();

                stopwatch.Stop();

                LogAction?.Invoke($"{name} - završeno za {stopwatch.Elapsed.TotalSeconds:N2} sekundi");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                LogAction?.Invoke($"{name} - greška posle {stopwatch.Elapsed.TotalSeconds:N2} sekundi: {ex.Message}");

                throw;
            }
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

        private async Task<List<ArtikalOsobine>> ReadArtikliOsobineBatch(long lastId, int batchSize, CancellationToken token)
        {
            const string sql = """
            SELECT
                ARTIKAL,
                OSNOVNASIFRA,
                OPCIJA,
                KATALOSKAOZNAKA,
                MALOPRODAJNAJM,
                VELEPRODAJNAJM,
                NARUCIVANJEJM,
                IZDAVANJEJM,
                TIPVARIJANTE,
                KOMITENTTIP,
                KOMITENT,
                AKCIZA,
                TAKSA,
                TARIFNISTAV,
                STAMPATIBARKOD,
                REFERENT,
                SERIJA,
                KONSIGNACIJA,
                ROKTRAJANJA,
                ROKTRAJANJADANA,
                BREND,
                PROIZVODJACTIP,
                PROIZVODJAC,
                REFERENTNABAVKE,
                TIPZALIHE,
                AKTIVAN,
                ASORTIMAN,
                KATEGORIJA,
                TRANSPORTJM,
                ODNOSPOLICA,
                ODNOSPALETA,
                TEZINA,
                ZAPREMINA,
                DUZINA,
                SIRINA,
                VISINA,
                VISINAPALETE,
                DRZAVA,
                VALUTA,
                ROKISPORUKE,
                SERIJAMLP,
                ROKTRAJANJAMLP,
                EKOLOSKANAKNADA,
                JEDINICNAMPJM,
                MLPJMPAR,
                OPIS
            FROM IIS.ARTIKLIOSOBINE
            WHERE ARTIKAL > :lastId
            ORDER BY ARTIKAL
            FETCH NEXT :batchSize ROWS ONLY
            """;

            var result = new List<ArtikalOsobine>(batchSize);

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
                result.Add(new ArtikalOsobine
                {
                    IdArtikla = GetInt64(reader, "ARTIKAL"),
                    OsnovnaSifra = GetString(reader, "OSNOVNASIFRA"),
                    Opcija = GetString(reader, "OPCIJA"),
                    KataloskaOznaka = GetString(reader, "KATALOSKAOZNAKA"),
                    MaloprodajnaJm = GetString(reader, "MALOPRODAJNAJM"),
                    VeleprodajnaJm = GetString(reader, "VELEPRODAJNAJM"),
                    NarucivanjeJm = GetString(reader, "NARUCIVANJEJM"),
                    IzdavanjeJm = GetString(reader, "IZDAVANJEJM"),
                    TipVarijante = GetString(reader, "TIPVARIJANTE"),
                    KomitentTip = GetString(reader, "KOMITENTTIP"),
                    Komitent = GetInt64(reader, "KOMITENT"),
                    Akciza = GetString(reader, "AKCIZA"),
                    Taksa = GetString(reader, "TAKSA"),
                    TarifniStav = GetString(reader, "TARIFNISTAV"),
                    StampatiBarkod = GetString(reader, "STAMPATIBARKOD"),
                    Referent = GetString(reader, "REFERENT"),
                    Serija = GetString(reader, "SERIJA"),
                    Konsignacija = GetString(reader, "KONSIGNACIJA"),
                    RokTrajanja = GetString(reader, "ROKTRAJANJA"),
                    RokTrajanjaDana = GetInt64(reader, "ROKTRAJANJADANA"),
                    Brend = GetString(reader, "BREND"),
                    ProizvodjacTip = GetString(reader, "PROIZVODJACTIP"),
                    Proizvodjac = GetInt64(reader, "PROIZVODJAC"),
                    ReferentNabavke = GetString(reader, "REFERENTNABAVKE"),
                    TipZalihe = GetString(reader, "TIPZALIHE"),
                    Aktivan = GetString(reader, "AKTIVAN"),
                    Asortiman = GetNullableInt64(reader, "ASORTIMAN"),
                    Kategorija = GetString(reader, "KATEGORIJA"),
                    TransportJm = GetString(reader, "TRANSPORTJM"),
                    OdnosPolica = GetDecimal(reader, "ODNOSPOLICA"),
                    OdnosPaleta = GetDecimal(reader, "ODNOSPALETA"),
                    Tezina = GetRequiredDecimal(reader, "TEZINA"),
                    Zapremina = GetRequiredDecimal(reader, "ZAPREMINA"),
                    Duzina = GetRequiredDecimal(reader, "DUZINA"),
                    Sirina = GetRequiredDecimal(reader, "SIRINA"),
                    Visina = GetRequiredDecimal(reader, "VISINA"),
                    VisinaPalete = GetDecimal(reader, "VISINAPALETE"),
                    Drzava = GetString(reader, "DRZAVA"),
                    Valuta = GetString(reader, "VALUTA"),
                    RokIsporuke = GetInt32(reader, "ROKISPORUKE"),
                    SerijaMlp = GetString(reader, "SERIJAMLP"),
                    RokTrajanjaMlp = GetString(reader, "ROKTRAJANJAMLP"),
                    EkoloskaNaknada = GetString(reader, "EKOLOSKANAKNADA"),
                    JedinicnaMpJm = GetString(reader, "JEDINICNAMPJM"),
                    MlpJmPar = GetString(reader, "MLPJMPAR"),
                    Opis = GetString(reader, "OPIS")
                });
            }

            return result;
        }

        private static DataTable CreateArtikliOsobineDataTable(List<ArtikalOsobine> artikliOsobine)
        {
            var table = new DataTable();

            table.Columns.Add("IDARTIKLA", typeof(decimal));
            table.Columns.Add("OSNOVNASIFRA", typeof(string));
            table.Columns.Add("OPCIJA", typeof(string));
            table.Columns.Add("KATALOSKAOZNAKA", typeof(string));
            table.Columns.Add("MALOPRODAJNAJM", typeof(string));
            table.Columns.Add("VELEPRODAJNAJM", typeof(string));
            table.Columns.Add("NARUCIVANJEJM", typeof(string));
            table.Columns.Add("IZDAVANJEJM", typeof(string));
            table.Columns.Add("TIPVARIJANTE", typeof(string));
            table.Columns.Add("KOMITENTTIP", typeof(string));
            table.Columns.Add("KOMITENT", typeof(decimal));
            table.Columns.Add("AKCIZA", typeof(string));
            table.Columns.Add("TAKSA", typeof(string));
            table.Columns.Add("TARIFNISTAV", typeof(string));
            table.Columns.Add("STAMPATIBARKOD", typeof(string));
            table.Columns.Add("REFERENT", typeof(string));
            table.Columns.Add("SERIJA", typeof(string));
            table.Columns.Add("KONSIGNACIJA", typeof(string));
            table.Columns.Add("ROKTRAJANJA", typeof(string));
            table.Columns.Add("ROKTRAJANJADANA", typeof(decimal));
            table.Columns.Add("BREND", typeof(string));
            table.Columns.Add("PROIZVODJACTIP", typeof(string));
            table.Columns.Add("PROIZVODJAC", typeof(decimal));
            table.Columns.Add("REFERENTNABAVKE", typeof(string));
            table.Columns.Add("TIPZALIHE", typeof(string));
            table.Columns.Add("AKTIVAN", typeof(string));
            table.Columns.Add("ASORTIMAN", typeof(decimal));
            table.Columns.Add("KATEGORIJA", typeof(string));
            table.Columns.Add("TRANSPORTJM", typeof(string));
            table.Columns.Add("ODNOSPOLICA", typeof(decimal));
            table.Columns.Add("ODNOSPALETA", typeof(decimal));
            table.Columns.Add("TEZINA", typeof(decimal));
            table.Columns.Add("ZAPREMINA", typeof(decimal));
            table.Columns.Add("DUZINA", typeof(decimal));
            table.Columns.Add("SIRINA", typeof(decimal));
            table.Columns.Add("VISINA", typeof(decimal));
            table.Columns.Add("VISINAPALETE", typeof(decimal));
            table.Columns.Add("DRZAVA", typeof(string));
            table.Columns.Add("VALUTA", typeof(string));
            table.Columns.Add("ROKISPORUKE", typeof(decimal));
            table.Columns.Add("SERIJAMLP", typeof(string));
            table.Columns.Add("ROKTRAJANJAMLP", typeof(string));
            table.Columns.Add("EKOLOSKANAKNADA", typeof(string));
            table.Columns.Add("JEDINICNAMPJM", typeof(string));
            table.Columns.Add("MLPJMPAR", typeof(string));
            table.Columns.Add("OPIS", typeof(string));

            foreach (var item in artikliOsobine)
            {
                table.Rows.Add(
                    Convert.ToDecimal(item.IdArtikla),
                    Required(item.OsnovnaSifra, item.IdArtikla, nameof(item.OsnovnaSifra)),
                    DbValue(item.Opcija),
                    DbValue(item.KataloskaOznaka),
                    Required(item.MaloprodajnaJm, item.IdArtikla, nameof(item.MaloprodajnaJm)),
                    Required(item.VeleprodajnaJm, item.IdArtikla, nameof(item.VeleprodajnaJm)),
                    Required(item.NarucivanjeJm, item.IdArtikla, nameof(item.NarucivanjeJm)),
                    Required(item.IzdavanjeJm, item.IdArtikla, nameof(item.IzdavanjeJm)),
                    Required(item.TipVarijante, item.IdArtikla, nameof(item.TipVarijante)),
                    Required(item.KomitentTip, item.IdArtikla, nameof(item.KomitentTip)),
                    Convert.ToDecimal(item.Komitent),
                    DbValue(item.Akciza),
                    DbValue(item.Taksa),
                    DbValue(item.TarifniStav),
                    Required(item.StampatiBarkod, item.IdArtikla, nameof(item.StampatiBarkod)),
                    DbValue(item.Referent),
                    Required(item.Serija, item.IdArtikla, nameof(item.Serija)),
                    Required(item.Konsignacija, item.IdArtikla, nameof(item.Konsignacija)),
                    Required(item.RokTrajanja, item.IdArtikla, nameof(item.RokTrajanja)),
                    Convert.ToDecimal(item.RokTrajanjaDana),
                    Required(item.Brend, item.IdArtikla, nameof(item.Brend)),
                    Required(item.ProizvodjacTip, item.IdArtikla, nameof(item.ProizvodjacTip)),
                    Convert.ToDecimal(item.Proizvodjac),
                    DbValue(item.ReferentNabavke),
                    Required(item.TipZalihe, item.IdArtikla, nameof(item.TipZalihe)),
                    Required(item.Aktivan, item.IdArtikla, nameof(item.Aktivan)),
                    DbLongAsDecimalValue(item.Asortiman),
                    Required(item.Kategorija, item.IdArtikla, nameof(item.Kategorija)),
                    Required(item.TransportJm, item.IdArtikla, nameof(item.TransportJm)),
                    DbValue(item.OdnosPolica),
                    DbValue(item.OdnosPaleta),
                    item.Tezina,
                    item.Zapremina,
                    item.Duzina,
                    item.Sirina,
                    item.Visina,
                    DbValue(item.VisinaPalete),
                    DbValue(item.Drzava),
                    DbValue(item.Valuta),
                    DbIntAsDecimalValue(item.RokIsporuke),
                    Required(item.SerijaMlp, item.IdArtikla, nameof(item.SerijaMlp)),
                    Required(item.RokTrajanjaMlp, item.IdArtikla, nameof(item.RokTrajanjaMlp)),
                    DbValue(item.EkoloskaNaknada),
                    DbValue(item.JedinicnaMpJm),
                    DbValue(item.MlpJmPar),
                    DbValue(item.Opis));
            }

            return table;
        }

        private async Task<List<Barkod>> ReadBarkodoviBatch(long lastId, int batchSize, CancellationToken token)
        {
                const string sql = """
            SELECT
                ID,
                BARKOD,
                ARTIKAL,
                VARIJANTA,
                JEDINICAMERE,
                REDNIBROJ
            FROM IIS.BARKODOVI
            WHERE ID > :lastId
            ORDER BY ID
            FETCH NEXT :batchSize ROWS ONLY
            """;

            var result = new List<Barkod>(batchSize);

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
                result.Add(new Barkod
                {
                    Id = GetInt64(reader, "ID"),
                    BarkodVrednost = GetString(reader, "BARKOD"),
                    IdArtikla = GetInt64(reader, "ARTIKAL"),
                    Varijanta = GetString(reader, "VARIJANTA"),
                    JedinicaMere = GetString(reader, "JEDINICAMERE"),
                    RedniBroj = GetRequiredInt32(reader, "REDNIBROJ")
                });
            }

            return result;
        }

        private static async Task MergeArtikliOsobine(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "dbo._pr_MergeArtikliOsobine";
            command.CommandType = CommandType.StoredProcedure;
            command.CommandTimeout = 120;

            await command.ExecuteNonQueryAsync(token);
        }

        private async Task TransferArtikliOsobine(int batchSize, CancellationToken token)
        {
            long lastId = 0;

            while (!token.IsCancellationRequested)
            {
                var artikliOsobine = await ReadArtikliOsobineBatch(lastId, batchSize, token);

                if (artikliOsobine.Count == 0)
                {
                    break;
                }

                await SaveArtikliOsobineToTmpTable(artikliOsobine, token);

                lastId = artikliOsobine[^1].IdArtikla;
            }
        }

        private async Task TransferBarkodovi(int batchSize, CancellationToken token)
        {
            long lastId = 0;

            while (!token.IsCancellationRequested)
            {
                var barkodovi = await ReadBarkodoviBatch(lastId, batchSize, token);

                if (barkodovi.Count == 0)
                {
                    break;
                }

                await SaveBarkodoviToTmpTable(barkodovi, token);

                lastId = barkodovi[^1].Id;
            }
        }

        private async Task SaveBarkodoviToTmpTable(List<Barkod> barkodovi, CancellationToken token)
        {
            await using var connection = new SqlConnection(connectionStrings.Transfer);
            await connection.OpenAsync(token);

            await using var transaction = await connection.BeginTransactionAsync(token);

            try
            {
                await ClearBarkodoviTmp(connection, (SqlTransaction)transaction, token);
                await BulkInsertBarkodoviTmp(connection, (SqlTransaction)transaction, barkodovi, token);
                await MergeBarkodovi(connection, (SqlTransaction)transaction, token);

                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }

        private async Task BulkInsertBarkodoviTmp(SqlConnection connection, SqlTransaction transaction, List<Barkod> barkodovi, CancellationToken token)
        {
            var table = CreateBarkodoviDataTable(barkodovi);

            using var bulkCopy = new SqlBulkCopy(
                connection,
                SqlBulkCopyOptions.CheckConstraints,
                transaction);

            bulkCopy.DestinationTableName = "dbo._tb_BARKODOVI_TMP";
            bulkCopy.BatchSize = barkodovi.Count;
            bulkCopy.BulkCopyTimeout = 60;

            bulkCopy.ColumnMappings.Add("ID", "ID");
            bulkCopy.ColumnMappings.Add("BARKOD", "BARKOD");
            bulkCopy.ColumnMappings.Add("IDARTIKLA", "IDARTIKLA");
            bulkCopy.ColumnMappings.Add("VARIJANTA", "VARIJANTA");
            bulkCopy.ColumnMappings.Add("JEDINICAMERE", "JEDINICAMERE");
            bulkCopy.ColumnMappings.Add("REDNIBROJ", "REDNIBROJ");

            await bulkCopy.WriteToServerAsync(table, token);
        }

        private static DataTable CreateBarkodoviDataTable(List<Barkod> barkodovi)
        {
            var table = new DataTable();

            table.Columns.Add("ID", typeof(decimal));
            table.Columns.Add("BARKOD", typeof(string));
            table.Columns.Add("IDARTIKLA", typeof(decimal));
            table.Columns.Add("VARIJANTA", typeof(string));
            table.Columns.Add("JEDINICAMERE", typeof(string));
            table.Columns.Add("REDNIBROJ", typeof(decimal));

            foreach (var item in barkodovi)
            {
                table.Rows.Add(
                    Convert.ToDecimal(item.Id),
                    Required(item.BarkodVrednost, item.Id, nameof(item.BarkodVrednost)),
                    Convert.ToDecimal(item.IdArtikla),
                    Required(item.Varijanta, item.Id, nameof(item.Varijanta)),
                    DbValue(item.JedinicaMere),
                    Convert.ToDecimal(item.RedniBroj));
            }

            return table;
        }

        private async Task ClearBarkodoviTmp(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "TRUNCATE TABLE dbo._tb_BARKODOVI_TMP;";

            await command.ExecuteNonQueryAsync(token);
        }

        private async Task SaveArtikliOsobineToTmpTable(List<ArtikalOsobine> artikliOsobine, CancellationToken token)
        {
            await using var connection = new SqlConnection(connectionStrings.Transfer);
            await connection.OpenAsync(token);

            await using var transaction = await connection.BeginTransactionAsync(token);

            try
            {
                await ClearArtikliOsobineTmp(connection, (SqlTransaction)transaction, token);
                await BulkInsertArtikliOsobineTmp(connection, (SqlTransaction)transaction, artikliOsobine, token);
                await MergeArtikliOsobine(connection, (SqlTransaction)transaction, token);

                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }

        private async Task BulkInsertArtikliOsobineTmp(SqlConnection connection, SqlTransaction transaction, List<ArtikalOsobine> artikliOsobine, CancellationToken token)
        {
            var table = CreateArtikliOsobineDataTable(artikliOsobine);

            using var bulkCopy = new SqlBulkCopy(
                connection,
                SqlBulkCopyOptions.CheckConstraints,
                transaction);

            bulkCopy.DestinationTableName = "dbo._tb_ARTIKLIOSOBINE_TMP";
            bulkCopy.BatchSize = artikliOsobine.Count;
            bulkCopy.BulkCopyTimeout = 60;

            foreach (DataColumn column in table.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(table, token);
        }

        private async Task ClearArtikliOsobineTmp(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "TRUNCATE TABLE dbo._tb_ARTIKLIOSOBINE_TMP;";

            await command.ExecuteNonQueryAsync(token);
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

        private static decimal? GetDecimal(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal));
        }

        private static decimal GetRequiredDecimal(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);

            if (reader.IsDBNull(ordinal))
            {
                throw new InvalidOperationException($"Oracle kolona {columnName} je NULL, a obavezna je.");
            }

            return Convert.ToDecimal(reader.GetValue(ordinal));
        }

        private static long? GetNullableInt64(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal));
        }
    }
}
