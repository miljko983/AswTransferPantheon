using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;
using AswTransferToPantheon.Infrastructure.Models;
using Oracle.ManagedDataAccess.Client;
using Microsoft.Data.SqlClient;
using System.Data;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class VlpIzvSveTransferService : IVlpIzvSveTransferService
{
    private readonly ConnectionStrings connectionStrings;

    public Action<string>? LogAction { get; set; }

    public Action<string, string, string, string, Exception>? BadRecordAction { get; set; }

    public VlpIzvSveTransferService(IOptions<ConnectionStrings> connectionStrings)
    {
        this.connectionStrings = connectionStrings.Value;
    }

    public async Task Transfer(
    int batchSize,
    int daysBack,
    CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        long lastId = 0;
        var batchNumber = 0;
        var totalZaglavlja = 0;
        var totalStavke = 0;
        var totalVarijante = 0;

        LogAction?.Invoke(
            $"VLPIzvSve - početak prenosa. Poslednjih {daysBack} dana.");

        while (!token.IsCancellationRequested)
        {
            batchNumber++;

            var zaglavlja =
                await ReadVlpIzvSveZaglavljaBatch(
                    lastId,
                    batchSize,
                    daysBack,
                    token);

            if (zaglavlja.Count == 0)
            {
                break;
            }

            var zaglavljeIds = zaglavlja
                .Select(x => (long)x.Id)
                .ToList();

            var stavke = await ReadVlpIzvSveStavke(zaglavljeIds, token);

            var varijante =await ReadVlpIzvSveVarijante( zaglavljeIds, token);

            await SaveVlpIzvSvePackage(zaglavlja, stavke, varijante, token);

            totalZaglavlja += zaglavlja.Count;
            totalStavke += stavke.Count;
            totalVarijante += varijante.Count;

            lastId = zaglavlja[^1].Id;

            LogAction?.Invoke(
                $"VLPIzvSve paket {batchNumber}: " +
                $"{zaglavlja.Count} zaglavlja, " +
                $"{stavke.Count} stavki, " +
                $"{varijante.Count} varijanti. " +
                $"Ukupno: {totalZaglavlja} zaglavlja, " +
                $"{totalStavke} stavki, " +
                $"{totalVarijante} varijanti.");
        }

        LogAction?.Invoke(
            $"VLPIzvSve - prenos završen. " +
            $"Ukupno: {totalZaglavlja} zaglavlja, " +
            $"{totalStavke} stavki, " +
            $"{totalVarijante} varijanti.");
    }

    private async Task SaveVlpIzvSvePackage(List<VlpIzvSveZaglavlje> zaglavlja, List<VlpIzvSveStavka> stavke, List<VlpIzvSveVarijanta> varijante, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionStrings.Transfer);

        await connection.OpenAsync(token);

        await using var transaction = await connection.BeginTransactionAsync(token);

        var sqlTransaction = (SqlTransaction)transaction;

        try
        {
            await ClearTable(
                connection,
                sqlTransaction,
                "dbo._tb_VLPZAGLAVLJA_IZV_SVE_TMP",
                token);

            await ClearTable(
                connection,
                sqlTransaction,
                "dbo._tb_VLPSTAVKE_IZV_SVE_TMP",
                token);

            await ClearTable(
                connection,
                sqlTransaction,
                "dbo._tb_VLPVARIJANTE_IZV_SVE_TMP",
                token);

            await BulkInsertVlpZaglavlja(
                connection,
                sqlTransaction,
                zaglavlja,
                token);

            await BulkInsertVlpStavke(
                connection,
                sqlTransaction,
                stavke,
                token);

            await BulkInsertVlpVarijante(
                connection,
                sqlTransaction,
                varijante,
                token);

            await ExecuteMergeProcedure(
                connection,
                sqlTransaction,
                "dbo._pr_MergeVLPZAGLAVLJA_IZV_SVE",
                token);

            await ExecuteMergeProcedure(
                connection,
                sqlTransaction,
                "dbo._pr_MergeVLPSTAVKE_IZV_SVE",
                token);

            await ExecuteMergeProcedure(
                connection,
                sqlTransaction,
                "dbo._pr_MergeVLPVARIJANTE_IZV_SVE",
                token);

            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private static async Task BulkInsertVlpZaglavlja(SqlConnection connection, SqlTransaction transaction, List<VlpIzvSveZaglavlje> items, CancellationToken token)
    {
        if (items.Count == 0)
        {
            return;
        }

        var table = CreateVlpZaglavljaDataTable(items);

        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction);

        bulkCopy.DestinationTableName ="dbo._tb_VLPZAGLAVLJA_IZV_SVE_TMP";

        bulkCopy.BatchSize = items.Count;
        bulkCopy.BulkCopyTimeout = 60;

        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(
                column.ColumnName,
                column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, token);
    }

    private static async Task BulkInsertVlpStavke(SqlConnection connection, SqlTransaction transaction, List<VlpIzvSveStavka> items, CancellationToken token)
    {
        if (items.Count == 0)
        {
            return;
        }

        var table = CreateVlpStavkeDataTable(items);

        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction);

        bulkCopy.DestinationTableName ="dbo._tb_VLPSTAVKE_IZV_SVE_TMP";

        bulkCopy.BatchSize = items.Count;
        bulkCopy.BulkCopyTimeout = 60;

        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(
                column.ColumnName,
                column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, token);
    }

    private static DataTable CreateVlpStavkeDataTable(List<VlpIzvSveStavka> items)
    {
        var table = new DataTable();

        table.Columns.Add("VLPZAGLAVLJE", typeof(int));
        table.Columns.Add("REDNIBROJ", typeof(int));
        table.Columns.Add("ARTIKAL", typeof(int));
        table.Columns.Add("JEDINICAMERE", typeof(string));
        table.Columns.Add("TARIFNAGRUPA", typeof(string));
        table.Columns.Add("TAKSA", typeof(string));
        table.Columns.Add("AKCIZA", typeof(string));
        table.Columns.Add("NABAVNA", typeof(decimal));
        table.Columns.Add("VELEPRODAJNA", typeof(decimal));
        table.Columns.Add("MALOPRODAJNA", typeof(decimal));
        table.Columns.Add("RABAT", typeof(decimal));
        table.Columns.Add("PRODAJNACENA", typeof(string));
        table.Columns.Add("VELEPRODAJNAVAL", typeof(decimal));
        table.Columns.Add("BROJPAKETA", typeof(int));
        table.Columns.Add("DATUMVALUTE", typeof(DateTime));

        foreach (var item in items)
        {
            table.Rows.Add(
                item.VlpZaglavlje,
                item.RedniBroj,
                item.Artikal,
                item.JedinicaMere,
                item.TarifnaGrupa,
                DbValue(item.Taksa),
                DbValue(item.Akciza),
                item.Nabavna,
                item.Veleprodajna,
                item.Maloprodajna,
                item.Rabat,
                item.ProdajnaCena,
                item.VeleprodajnaVal,
                DbValue(item.BrojPaketa),
                DbValue(item.DatumValute));
        }

        return table;
    }
    private static async Task ExecuteMergeProcedure(SqlConnection connection, SqlTransaction transaction, string procedureName,  CancellationToken token)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = procedureName;
        command.CommandType = CommandType.StoredProcedure;

        await command.ExecuteNonQueryAsync(token);
    }       

    private static DataTable CreateVlpZaglavljaDataTable(List<VlpIzvSveZaglavlje> items)
    {
        var table = new DataTable();

        table.Columns.Add("ID", typeof(int));
        table.Columns.Add("ORGJED", typeof(string));
        table.Columns.Add("DOKUMENT", typeof(int));
        table.Columns.Add("GODINA", typeof(int));
        table.Columns.Add("BROJ", typeof(int));
        table.Columns.Add("STORNO", typeof(string));
        table.Columns.Add("SKLADISTE", typeof(string));
        table.Columns.Add("KOMITENTTIP", typeof(string));
        table.Columns.Add("KOMITENT", typeof(int));
        table.Columns.Add("DATUM", typeof(DateTime));
        table.Columns.Add("VREME", typeof(DateTime));
        table.Columns.Add("VREMEKREIRANJA", typeof(DateTime));
        table.Columns.Add("DATUMVALUTE", typeof(DateTime));
        table.Columns.Add("DATUMDPO", typeof(DateTime));
        table.Columns.Add("EKSTERNIBROJ", typeof(string));
        table.Columns.Add("POREZ", typeof(string));
        table.Columns.Add("KORISNIK", typeof(string));
        table.Columns.Add("KOMENTAR", typeof(string));
        table.Columns.Add("RABAT", typeof(decimal));
        table.Columns.Add("VLPZAGLAVLJE", typeof(int));
        table.Columns.Add("KIF", typeof(int));
        table.Columns.Add("NALOG", typeof(int));
        table.Columns.Add("POTVRDJEN", typeof(string));
        table.Columns.Add("POTVRDIO", typeof(string));
        table.Columns.Add("VREMEPOTVRDE", typeof(DateTime));
        table.Columns.Add("BROJIZJAVE", typeof(string));
        table.Columns.Add("DATUMIZJAVE", typeof(DateTime));
        table.Columns.Add("TIPDOBAVLJACA", typeof(string));
        table.Columns.Add("TIPKUPCA", typeof(string));
        table.Columns.Add("ZAVRSEN", typeof(string));
        table.Columns.Add("PRODAVAC", typeof(string));
        table.Columns.Add("PRODAJNACENA", typeof(string));
        table.Columns.Add("KOMISIONAR", typeof(string));
        table.Columns.Add("TAKSA", typeof(string));
        table.Columns.Add("VALUTA", typeof(string));
        table.Columns.Add("KURS", typeof(decimal));
        table.Columns.Add("NADREDJENITIP", typeof(string));
        table.Columns.Add("NADREDJENIKOMITENT", typeof(int));
        table.Columns.Add("ARTIKAL", typeof(int));
        table.Columns.Add("DODATNITROSAK", typeof(decimal));
        table.Columns.Add("POREZNATROSAK", typeof(decimal));
        table.Columns.Add("AVANSBROJ", typeof(int));
        table.Columns.Add("AVANSIZNOS", typeof(decimal));
        table.Columns.Add("PORESKAKLAUZULA", typeof(string));
        table.Columns.Add("NACINISPORUKE", typeof(string));
        table.Columns.Add("CENEZAKALKULACIJU", typeof(string));
        table.Columns.Add("BROJPAKETA", typeof(int));
        table.Columns.Add("DATUMRACUNA", typeof(DateTime));
        table.Columns.Add("BROJRACUNA", typeof(string));
        table.Columns.Add("STORNIRAN", typeof(string));

        foreach (var item in items)
        {
            table.Rows.Add(
                item.Id,
                item.OrgJed,
                item.Dokument,
                item.Godina,
                item.Broj,
                item.Storno,
                item.Skladiste,
                DbValue(item.KomitentTip),
                DbValue(item.Komitent),
                item.Datum,
                item.Vreme,
                item.VremeKreiranja,
                DbValue(item.DatumValute),
                DbValue(item.DatumDpo),
                DbValue(item.EksterniBroj),
                item.Porez,
                item.Korisnik,
                DbValue(item.Komentar),
                item.Rabat,
                item.VlpZaglavlje,
                DbValue(item.Kif),
                DbValue(item.Nalog),
                item.Potvrdjen,
                DbValue(item.Potvrdio),
                DbValue(item.VremePotvrde),
                DbValue(item.BrojIzjave),
                DbValue(item.DatumIzjave),
                DbValue(item.TipDobavljaca),
                DbValue(item.TipKupca),
                item.Zavrsen,
                DbValue(item.Prodavac),
                item.ProdajnaCena,
                DbValue(item.Komisionar),
                item.Taksa,
                item.Valuta,
                item.Kurs,
                DbValue(item.NadredjeniTip),
                DbValue(item.NadredjeniKomitent),
                DbValue(item.Artikal),
                DbValue(item.DodatniTrosak),
                DbValue(item.PorezNaTrosak),
                DbValue(item.AvansBroj),
                DbValue(item.AvansIznos),
                DbValue(item.PoreskaKlauzula),
                DbValue(item.NacinIsporuke),
                item.CeneZaKalkulaciju,
                DbValue(item.BrojPaketa),
                DbValue(item.DatumRacuna),
                DbValue(item.BrojRacuna),
                item.Storniran);
        }

        return table;
    }

    private static async Task BulkInsertVlpVarijante(SqlConnection connection, SqlTransaction transaction, List<VlpIzvSveVarijanta> items, CancellationToken token)
    {
        if (items.Count == 0)
        {
            return;
        }

        var table = CreateVlpVarijanteDataTable(items);

        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction);

        bulkCopy.DestinationTableName = "dbo._tb_VLPVARIJANTE_IZV_SVE_TMP";

        bulkCopy.BatchSize = items.Count;
        bulkCopy.BulkCopyTimeout = 60;

        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(
                column.ColumnName,
                column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, token);
    }

    private static DataTable CreateVlpVarijanteDataTable(List<VlpIzvSveVarijanta> items)
    {
        var table = new DataTable();

        table.Columns.Add("ID", typeof(int));
        table.Columns.Add("VLPZAGLAVLJE", typeof(int));
        table.Columns.Add("REDNIBROJ", typeof(int));
        table.Columns.Add("REDNIBROJVAR", typeof(int));
        table.Columns.Add("SKLADISTE", typeof(string));
        table.Columns.Add("ARTIKAL", typeof(int));
        table.Columns.Add("VARIJANTA", typeof(string));
        table.Columns.Add("ROKTRAJANJA", typeof(DateTime));
        table.Columns.Add("SERIJA", typeof(string));
        table.Columns.Add("TRAZENO", typeof(decimal));
        table.Columns.Add("ISPORUCENO", typeof(decimal));
        table.Columns.Add("KOLICINAULAZ", typeof(decimal));
        table.Columns.Add("KOLICINAIZLAZ", typeof(decimal));
        table.Columns.Add("SKLADISNA", typeof(decimal));
        table.Columns.Add("DUGUJE", typeof(decimal));
        table.Columns.Add("POTRAZUJE", typeof(decimal));

        foreach (var item in items)
        {
            table.Rows.Add(
                item.Id,
                item.VlpZaglavlje,
                item.RedniBroj,
                item.RedniBrojVar,
                item.Skladiste,
                item.Artikal,
                item.Varijanta,
                item.RokTrajanja,
                item.Serija,
                item.Trazeno,
                item.Isporuceno,
                item.KolicinaUlaz,
                item.KolicinaIzlaz,
                item.Skladisna,
                item.Duguje,
                item.Potrazuje);
        }

        return table;
    }
    private static async Task ClearTable(SqlConnection connection, SqlTransaction transaction, string tableName, CancellationToken token)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = $"TRUNCATE TABLE {tableName};";

        await command.ExecuteNonQueryAsync(token);
    }
    private async Task<List<VlpIzvSveZaglavlje>> ReadVlpIzvSveZaglavljaBatch(long lastId, int batchSize, int daysBack, CancellationToken token)
    {
        const string sql = """
                SELECT
                    ID,
                    ORGJED,
                    DOKUMENT,
                    GODINA,
                    BROJ,
                    STORNO,
                    SKLADISTE,
                    KOMITENTTIP,
                    KOMITENT,
                    DATUM,
                    VREME,
                    VREMEKREIRANJA,
                    DATUMVALUTE,
                    DATUMDPO,
                    EKSTERNIBROJ,
                    POREZ,
                    KORISNIK,
                    KOMENTAR,
                    RABAT,
                    VLPZAGLAVLJE,
                    KIF,
                    NALOG,
                    POTVRDJEN,
                    POTVRDIO,
                    VREMEPOTVRDE,
                    BROJIZJAVE,
                    DATUMIZJAVE,
                    TIPDOBAVLJACA,
                    TIPKUPCA,
                    ZAVRSEN,
                    PRODAVAC,
                    PRODAJNACENA,
                    KOMISIONAR,
                    TAKSA,
                    VALUTA,
                    KURS,
                    NADREDJENITIP,
                    NADREDJENIKOMITENT,
                    ARTIKAL,
                    DODATNITROSAK,
                    POREZNATROSAK,
                    AVANSBROJ,
                    AVANSIZNOS,
                    PORESKAKLAUZULA,
                    NACINISPORUKE,
                    CENEZAKALKULACIJU,
                    BROJPAKETA,
                    DATUMRACUNA,
                    BROJRACUNA,
                    STORNIRAN
                FROM IIS.VLPZAGLAVLJA
                WHERE ID > :lastId
                  AND DATUM >= SYSDATE - :daysBack
                ORDER BY ID
                FETCH NEXT :batchSize ROWS ONLY
                """;

        var result = new List<VlpIzvSveZaglavlje>(batchSize);

        await using var connection = new OracleConnection(BuildOracleConnectionString());

        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        command.BindByName = true;

        command.Parameters.Add("lastId", OracleDbType.Int64).Value = lastId;
        command.Parameters.Add("daysBack", OracleDbType.Int32).Value = daysBack;
        command.Parameters.Add("batchSize", OracleDbType.Int32).Value = batchSize;

        await using var reader =
            await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            result.Add(new VlpIzvSveZaglavlje
            {
                Id = GetRequiredInt32(reader, "ID"),
                OrgJed = GetRequiredString(reader, "ORGJED"),
                Dokument = GetRequiredInt32(reader, "DOKUMENT"),
                Godina = GetRequiredInt32(reader, "GODINA"),
                Broj = GetRequiredInt32(reader, "BROJ"),
                Storno = GetRequiredString(reader, "STORNO"),
                Skladiste = GetRequiredString(reader, "SKLADISTE"),
                KomitentTip = GetString(reader, "KOMITENTTIP"),
                Komitent = GetNullableInt32(reader, "KOMITENT"),
                Datum = GetRequiredDateTime(reader, "DATUM"),
                Vreme = GetRequiredDateTime(reader, "VREME"),
                VremeKreiranja = GetRequiredDateTime(reader, "VREMEKREIRANJA"),
                DatumValute = GetDateTime(reader, "DATUMVALUTE"),
                DatumDpo = GetDateTime(reader, "DATUMDPO"),
                EksterniBroj = GetString(reader, "EKSTERNIBROJ"),
                Porez = GetRequiredString(reader, "POREZ"),
                Korisnik = GetRequiredString(reader, "KORISNIK"),
                Komentar = GetString(reader, "KOMENTAR"),
                Rabat = GetRequiredDecimal(reader, "RABAT"),
                VlpZaglavlje = GetRequiredInt32(reader, "VLPZAGLAVLJE"),
                Kif = GetNullableInt32(reader, "KIF"),
                Nalog = GetNullableInt32(reader, "NALOG"),
                Potvrdjen = GetRequiredString(reader, "POTVRDJEN"),
                Potvrdio = GetString(reader, "POTVRDIO"),
                VremePotvrde = GetDateTime(reader, "VREMEPOTVRDE"),
                BrojIzjave = GetString(reader, "BROJIZJAVE"),
                DatumIzjave = GetDateTime(reader, "DATUMIZJAVE"),
                TipDobavljaca = GetString(reader, "TIPDOBAVLJACA"),
                TipKupca = GetString(reader, "TIPKUPCA"),
                Zavrsen = GetRequiredString(reader, "ZAVRSEN"),
                Prodavac = GetString(reader, "PRODAVAC"),
                ProdajnaCena = GetRequiredString(reader, "PRODAJNACENA"),
                Komisionar = GetString(reader, "KOMISIONAR"),
                Taksa = GetRequiredString(reader, "TAKSA"),
                Valuta = GetRequiredString(reader, "VALUTA"),
                Kurs = GetRequiredDecimal(reader, "KURS"),
                NadredjeniTip = GetString(reader, "NADREDJENITIP"),
                NadredjeniKomitent =
                    GetNullableInt32(reader, "NADREDJENIKOMITENT"),
                Artikal = GetNullableInt32(reader, "ARTIKAL"),
                DodatniTrosak = GetNullableDecimal(reader, "DODATNITROSAK"),
                PorezNaTrosak = GetNullableDecimal(reader, "POREZNATROSAK"),
                AvansBroj = GetNullableInt32(reader, "AVANSBROJ"),
                AvansIznos = GetNullableDecimal(reader, "AVANSIZNOS"),
                PoreskaKlauzula = GetString(reader, "PORESKAKLAUZULA"),
                NacinIsporuke = GetString(reader, "NACINISPORUKE"),
                CeneZaKalkulaciju =
                    GetRequiredString(reader, "CENEZAKALKULACIJU"),
                BrojPaketa = GetNullableInt32(reader, "BROJPAKETA"),
                DatumRacuna = GetDateTime(reader, "DATUMRACUNA"),
                BrojRacuna = GetString(reader, "BROJRACUNA"),
                Storniran = GetRequiredString(reader, "STORNIRAN")
            });
        }

        return result;
    }

    private string BuildOracleConnectionString()
    {
        var builder = new OracleConnectionStringBuilder
        {
            UserID = connectionStrings.AswUser,
            Password = connectionStrings.AswPassword,
            DataSource = connectionStrings.AswDataSource
        };

        return builder.ConnectionString;
    }

    private static string? GetString(OracleDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));
    }

    private static string GetRequiredString(OracleDataReader reader, string columnName)
    {
        var value = GetString(reader, columnName);

        if (value is null)
        {
            throw new InvalidOperationException($"Oracle kolona {columnName} je NULL, a obavezna je.");
        }

        return value;
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

    private static int? GetNullableInt32(OracleDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static DateTime GetRequiredDateTime(OracleDataReader reader, string columnName)
    {
        {
            var ordinal = reader.GetOrdinal(columnName);

            if (reader.IsDBNull(ordinal))
            {
                throw new InvalidOperationException($"Oracle kolona {columnName} je NULL, a obavezna je.");
            }

            return reader.GetDateTime(ordinal);
        }
    }

    private static DateTime? GetDateTime(OracleDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
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

    private static decimal? GetNullableDecimal(OracleDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal));
    }

    private async Task<List<VlpIzvSveStavka>> ReadVlpIzvSveStavke(List<long> zaglavljeIds, CancellationToken token)
    {
        if (zaglavljeIds.Count == 0)
        {
            return [];
        }

        var parameterNames = zaglavljeIds.Select((_, index) => $":zaglavlje{index}").ToList();

        var sql = $"""
                SELECT
                    CAST(VLPZAGLAVLJE AS NUMBER(10, 0)) AS VLPZAGLAVLJE,
                    CAST(REDNIBROJ AS NUMBER(10, 0)) AS REDNIBROJ,
                    CAST(ARTIKAL AS NUMBER(10, 0)) AS ARTIKAL,
                    JEDINICAMERE,
                    TARIFNAGRUPA,
                    TAKSA,
                    AKCIZA,
                    CAST(NABAVNA AS NUMBER(20, 4)) AS NABAVNA,
                    CAST(VELEPRODAJNA AS NUMBER(20, 4)) AS VELEPRODAJNA,
                    CAST(MALOPRODAJNA AS NUMBER(16, 2)) AS MALOPRODAJNA,
                    CAST(RABAT AS NUMBER(16, 4)) AS RABAT,
                    PRODAJNACENA,
                    CAST(VELEPRODAJNAVAL AS NUMBER(20, 4)) AS VELEPRODAJNAVAL,
                    CAST(BROJPAKETA AS NUMBER(10, 0)) AS BROJPAKETA,
                    DATUMVALUTE
                FROM IIS.VLPSTAVKE
                WHERE VLPZAGLAVLJE IN ({string.Join(", ", parameterNames)})
                ORDER BY VLPZAGLAVLJE, REDNIBROJ
                """;

        var result = new List<VlpIzvSveStavka>();

        await using var connection =
            new OracleConnection(BuildOracleConnectionString());

        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        command.BindByName = true;

        for (var i = 0; i < zaglavljeIds.Count; i++)
        {
            command.Parameters.Add(
                $"zaglavlje{i}",
                OracleDbType.Int64).Value = zaglavljeIds[i];
        }

        await using var reader =
            await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            result.Add(new VlpIzvSveStavka
            {
                VlpZaglavlje = GetRequiredInt32(reader, "VLPZAGLAVLJE"),
                RedniBroj = GetRequiredInt32(reader, "REDNIBROJ"),
                Artikal = GetRequiredInt32(reader, "ARTIKAL"),
                JedinicaMere = GetRequiredString(reader, "JEDINICAMERE"),
                TarifnaGrupa = GetRequiredString(reader, "TARIFNAGRUPA"),
                Taksa = GetString(reader, "TAKSA"),
                Akciza = GetString(reader, "AKCIZA"),
                Nabavna = GetRequiredDecimal(reader, "NABAVNA"),
                Veleprodajna = GetRequiredDecimal(reader, "VELEPRODAJNA"),
                Maloprodajna = GetRequiredDecimal(reader, "MALOPRODAJNA"),
                Rabat = GetRequiredDecimal(reader, "RABAT"),
                ProdajnaCena = GetRequiredString(reader, "PRODAJNACENA"),
                VeleprodajnaVal = GetRequiredDecimal(reader, "VELEPRODAJNAVAL"),
                BrojPaketa = GetNullableInt32(reader, "BROJPAKETA"),
                DatumValute = GetDateTime(reader, "DATUMVALUTE")
            });
        }
        return result;
    }
    private async Task<List<VlpIzvSveVarijanta>> ReadVlpIzvSveVarijante(List<long> zaglavljeIds, CancellationToken token)
    {
        if (zaglavljeIds.Count == 0)
        {
            return [];
        }

        var parameterNames = zaglavljeIds
            .Select((_, index) => $":zaglavlje{index}")
            .ToList();

        var sql = $"""
                SELECT
                        CAST(ID AS NUMBER(10, 0)) AS ID,
                        CAST(VLPZAGLAVLJE AS NUMBER(10, 0)) AS VLPZAGLAVLJE,
                        CAST(REDNIBROJ AS NUMBER(10, 0)) AS REDNIBROJ,
                        CAST(REDNIBROJVAR AS NUMBER(5, 0)) AS REDNIBROJVAR,
                        SKLADISTE,
                        CAST(ARTIKAL AS NUMBER(10, 0)) AS ARTIKAL,
                        VARIJANTA,
                        ROKTRAJANJA,
                        SERIJA,
                        CAST(TRAZENO AS NUMBER(16, 3)) AS TRAZENO,
                        CAST(ISPORUCENO AS NUMBER(16, 3)) AS ISPORUCENO,
                        CAST(KOLICINAULAZ AS NUMBER(16, 3)) AS KOLICINAULAZ,
                        CAST(KOLICINAIZLAZ AS NUMBER(16, 3)) AS KOLICINAIZLAZ,
                        CAST(SKLADISNA AS NUMBER(16, 3)) AS SKLADISNA,
                        CAST(DUGUJE AS NUMBER(16, 2)) AS DUGUJE,
                        CAST(POTRAZUJE AS NUMBER(16, 2)) AS POTRAZUJE
                FROM IIS.VLPVARIJANTE
                WHERE VLPZAGLAVLJE IN ({string.Join(", ", parameterNames)})
                ORDER BY VLPZAGLAVLJE, REDNIBROJ, REDNIBROJVAR, ID
                """;

        var result = new List<VlpIzvSveVarijanta>();

        await using var connection =
            new OracleConnection(BuildOracleConnectionString());

        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        command.BindByName = true;

        for (var i = 0; i < zaglavljeIds.Count; i++)
        {
            command.Parameters.Add($"zaglavlje{i}", OracleDbType.Int64).Value = zaglavljeIds[i];
        }

        await using var reader = await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            result.Add(new VlpIzvSveVarijanta
            {
                Id = GetRequiredInt32(reader, "ID"),
                VlpZaglavlje = GetRequiredInt32(reader, "VLPZAGLAVLJE"),
                RedniBroj = GetRequiredInt32(reader, "REDNIBROJ"),
                RedniBrojVar = GetRequiredInt32(reader, "REDNIBROJVAR"),
                Skladiste = GetRequiredString(reader, "SKLADISTE"),
                Artikal = GetRequiredInt32(reader, "ARTIKAL"),
                Varijanta = GetRequiredString(reader, "VARIJANTA"),
                RokTrajanja = GetRequiredDateTime(reader, "ROKTRAJANJA"),
                Serija = GetRequiredString(reader, "SERIJA"),
                Trazeno = GetRequiredDecimal(reader, "TRAZENO"),
                Isporuceno = GetRequiredDecimal(reader, "ISPORUCENO"),
                KolicinaUlaz = GetRequiredDecimal(reader, "KOLICINAULAZ"),
                KolicinaIzlaz = GetRequiredDecimal(reader, "KOLICINAIZLAZ"),
                Skladisna = GetRequiredDecimal(reader, "SKLADISNA"),
                Duguje = GetRequiredDecimal(reader, "DUGUJE"),
                Potrazuje = GetRequiredDecimal(reader, "POTRAZUJE")
            });
        }

        return result;
    }
    private static object DbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }

}

