using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using AswTransferToPantheon.Services.Helpers;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class NaloziTransferService : INaloziTransferService
{
    private readonly ConnectionStrings connectionStrings;

    public Action<string>? LogAction { get; set; }

    public Action<string, string, string, string, Exception>? BadRecordAction { get; set; }

    public NaloziTransferService(IOptions<ConnectionStrings> connectionStrings)
    {
        this.connectionStrings = connectionStrings.Value;
    }

    public async Task Transfer(int batchSize, DateTime datumOd, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        long lastId = 0;
        var ukupnoNalozi = 0;
        var brojPaketa = 0;

        LogAction?.Invoke($"NALOZI - početak čitanja od {datumOd:yyyy-MM-dd}, " + "samo zatvoreni nalozi.");

        while (!token.IsCancellationRequested)
        {
            brojPaketa++;

            var nalozi = await ReadNaloziBatch(lastId, batchSize, datumOd, token);

            if (nalozi.Count == 0)
            {
                break;
            }

            var nalogIds = nalozi.Select(x => x.Id).ToList();

            var stavke = await ReadNaloziStavke(nalogIds, token);

            await SaveNaloziPackageWithFallback(nalozi, stavke, token);

            ukupnoNalozi += nalozi.Count;

            lastId = nalozi.Max(x => x.Id);

            LogAction?.Invoke($"NALOZI paket {brojPaketa}: " + $"{nalozi.Count} naloga i " + $"{stavke.Count} stavki. " + $"Ukupno naloga: {ukupnoNalozi}.");
        }

        LogAction?.Invoke($"NALOZI - završeno čitanje. " + $"Ukupno naloga: {ukupnoNalozi}.");
    }

    private async Task SaveNaloziPackage(List<Nalog> nalozi, List<NalogStavka> stavke, CancellationToken token)
    {
        await using var connection =new SqlConnection(connectionStrings.Transfer);

        await connection.OpenAsync(token);

        await using var transaction =await connection.BeginTransactionAsync(token);

        try
        {
            var sqlTransaction = (SqlTransaction)transaction;

            await ClearNaloziTmp(connection, sqlTransaction, token);

            await ClearNaloziStavkeTmp(connection, sqlTransaction, token);

            await BulkInsertNaloziTmp(connection, sqlTransaction, nalozi, token);

            await BulkInsertNaloziStavkeTmp(connection, sqlTransaction, stavke, token);

            await ExecuteInsertNalozi(connection, sqlTransaction, token);

            await transaction.CommitAsync(token);

            LogAction?.Invoke(
                $"NALOZI paket upisan u TMP tabele: " +
                $"{nalozi.Count} naloga i " +
                $"{stavke.Count} stavki.");
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private async Task SaveNaloziPackageWithFallback(List<Nalog> nalozi, List<NalogStavka> stavke, CancellationToken token)
    {
        try
        {
            await SaveNaloziPackage(nalozi, stavke, token);
        }
        catch (Exception batchException)
        {
            if (TransferErrorHelper.IsCriticalError(batchException))
            {
                throw;
            }

            LogAction?.Invoke(
                $"NALOZI paket od {nalozi.Count} naloga je neuspešan. " +
                "Pokušavam naloge i stavke pojedinačno.");

            foreach (var nalog in nalozi)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    // Prvo pokušavamo samo zaglavlje naloga.
                    await SaveNaloziPackage([nalog], [], token);
                }
                catch (Exception nalogException)
                {
                    BadRecordAction?.Invoke("NALOZI", $"ID={nalog.Id}", JsonSerializer.Serialize(nalog), "Greška pri upisu naloga.", nalogException);

                    continue;
                }

                var nalogStavke = stavke.Where(x => x.Nalog == nalog.Id).ToList();

                foreach (var stavka in nalogStavke)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        // Svaku stavku pokušavamo pojedinačno.
                        await SaveNaloziPackage([], [stavka], token);
                    }
                    catch (Exception stavkaException)
                    {
                        BadRecordAction?.Invoke(
                            "NALOZISTAVKE",
                            $"ID={stavka.Id}; NALOG={stavka.Nalog}",
                            JsonSerializer.Serialize(stavka),
                            "Greška pri upisu stavke naloga.",
                            stavkaException);
                    }
                }
            }
        }
    }

    private static async Task ExecuteInsertNalozi(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = "dbo._pr_InsertNalozi";
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = 300;

        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<List<NalogStavka>> ReadNaloziStavke(List<long> nalogIds, CancellationToken token)
    {
        if (nalogIds.Count == 0)
        {
            return [];
        }

        var parameterNames = nalogIds
            .Select((_, index) => $":nalog{index}")
            .ToList();

        var sql = $"""
        SELECT
            ID,
            NALOG,
            REDNIBROJ,
            KONTO,
            ORGJED,
            KOMITENTTIP,
            KOMITENT,
            DATUMDOKUMENTA,
            DATUMVALUTE,
            VALUTA,
            ODNOS,
            DUGUJE,
            POTRAZUJE,
            DUGUJEVAL,
            POTRAZUJEVAL,
            DOKUMENTID,
            DOKUMENTSTAVKA,
            DOKUMENTVRSTA,
            EKSTERNIBROJ,
            KOMENTAR,
            KORISNIK,
            VREMEKREIRANJA,
            POZIVNABROJ,
            IZNOSDOKUMENTA,
            IZNOSUPLATE,
            IZNOSZATVOREN,
            REFERENT
        FROM IIS.NALOZISTAVKE
        WHERE NALOG IN ({string.Join(", ", parameterNames)})
        ORDER BY NALOG, REDNIBROJ, ID
        """;

        var result = new List<NalogStavka>();

        await using var connection =
            new OracleConnection(BuildOracleConnectionString());

        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        command.BindByName = true;

        for (var i = 0; i < nalogIds.Count; i++)
        {
            command.Parameters.Add(
                $"nalog{i}",
                OracleDbType.Int64).Value = nalogIds[i];
        }

        await using var reader =
            await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            result.Add(new NalogStavka
            {
                Id = GetInt64(reader, "ID"),
                Nalog = GetNullableInt64(reader, "NALOG"),
                RedniBroj = GetInt64(reader, "REDNIBROJ"),
                Konto = GetString(reader, "KONTO"),
                OrgJed = GetString(reader, "ORGJED"),
                KomitentTip = GetString(reader, "KOMITENTTIP"),
                Komitent = GetNullableInt64(reader, "KOMITENT"),
                DatumDokumenta = GetDateTime(reader, "DATUMDOKUMENTA"),
                DatumValute = GetNullableDateTime(reader, "DATUMVALUTE"),
                Valuta = GetString(reader, "VALUTA"),
                Odnos = GetDecimal(reader, "ODNOS"),
                Duguje = GetDecimal(reader, "DUGUJE"),
                Potrazuje = GetDecimal(reader, "POTRAZUJE"),
                DugujeVal = GetDecimal(reader, "DUGUJEVAL"),
                PotrazujeVal = GetDecimal(reader, "POTRAZUJEVAL"),
                DokumentId = GetInt64(reader, "DOKUMENTID"),
                DokumentStavka = GetInt64(reader, "DOKUMENTSTAVKA"),
                DokumentVrsta = GetString(reader, "DOKUMENTVRSTA"),
                EksterniBroj = GetString(reader, "EKSTERNIBROJ"),
                Komentar = GetString(reader, "KOMENTAR"),
                Korisnik = GetString(reader, "KORISNIK"),
                VremeKreiranja = GetDateTime(reader, "VREMEKREIRANJA"),
                PozivniBroj = GetString(reader, "POZIVNABROJ"),
                IznosDokumenta = GetDecimal(reader, "IZNOSDOKUMENTA"),
                IznosUplate = GetDecimal(reader, "IZNOSUPLATE"),
                IznosZatvoren = GetDecimal(reader, "IZNOSZATVOREN"),
                Referent = GetString(reader, "REFERENT")
            });
        }

        return result;
    }
    private static DateTime? GetNullableDateTime(OracleDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : Convert.ToDateTime(reader.GetValue(ordinal));
    }
    private static long? GetNullableInt64(OracleDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal));
    }


    private static decimal GetDecimal(OracleDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal));
    }

    private async Task<List<Nalog>> ReadNaloziBatch(long lastId, int batchSize, DateTime datumOd, CancellationToken token)
    {
        const string sql = """
            SELECT
                ID,
                FIRMA,
                VRSTANALOGA,
                GODINA,
                BROJ,
                DATUM,
                ZATVOREN,
                KORISNIK,
                VREMEKREIRANJA,
                KOMENTAR
            FROM IIS.NALOZI
            WHERE ID > :lastId
              AND DATUM >= :datumOd
              AND ZATVOREN = 'D'
            ORDER BY ID
            FETCH NEXT :batchSize ROWS ONLY
            """;

        var result = new List<Nalog>(batchSize);

        await using var connection = new OracleConnection(BuildOracleConnectionString());

        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        command.BindByName = true;

        command.Parameters.Add("lastId", OracleDbType.Int64).Value = lastId;

        command.Parameters.Add("datumOd", OracleDbType.Date).Value = datumOd;

        command.Parameters.Add("batchSize", OracleDbType.Int32).Value = batchSize;

        await using var reader = await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            result.Add(new Nalog
            {
                Id = GetInt64(reader, "ID"),
                Firma = GetString(reader, "FIRMA"),
                VrstaNaloga = GetString(reader, "VRSTANALOGA"),
                Godina = GetInt64(reader, "GODINA"),
                Broj = GetInt64(reader, "BROJ"),
                Datum = GetDateTime(reader, "DATUM"),
                Zatvoren = GetString(reader, "ZATVOREN"),
                Korisnik = GetString(reader, "KORISNIK"),
                VremeKreiranja = GetDateTime(reader, "VREMEKREIRANJA"),
                Komentar = GetString(reader, "KOMENTAR")
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

        return reader.IsDBNull(ordinal)
            ? null
            : reader.GetString(ordinal);
    }

    private static long GetInt64(OracleDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return Convert.ToInt64(reader.GetValue(ordinal));
    }

    private static DateTime GetDateTime(OracleDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return Convert.ToDateTime(reader.GetValue(ordinal));
    }

    private async Task ClearNaloziTmp(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = "TRUNCATE TABLE dbo._tb_NALOZI_TMP;";

        await command.ExecuteNonQueryAsync(token);
    }

    private async Task BulkInsertNaloziTmp(SqlConnection connection, SqlTransaction transaction, List<Nalog> nalozi, CancellationToken token)
    {
        if (nalozi.Count == 0)
        {
            return;
        }

        var table = CreateNaloziDataTable(nalozi);

        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction);

        bulkCopy.DestinationTableName = "dbo._tb_NALOZI_TMP";

        bulkCopy.BatchSize = nalozi.Count;
        bulkCopy.BulkCopyTimeout = 120;

        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, token);
    }

    private static DataTable CreateNaloziDataTable(List<Nalog> nalozi)
    {
        var table = new DataTable();

        table.Columns.Add("ID", typeof(int));
        table.Columns.Add("FIRMA", typeof(string));
        table.Columns.Add("VRSTANALOGA", typeof(string));
        table.Columns.Add("GODINA", typeof(int));
        table.Columns.Add("BROJ", typeof(int));
        table.Columns.Add("DATUM", typeof(DateTime));
        table.Columns.Add("ZATVOREN", typeof(string));
        table.Columns.Add("KORISNIK", typeof(string));
        table.Columns.Add("VREMEKREIRANJA", typeof(DateTime));
        table.Columns.Add("KOMENTAR", typeof(string));

        foreach (var item in nalozi)
        {
            table.Rows.Add(
                checked((int)item.Id),
                DbValue(item.Firma),
                DbValue(item.VrstaNaloga),
                checked((int)item.Godina),
                checked((int)item.Broj),
                item.Datum,
                DbValue(item.Zatvoren),
                DbValue(item.Korisnik),
                item.VremeKreiranja,
                DbValue(item.Komentar));
        }

        return table;
    }

    private static object DbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }
    private async Task ClearNaloziStavkeTmp(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = "TRUNCATE TABLE dbo._tb_NALOZISTAVKE_TMP;";

        await command.ExecuteNonQueryAsync(token);
    }

    private async Task BulkInsertNaloziStavkeTmp(SqlConnection connection, SqlTransaction transaction, List<NalogStavka> stavke, CancellationToken token)
    {
        if (stavke.Count == 0)
        {
            return;
        }

        var table = CreateNaloziStavkeDataTable(stavke);

        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction);

        bulkCopy.DestinationTableName = "dbo._tb_NALOZISTAVKE_TMP";

        bulkCopy.BatchSize = stavke.Count;
        bulkCopy.BulkCopyTimeout = 120;

        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, token);
    }

    private static DataTable CreateNaloziStavkeDataTable(
    List<NalogStavka> stavke)
    {
        var table = new DataTable();

        table.Columns.Add("ID", typeof(int));
        table.Columns.Add("NALOG", typeof(int));
        table.Columns.Add("REDNIBROJ", typeof(int));
        table.Columns.Add("KONTO", typeof(string));
        table.Columns.Add("ORGJED", typeof(string));
        table.Columns.Add("KOMITENTTIP", typeof(string));
        table.Columns.Add("KOMITENT", typeof(int));
        table.Columns.Add("DATUMDOKUMENTA", typeof(DateTime));
        table.Columns.Add("DATUMVALUTE", typeof(DateTime));
        table.Columns.Add("VALUTA", typeof(string));
        table.Columns.Add("ODNOS", typeof(decimal));
        table.Columns.Add("DUGUJE", typeof(decimal));
        table.Columns.Add("POTRAZUJE", typeof(decimal));
        table.Columns.Add("DUGUJEVAL", typeof(decimal));
        table.Columns.Add("POTRAZUJEVAL", typeof(decimal));
        table.Columns.Add("DOKUMENTID", typeof(int));
        table.Columns.Add("DOKUMENTSTAVKA", typeof(int));
        table.Columns.Add("DOKUMENTVRSTA", typeof(string));
        table.Columns.Add("EKSTERNIBROJ", typeof(string));
        table.Columns.Add("KOMENTAR", typeof(string));
        table.Columns.Add("KORISNIK", typeof(string));
        table.Columns.Add("VREMEKREIRANJA", typeof(DateTime));
        table.Columns.Add("POZIVNABROJ", typeof(string));
        table.Columns.Add("IZNOSDOKUMENTA", typeof(decimal));
        table.Columns.Add("IZNOSUPLATE", typeof(decimal));
        table.Columns.Add("IZNOSZATVOREN", typeof(decimal));
        table.Columns.Add("REFERENT", typeof(string));

        foreach (var item in stavke)
        {
            table.Rows.Add(
                checked((int)item.Id),
                DbInt(item.Nalog),
                checked((int)item.RedniBroj),
                DbValue(item.Konto),
                DbValue(item.OrgJed),
                DbValue(item.KomitentTip),
                DbInt(item.Komitent),
                item.DatumDokumenta,
                DbValue(item.DatumValute),
                DbValue(item.Valuta),
                item.Odnos,
                item.Duguje,
                item.Potrazuje,
                item.DugujeVal,
                item.PotrazujeVal,
                checked((int)item.DokumentId),
                checked((int)item.DokumentStavka),
                DbValue(item.DokumentVrsta),
                DbValue(item.EksterniBroj),
                DbValue(item.Komentar),
                DbValue(item.Korisnik),
                item.VremeKreiranja,
                DbValue(item.PozivniBroj),
                item.IznosDokumenta,
                item.IznosUplate,
                item.IznosZatvoren,
                DbValue(item.Referent));
        }

        return table;
    }

    private static object DbInt(long? value)
    {
        return value.HasValue ? checked((int)value.Value) : DBNull.Value;
    }
}