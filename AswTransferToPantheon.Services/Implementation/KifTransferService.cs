using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using Microsoft.Data.SqlClient;

namespace AswTransferToPantheon.Services.Implementation
{
    public class KifTransferService : IKifTransferService
    {
        private readonly ConnectionStrings connectionStrings;
        public Action<string>? LogAction { get; set; }
        public KifTransferService(IOptions<ConnectionStrings> connectionStrings)
        {
            this.connectionStrings = connectionStrings.Value;
        }

        public async Task Transfer(int batchSize, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            long lastId = 0;
            var totalKif = 0;
            var totalStavke = 0;
            var batchNumber = 0;

            LogAction?.Invoke("KIF - početak prenosa...");

            while (!token.IsCancellationRequested)
            {
                batchNumber++;

                var kifovi = await ReadKifBatch(lastId, batchSize, token);

                if (kifovi.Count == 0)
                {
                    break;
                }

                lastId = kifovi.Max(x => x.Id);

                var noviKifovi = await FilterExistingKif(kifovi, token);

                if (noviKifovi.Count == 0)
                {
                    LogAction?.Invoke($"KIF paket {batchNumber}: nema novih KIF-ova.");
                    continue;
                }

                var kifIds = noviKifovi
                    .Select(x => x.Id)
                    .ToList();

                var kifStavke = await ReadKifStavke(kifIds, token);

                await InsertKifPackage(noviKifovi, kifStavke, token);

                totalKif += noviKifovi.Count;
                totalStavke += kifStavke.Count;

                LogAction?.Invoke(
                    $"KIF paket {batchNumber}: ubačeno {noviKifovi.Count} KIF, {kifStavke.Count} stavki. Ukupno: {totalKif} KIF, {totalStavke} stavki.");
            }

            LogAction?.Invoke(
                $"KIF završen. Ukupno ubačeno: {totalKif} KIF, {totalStavke} stavki.");
        }

        private async Task<List<Kif>> FilterExistingKif(List<Kif> kifovi, CancellationToken token)
        {
            if (kifovi.Count == 0)
            {
                return [];
            }

            var ids = kifovi.Select(x => x.Id).ToList();

            var parameterNames = ids
                .Select((_, index) => $"@id{index}")
                .ToList();

            var sql = $"""
                    SELECT ID
                    FROM dbo.KIF
                    WHERE ID IN ({string.Join(", ", parameterNames)})
                    """;

            var existingIds = new HashSet<long>();

            await using var connection = new SqlConnection(connectionStrings.Transfer);
            await connection.OpenAsync(token);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            for (var i = 0; i < ids.Count; i++)
            {
                command.Parameters.AddWithValue($"@id{i}", ids[i]);
            }

            await using var reader = await command.ExecuteReaderAsync(token);

            while (await reader.ReadAsync(token))
            {
                existingIds.Add(Convert.ToInt64(reader["ID"]));
            }

            return kifovi
                .Where(x => !existingIds.Contains(x.Id))
                .ToList();
        }

        private async Task InsertKifPackage( List<Kif> kifovi, List<KifStavka> kifStavke, CancellationToken token)
        {
            await using var connection = new SqlConnection(connectionStrings.Transfer);
            await connection.OpenAsync(token);

            await using var transaction = await connection.BeginTransactionAsync(token);

            try
            {
                await BulkInsertKif(connection, (SqlTransaction)transaction, kifovi, token);
                await BulkInsertKifStavke(connection, (SqlTransaction)transaction, kifStavke, token);

                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }

        private async Task BulkInsertKifStavke(SqlConnection connection, SqlTransaction transaction, List<KifStavka> kifStavke, CancellationToken token)
        {
            if (kifStavke.Count == 0)
            {
                return;
            }

            var table = CreateKifStavkeDataTable(kifStavke);

            table.Columns.Add("INSERTUPDATE", typeof(string));

            foreach (DataRow row in table.Rows)
            {
                row["INSERTUPDATE"] = "I";
            }

            using var bulkCopy = new SqlBulkCopy(
                connection,
                SqlBulkCopyOptions.CheckConstraints,
                transaction);

            bulkCopy.DestinationTableName = "dbo.KIFSTAVKE";
            bulkCopy.BatchSize = kifStavke.Count;
            bulkCopy.BulkCopyTimeout = 60;

            foreach (DataColumn column in table.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(table);
        }

        private async Task BulkInsertKif(SqlConnection connection, SqlTransaction transaction, List<Kif> kifovi, CancellationToken token)
        {
            if (kifovi.Count == 0)
            {
                return;
            }

            var table = CreateKifDataTable(kifovi);

            table.Columns.Add("INSERTUPDATE", typeof(string));

            foreach (DataRow row in table.Rows)
            {
                row["INSERTUPDATE"] = "I";
            }

            using var bulkCopy = new SqlBulkCopy(
                connection,
                SqlBulkCopyOptions.CheckConstraints,
                transaction);

            bulkCopy.DestinationTableName = "dbo.KIF";
            bulkCopy.BatchSize = kifovi.Count;
            bulkCopy.BulkCopyTimeout = 60;

            foreach (DataColumn column in table.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(table);
        }

        private async Task SaveKifPackage(List<Kif> kifovi, List<KifStavka> kifStavke, CancellationToken token)
        {
            await using var connection = new SqlConnection(connectionStrings.Transfer);
            await connection.OpenAsync(token);

            await using var transaction = await connection.BeginTransactionAsync(token);

            try
            {
                await ClearKifTmp(connection, (SqlTransaction)transaction, token);
                await ClearKifStavkeTmp(connection, (SqlTransaction)transaction, token);

                await BulkInsertKifTmp(connection, (SqlTransaction)transaction, kifovi, token);
                await BulkInsertKifStavkeTmp(connection, (SqlTransaction)transaction, kifStavke, token);

                await MergeKif(connection, (SqlTransaction)transaction, token);
                await MergeKifStavke(connection, (SqlTransaction)transaction, token);

                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }

        private async Task MergeKifStavke(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "dbo._pr_MergeKifStavke";
            command.CommandType = CommandType.StoredProcedure;

            await command.ExecuteNonQueryAsync(token);
        }

        private async Task MergeKif(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "dbo._pr_MergeKif";
            command.CommandType = CommandType.StoredProcedure;

            await command.ExecuteNonQueryAsync(token);
        }

        private async Task<List<KifStavka>> ReadKifStavke(List<long> kifIds, CancellationToken token)
        {
            if (kifIds.Count == 0)
            {
                return [];
            }

            var parameterNames = kifIds
                .Select((_, index) => $":kif{index}")
                .ToList();

            var sql = $"""
                    SELECT
                        CAST(KIF AS NUMBER(18,0)) AS KIF,
                        CAST(BROJSTAVKE AS NUMBER(18,0)) AS BROJSTAVKE,
                        CAST(ARTIKAL AS NUMBER(18,0)) AS ARTIKAL,
                        OPIS,
                        CAST(KOLICINA AS NUMBER(18,4)) AS KOLICINA,
                        CAST(CENA AS NUMBER(18,4)) AS CENA,
                        CAST(POPUST AS NUMBER(18,4)) AS POPUST,
                        CAST(NABAVNA AS NUMBER(18,4)) AS NABAVNA,
                        CAST(IZNOS AS NUMBER(18,4)) AS IZNOS,
                        CAST(IZNOSSAPOREZOM AS NUMBER(18,4)) AS IZNOSSAPOREZOM,
                        CAST(IZNOSBEZPOPUSTA AS NUMBER(18,4)) AS IZNOSBEZPOPUSTA,
                        TARIFNAGRUPA,
                        TAKSA,
                        CAST(TAKSAIZNOS AS NUMBER(18,4)) AS TAKSAIZNOS,
                        CAST(IZNOSSAPOPUSTOM AS NUMBER(18,4)) AS IZNOSSAPOPUSTOM,
                        ORGJED,
                        CAST(EFPDVIZUZECEVRSTA AS NUMBER(18,0)) AS EFPDVIZUZECEVRSTA
                    FROM IIS.KIFSTAVKE
                    WHERE KIF IN ({string.Join(", ", parameterNames)})
                    ORDER BY KIF, BROJSTAVKE
                    """;

            var result = new List<KifStavka>();

            await using var connection = new OracleConnection(BuildOracleConnectionString());
            await connection.OpenAsync(token);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.BindByName = true;

            for (var i = 0; i < kifIds.Count; i++)
            {
                command.Parameters.Add($"kif{i}", OracleDbType.Int64).Value = kifIds[i];
            }

            await using var reader = await command.ExecuteReaderAsync(token);

            while (await reader.ReadAsync(token))
            {
                result.Add(new KifStavka
                {
                    IdKifa = GetInt64(reader, "KIF"),
                    BrojStavke = GetInt64(reader, "BROJSTAVKE"),
                    Artikal = GetInt64(reader, "ARTIKAL"),
                    Opis = GetString(reader, "OPIS"),
                    Kolicina = GetDecimal(reader, "KOLICINA"),
                    Cena = GetDecimal(reader, "CENA"),
                    Popust = GetDecimal(reader, "POPUST"),
                    Nabavna = GetDecimal(reader, "NABAVNA"),
                    Iznos = GetDecimal(reader, "IZNOS"),
                    IznosSaPorezom = GetDecimal(reader, "IZNOSSAPOREZOM"),
                    IznosBezPopusta = GetDecimal(reader, "IZNOSBEZPOPUSTA"),
                    TarifnaGrupa = GetString(reader, "TARIFNAGRUPA"),
                    Taksa = GetString(reader, "TAKSA"),
                    TaksaIznos = GetDecimal(reader, "TAKSAIZNOS"),
                    IznosSaPopustom = GetDecimal(reader, "IZNOSSAPOPUSTOM"),
                    OrgJed = GetString(reader, "ORGJED"),
                    EfPdvIzuzeceVrsta = GetNullableInt64(reader, "EFPDVIZUZECEVRSTA")
                });
            }

            return result;
        }

        /*private async Task SaveKifStavkeToTmpTable(List<KifStavka> kifStavke, CancellationToken token)
        {
            await using var connection = new SqlConnection(connectionStrings.Transfer);
            await connection.OpenAsync(token);

            await using var transaction = await connection.BeginTransactionAsync(token);

            try
            {
                await ClearKifStavkeTmp(connection, (SqlTransaction)transaction, token);
                await BulkInsertKifStavkeTmp(connection, (SqlTransaction)transaction, kifStavke, token);

                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }*/

        private async Task BulkInsertKifStavkeTmp(
            SqlConnection connection,
            SqlTransaction transaction,
            List<KifStavka> kifStavke,
            CancellationToken token)
        {
            if (kifStavke.Count == 0)
            {
                return;
            }

            var table = CreateKifStavkeDataTable(kifStavke);

            using var bulkCopy = new SqlBulkCopy(
                connection,
                SqlBulkCopyOptions.CheckConstraints,
                transaction);

            bulkCopy.DestinationTableName = "dbo._tb_KIFSTAVKE_TMP";
            bulkCopy.BatchSize = kifStavke.Count;
            bulkCopy.BulkCopyTimeout = 60;

            foreach (DataColumn column in table.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(table);
        }

        private DataTable CreateKifStavkeDataTable(List<KifStavka> kifStavke)
        {
            var table = new DataTable();

            table.Columns.Add("IDKIFA", typeof(decimal));
            table.Columns.Add("BROJSTAVKE", typeof(decimal));
            table.Columns.Add("ARTIKAL", typeof(decimal));
            table.Columns.Add("OPIS", typeof(string));
            table.Columns.Add("KOLICINA", typeof(double));
            table.Columns.Add("CENA", typeof(double));
            table.Columns.Add("POPUST", typeof(double));
            table.Columns.Add("NABAVNA", typeof(double));
            table.Columns.Add("IZNOS", typeof(double));
            table.Columns.Add("IZNOSSAPOREZOM", typeof(double));
            table.Columns.Add("IZNOSBEZPOPUSTA", typeof(double));
            table.Columns.Add("TARIFNAGRUPA", typeof(string));
            table.Columns.Add("TAKSA", typeof(string));
            table.Columns.Add("TAKSAIZNOS", typeof(double));
            table.Columns.Add("IZNOSSAPOPUSTOM", typeof(double));
            table.Columns.Add("ORGJED", typeof(string));
            table.Columns.Add("EFPDVIZUZECEVRSTA", typeof(decimal));

            foreach (var stavka in kifStavke)
            {
                table.Rows.Add(
                    stavka.IdKifa,
                    stavka.BrojStavke,
                    stavka.Artikal,
                    DbValue(stavka.Opis),
                    DbValue(stavka.Kolicina),
                    DbValue(stavka.Cena),
                    DbValue(stavka.Popust),
                    DbValue(stavka.Nabavna),
                    DbValue(stavka.Iznos),
                    DbValue(stavka.IznosSaPorezom),
                    DbValue(stavka.IznosBezPopusta),
                    DbValue(stavka.TarifnaGrupa),
                    DbValue(stavka.Taksa),
                    DbValue(stavka.TaksaIznos),
                    DbValue(stavka.IznosSaPopustom),
                    DbValue(stavka.OrgJed),
                    DbValue(stavka.EfPdvIzuzeceVrsta));
            }

            return table;
        }

        private async Task ClearKifStavkeTmp(
            SqlConnection connection,
            SqlTransaction transaction,
            CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "TRUNCATE TABLE dbo._tb_KIFSTAVKE_TMP;";

            await command.ExecuteNonQueryAsync(token);
        }

        /*private async Task SaveKifToTmpTable(List<Kif> kifovi, CancellationToken token)
        {
            await using var connection = new SqlConnection(connectionStrings.Transfer);
            await connection.OpenAsync(token);

            await using var transaction = await connection.BeginTransactionAsync(token);

            try
            {
                await ClearKifTmp(connection, (SqlTransaction)transaction, token);
                await BulkInsertKifTmp(connection, (SqlTransaction)transaction, kifovi, token);

                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }*/

        private async Task BulkInsertKifTmp(SqlConnection connection, SqlTransaction transaction, List<Kif> kifovi, CancellationToken token)
        {
            var table = CreateKifDataTable(kifovi);

            using var bulkCopy = new SqlBulkCopy(
                connection,
                SqlBulkCopyOptions.CheckConstraints,
                transaction);

            bulkCopy.DestinationTableName = "dbo._tb_KIF_TMP";
            bulkCopy.BatchSize = kifovi.Count;
            bulkCopy.BulkCopyTimeout = 60;

            foreach (DataColumn column in table.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(table);
        }

        private DataTable CreateKifDataTable(List<Kif> kifovi)
        {
            var table = new DataTable();

            table.Columns.Add("ID", typeof(decimal));
            table.Columns.Add("FIRMA", typeof(string));
            table.Columns.Add("DOKUMENT", typeof(decimal));
            table.Columns.Add("GODINA", typeof(decimal));
            table.Columns.Add("BROJ", typeof(decimal));
            table.Columns.Add("STORNO", typeof(string));
            table.Columns.Add("KOMITENTTIP", typeof(string));
            table.Columns.Add("KOMITENT", typeof(decimal));
            table.Columns.Add("DATUMDOKUMENTA", typeof(DateTime));
            table.Columns.Add("DATUMVALUTE", typeof(DateTime));
            table.Columns.Add("DATUMRACUNA", typeof(DateTime));
            table.Columns.Add("VALUTA", typeof(string));
            table.Columns.Add("ODNOS", typeof(double));
            table.Columns.Add("POPUST", typeof(double));
            table.Columns.Add("POREZ", typeof(string));
            table.Columns.Add("TIPKUPCA", typeof(string));
            table.Columns.Add("TIPIZLAZNOGRACUNA", typeof(string));
            table.Columns.Add("KORISNIK", typeof(string));
            table.Columns.Add("VREMEKREIRANJA", typeof(DateTime));
            table.Columns.Add("LIKVIDIRAN", typeof(string));
            table.Columns.Add("LIKVIDIRAO", typeof(string));
            table.Columns.Add("BROJIZJAVE", typeof(string));
            table.Columns.Add("DATUMIZJAVE", typeof(DateTime));
            table.Columns.Add("NALOG", typeof(decimal));
            table.Columns.Add("EKSTERNIBROJ", typeof(string));
            table.Columns.Add("NACINPLACANJA", typeof(decimal));
            table.Columns.Add("DATUMZAPOREZ", typeof(DateTime));
            table.Columns.Add("TAKSA", typeof(string));
            table.Columns.Add("DATUMPROMETADOBARAOD", typeof(DateTime));
            table.Columns.Add("DATUMPROMETADOBARADO", typeof(DateTime));
            table.Columns.Add("PORESKAKLAUZULA", typeof(string));
            table.Columns.Add("POZIVNABROJMALI", typeof(string));
            table.Columns.Add("POZIVNABROJ", typeof(string));
            table.Columns.Add("PORESKIOBVEZNIK", typeof(string));
            table.Columns.Add("AVANSBROJ", typeof(decimal));
            table.Columns.Add("AVANSIZNOS", typeof(decimal));
            table.Columns.Add("INCOTERMSKLAUZULA", typeof(string));

            foreach (var kif in kifovi)
            {
                table.Rows.Add(
                    kif.Id,
                    DbValue(kif.Firma),
                    DbValue(kif.Dokument),
                    DbValue(kif.Godina),
                    DbValue(kif.Broj),
                    DbValue(kif.Storno),
                    DbValue(kif.KomitentTip),
                    DbValue(kif.Komitent),
                    DbValue(kif.DatumDokumenta),
                    DbValue(kif.DatumValute),
                    DbValue(kif.DatumRacuna),
                    DbValue(kif.Valuta),
                    DbValue(kif.Odnos),
                    DbValue(kif.Popust),
                    DbValue(kif.Porez),
                    DbValue(kif.TipKupca),
                    DbValue(kif.TipIzlaznogRacuna),
                    DbValue(kif.Korisnik),
                    DbValue(kif.VremeKreiranja),
                    DbValue(kif.Likvidiran),
                    DbValue(kif.Likvidirao),
                    DbValue(kif.BrojIzjave),
                    DbValue(kif.DatumIzjave),
                    DbValue(kif.Nalog),
                    DbValue(kif.EksterniBroj),
                    DbValue(kif.NacinPlacanja),
                    DbValue(kif.DatumZaPorez),
                    DbValue(kif.Taksa),
                    DbValue(kif.DatumPrometaDobaraOd),
                    DbValue(kif.DatumPrometaDobaraDo),
                    DbValue(kif.PoreskaKlauzula),
                    DbValue(kif.PozivNaBrojMali),
                    DbValue(kif.PozivNaBroj),
                    DbValue(kif.PoreskiObveznik),
                    DbValue(kif.AvansBroj),
                    DbValue(kif.AvansIznos),
                    DbValue(kif.IncotermsKlauzula));
            }

            return table;
        }

        private static object DbValue<T>(T? value)
        {
            return value is null ? DBNull.Value : value;
        }

        private async Task ClearKifTmp(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "TRUNCATE TABLE dbo._tb_KIF_TMP;";

            await command.ExecuteNonQueryAsync(token);
        }

        private async Task<List<Kif>> ReadKifBatch(long lastId, int batchSize, CancellationToken token)
        {
            const string sql = """
                SELECT
                    ID,
                    FIRMA,
                    DOKUMENT,
                    GODINA,
                    BROJ,
                    STORNO,
                    KOMITENTTIP,
                    KOMITENT,
                    DATUMDOKUMENTA,
                    DATUMVALUTE,
                    DATUMRACUNA,
                    VALUTA,
                    ODNOS,
                    POPUST,
                    POREZ,
                    TIPKUPCA,
                    TIPIZLAZNOGRACUNA,
                    KORISNIK,
                    VREMEKREIRANJA,
                    LIKVIDIRAN,
                    LIKVIDIRAO,
                    BROJIZJAVE,
                    DATUMIZJAVE,
                    NALOG,
                    EKSTERNIBROJ,
                    NACINPLACANJA,
                    DATUMZAPOREZ,
                    TAKSA,
                    DATUMPROMETADOBARAOD,
                    DATUMPROMETADOBARADO,
                    PORESKAKLAUZULA,
                    POZIVNABROJMALI,
                    POZIVNABROJ,
                    PORESKIOBVEZNIK,
                    AVANSBROJ,
                    AVANSIZNOS,
                    INCOTERMSKLAUZULA
                FROM IIS.KIF
                WHERE ID > :lastId
                  AND LIKVIDIRAN = 'D'
                  AND DATUMDOKUMENTA >= SYSDATE - 30
                ORDER BY ID
                FETCH NEXT :batchSize ROWS ONLY
                """;

            var result = new List<Kif>(batchSize);

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
                result.Add(new Kif
                {
                    Id = GetInt64(reader, "ID"),
                    Firma = GetString(reader, "FIRMA"),
                    Dokument = GetNullableInt64(reader, "DOKUMENT"),
                    Godina = GetNullableInt64(reader, "GODINA"),
                    Broj = GetNullableInt64(reader, "BROJ"),
                    Storno = GetString(reader, "STORNO"),
                    KomitentTip = GetString(reader, "KOMITENTTIP"),
                    Komitent = GetNullableInt64(reader, "KOMITENT"),
                    DatumDokumenta = GetDateTime(reader, "DATUMDOKUMENTA"),
                    DatumValute = GetDateTime(reader, "DATUMVALUTE"),
                    DatumRacuna = GetDateTime(reader, "DATUMRACUNA"),
                    Valuta = GetString(reader, "VALUTA"),
                    Odnos = GetDecimal(reader, "ODNOS"),
                    Popust = GetDecimal(reader, "POPUST"),
                    Porez = GetString(reader, "POREZ"),
                    TipKupca = GetString(reader, "TIPKUPCA"),
                    TipIzlaznogRacuna = GetString(reader, "TIPIZLAZNOGRACUNA"),
                    Korisnik = GetString(reader, "KORISNIK"),
                    VremeKreiranja = GetDateTime(reader, "VREMEKREIRANJA"),
                    Likvidiran = GetString(reader, "LIKVIDIRAN"),
                    Likvidirao = GetString(reader, "LIKVIDIRAO"),
                    BrojIzjave = GetString(reader, "BROJIZJAVE"),
                    DatumIzjave = GetDateTime(reader, "DATUMIZJAVE"),
                    Nalog = GetNullableInt64(reader, "NALOG"),
                    EksterniBroj = GetString(reader, "EKSTERNIBROJ"),
                    NacinPlacanja = GetNullableInt64(reader, "NACINPLACANJA"),
                    DatumZaPorez = GetDateTime(reader, "DATUMZAPOREZ"),
                    Taksa = GetString(reader, "TAKSA"),
                    DatumPrometaDobaraOd = GetDateTime(reader, "DATUMPROMETADOBARAOD"),
                    DatumPrometaDobaraDo = GetDateTime(reader, "DATUMPROMETADOBARADO"),
                    PoreskaKlauzula = GetString(reader, "PORESKAKLAUZULA"),
                    PozivNaBrojMali = GetString(reader, "POZIVNABROJMALI"),
                    PozivNaBroj = GetString(reader, "POZIVNABROJ"),
                    PoreskiObveznik = GetString(reader, "PORESKIOBVEZNIK"),
                    AvansBroj = GetNullableInt64(reader, "AVANSBROJ"),
                    AvansIznos = GetDecimal(reader, "AVANSIZNOS"),
                    IncotermsKlauzula = GetString(reader, "INCOTERMSKLAUZULA")
                });
            }

            return result;
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

        private static string? GetString(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static long GetInt64(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return Convert.ToInt64(reader.GetValue(ordinal));
        }

        private static long? GetNullableInt64(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal));
        }

        private static DateTime? GetDateTime(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
        }

        private static decimal? GetDecimal(OracleDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal));
        }
    }
}