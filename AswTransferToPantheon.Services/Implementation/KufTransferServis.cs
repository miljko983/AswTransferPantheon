using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using System.Text.Json;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class KufTransferService : IKufTransferService
{
    private readonly ConnectionStrings connectionStrings;

    public Action<string>? LogAction { get; set; }
    public Action<string, string, string, string, Exception>? BadRecordAction { get; set; }

    public KufTransferService(IOptions<ConnectionStrings> connectionStrings)
    {
        this.connectionStrings = connectionStrings.Value;
    }

    public async Task Transfer(int batchSize, CancellationToken token)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        token.ThrowIfCancellationRequested();
        long lastId = 0;
        long totalRead = 0, totalInserted = 0, totalExisting = 0, totalRejected = 0;
        var batchNumber = 0;
        LogAction?.Invoke("KUF - početak prenosa likvidiranih računa od 01.08.2026.");

        while (true)
        {
            token.ThrowIfCancellationRequested();
            var batch = await ReadKufBatch(lastId, batchSize, token);
            if (batch.ReadCount == 0)
                break;

            lastId = batch.LastId;
            batchNumber++;
            totalRead += batch.ReadCount;
            totalRejected += batch.RejectedCount;

            var newRows = await FilterExistingKuf(batch.Rows, token);
            totalExisting += batch.Rows.Count - newRows.Count;

            var readyRows = await ReadAndApplyVlpLinks(newRows, token);
            totalRejected += newRows.Count - readyRows.Count;

            var inserted = await InsertKufPackage(readyRows, token);
            totalInserted += inserted;
            totalExisting += readyRows.Count - inserted;

            LogAction?.Invoke(
                $"KUF paket {batchNumber}: pročitano {batch.ReadCount}, " +
                $"upisano {inserted}, neispravnih " +
                $"{batch.RejectedCount + newRows.Count - readyRows.Count}.");
        }

        LogAction?.Invoke(
            $"KUF završen. Pročitano {totalRead}, upisano {totalInserted}, " +
            $"već postojećih {totalExisting}, neispravnih {totalRejected}.");

        // Radi i kada nije bilo novih KUF računa: veze mogu stići naknadno.
        await TransferVlpKuf(batchSize, token);
        LogAction?.Invoke("KUF i VLPKUF - obrada je završena.");
    }

    private async Task<(List<Kuf> Rows, long LastId, int ReadCount, int RejectedCount)>
        ReadKufBatch(long lastId, int batchSize, CancellationToken token)
    {
        const string sql = """
            SELECT
                ID,
                FIRMA,
                DOKUMENT,
                GODINA,
                BROJ,
                STORNO,
                TIPDOBAVLJACA,
                KOMITENTTIP,
                KOMITENT,
                ZIRORACUN,
                DATUMDOKUMENTA,
                DATUMVALUTE,
                DATUMPRIJEMA,
                DATUMRACUNA,
                EKSTERNIBROJ,
                POZIVNABROJMALI,
                POZIVNABROJ,
                KOMENTAR,
                VALUTA,
                ODNOS,
                NALOG,
                KORISNIK,
                VREME,
                LIKVIDIRAN,
                LIKVIDIRAO,
                VREMELIKVIDACIJE,
                TIPULAZNOGRACUNA,
                POREZ,
                DATUMZAPOREZ,
                PORESKIOBVEZNIK,
                DATUMDPO,
                DATUMKNJIZENJA,
                BROJOTPREMNICE,
                UKUPANIZNOS
            FROM IIS.KUF
            WHERE ID > :lastId
              AND LIKVIDIRAN = 'D'
              AND DATUMRACUNA >= DATE '2026-08-01'
            ORDER BY ID
            FETCH NEXT :batchSize ROWS ONLY
            """;

        var rows = new List<Kuf>();
        var readCount = 0;
        var rejectedCount = 0;
        var nextId = lastId;
        await using var connection = new OracleConnection(BuildOracleConnectionString());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        command.BindByName = true;
        command.Parameters.Add("lastId", OracleDbType.Int64).Value = lastId;
        command.Parameters.Add("batchSize", OracleDbType.Int32).Value = batchSize;

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = ReadRequired<long>(reader, "ID");
            nextId = Math.Max(nextId, id);
            readCount++;
            try
            {
                var row = MapKuf(reader);
                ValidateKuf(row);
                rows.Add(row);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or InvalidCastException or
                OverflowException or FormatException or ArgumentException)
            {
                rejectedCount++;
                ReportBadRecord(
                    "KUF", $"ID={id}", JsonSerializer.Serialize(new { ID = id }),
                    $"Neispravan KUF zapis: {exception.Message}", exception);
            }
        }

        // ID poslednjeg pročitanog reda napreduje i kada je ceo paket neispravan.
        return (rows, nextId, readCount, rejectedCount);
    }

    private async Task<List<Kuf>> FilterExistingKuf(List<Kuf> rows, CancellationToken token)
    {
        if (rows.Count == 0)
            return [];

        await using var connection = new SqlConnection(connectionStrings.Transfer);
        await connection.OpenAsync(token);
        var existing = await ReadExistingIds(connection, null, rows, token);
        return rows.Where(row => !existing.Contains(row.Id)).ToList();
    }

    private static async Task<HashSet<long>> ReadExistingIds(
        SqlConnection connection, SqlTransaction? transaction,
        List<Kuf> rows, CancellationToken token)
    {
        var existing = new HashSet<long>();
        var lockHint = transaction is null ? string.Empty : "WITH (UPDLOCK, HOLDLOCK)";
        foreach (var chunk in rows.Select(row => row.Id).Distinct().OrderBy(id => id).Chunk(500))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 120;
            var names = chunk.Select((_, i) => $"@id{i}").ToArray();
            command.CommandText = $"""
                SELECT ID
                FROM dbo.KUF {lockHint}
                WHERE ID IN ({string.Join(", ", names)});
                """;
            for (var i = 0; i < chunk.Length; i++)
                command.Parameters.Add(names[i], SqlDbType.BigInt).Value = chunk[i];

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                existing.Add(Convert.ToInt64(reader["ID"]));
        }
        return existing;
    }

    private async Task<List<Kuf>> ReadAndApplyVlpLinks(List<Kuf> rows, CancellationToken token)
    {
        if (rows.Count == 0)
            return [];

        var links = new Dictionary<long, HashSet<long>>();
        await using var connection = new OracleConnection(BuildOracleConnectionString());
        await connection.OpenAsync(token);

        foreach (var chunk in rows.Select(row => row.Id).Distinct().OrderBy(id => id).Chunk(500))
        {
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandTimeout = 120;
            var names = chunk.Select((_, i) => $":id{i}").ToArray();
            command.CommandText = $"""
                SELECT KUF, VLPZAGLAVLJE
                FROM IIS.VLPKUF
                WHERE KUF IN ({string.Join(", ", names)})
                  AND VLPZAGLAVLJE IS NOT NULL
                ORDER BY KUF, VLPZAGLAVLJE
                """;
            for (var i = 0; i < chunk.Length; i++)
                command.Parameters.Add($"id{i}", OracleDbType.Int64).Value = chunk[i];

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var kufId = ReadRequired<long>(reader, "KUF");
                var vlpId = ReadRequired<long>(reader, "VLPZAGLAVLJE");
                if (!links.TryGetValue(kufId, out var linkedIds))
                {
                    linkedIds = [];
                    links.Add(kufId, linkedIds);
                }
                linkedIds.Add(vlpId);
            }
        }
        return ApplyVlpLinks(rows, links);
    }

    private List<Kuf> ApplyVlpLinks(List<Kuf> rows, Dictionary<long, HashSet<long>> links)
    {
        var result = new List<Kuf>();
        foreach (var row in rows)
        {
            row.VlpZaglavlje = null;
            if (links.TryGetValue(row.Id, out var linkedIds) && linkedIds.Count > 0)
            {
                if (linkedIds.Count > 1)
                {
                    // Sve veze se prenose u dbo.VLPKUF; jedna kolona ne bira proizvoljan ID.
                    LogAction?.Invoke(
                        $"KUF ID={row.Id}: {linkedIds.Count} VLP veza. " +
                        "Polje KUF.VLPZAGLAVLJE ostaje NULL; sve veze obrađuje VLPKUF prenos.");
                    result.Add(row);
                    continue;
                }

                var vlpId = linkedIds.Single();
                if (vlpId < -9999999999L || vlpId > 9999999999L)
                {
                    var message = $"VLPZAGLAVLJE={vlpId} ne staje u NUMERIC(10,0).";
                    ReportBadRecord("VLPKUF", $"KUF={row.Id}", vlpId.ToString(),
                        message, new InvalidDataException(message));
                    continue;
                }
                row.VlpZaglavlje = vlpId;
            }
            result.Add(row);
        }
        return result;
    }

    private async Task<int> InsertKufPackage(List<Kuf> rows, CancellationToken token)
    {
        if (rows.Count == 0)
            return 0;

        await using var connection = new SqlConnection(connectionStrings.Transfer);
        await connection.OpenAsync(token);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // Ponovna provera u istoj transakciji štiti i od paralelnog upisa istih ID-eva.
            var existing = await ReadExistingIds(connection, transaction, rows, token);
            var newRows = rows.Where(row => !existing.Contains(row.Id)).ToList();
            if (newRows.Count > 0)
            {
                using var table = CreateKufDataTable(newRows);
                using var bulkCopy = new SqlBulkCopy(
                    connection,
                    SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls,
                    transaction);
                bulkCopy.DestinationTableName = "dbo.KUF";
                bulkCopy.BatchSize = newRows.Count;
                bulkCopy.BulkCopyTimeout = 120;
                foreach (DataColumn column in table.Columns)
                    bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);

                await bulkCopy.WriteToServerAsync(table, token);
            }
            await transaction.CommitAsync(token);
            return newRows.Count;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                LogAction?.Invoke($"KUF - greška pri poništavanju paketa: {rollbackException.Message}");
            }
            throw;
        }
    }

    private async Task TransferVlpKuf(int batchSize, CancellationToken token)
    {
        // Oracle IN lista ostaje ispod ograničenja; čitaju se svi SQL KUF ID-evi.
        var idBatchSize = Math.Min(batchSize, 500);
        long? lastKufId = null;
        long totalKuf = 0, totalRead = 0, totalInserted = 0;
        long totalExisting = 0, totalRejected = 0;

        LogAction?.Invoke("VLPKUF - početak prenosa veza za sve račune iz dbo.KUF.");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var kufIds = await ReadTransferredKufIds(lastKufId, idBatchSize, token);
            if (kufIds.Count == 0)
                break;

            lastKufId = kufIds[^1];
            totalKuf += kufIds.Count;
            var batch = await ReadVlpKufBatch(kufIds, token);
            totalRead += batch.ReadCount;
            totalRejected += batch.RejectedCount;

            var inserted = await InsertVlpKufPackage(batch.Rows, token);
            totalInserted += inserted;
            totalExisting += batch.Rows.Count - inserted;

            if (inserted > 0 || batch.RejectedCount > 0)
            {
                LogAction?.Invoke(
                    $"VLPKUF paket: KUF računa {kufIds.Count}, " +
                    $"pročitano veza {batch.ReadCount}, upisano {inserted}, " +
                    $"neispravnih {batch.RejectedCount}.");
            }
        }

        LogAction?.Invoke(
            $"VLPKUF završen. Provereno KUF računa {totalKuf}, " +
            $"pročitano veza {totalRead}, upisano {totalInserted}, " +
            $"već postojećih {totalExisting}, neispravnih {totalRejected}.");
    }

    private async Task<List<long>> ReadTransferredKufIds(
        long? lastId, int batchSize, CancellationToken token)
    {
        var ids = new List<long>();
        await using var connection = new SqlConnection(connectionStrings.Transfer);
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = """
            SELECT TOP (@batchSize) ID
            FROM dbo.KUF
            WHERE @lastId IS NULL OR ID > @lastId
            ORDER BY ID;
            """;
        command.Parameters.Add("@batchSize", SqlDbType.Int).Value = batchSize;
        command.Parameters.Add("@lastId", SqlDbType.BigInt).Value = DbValue(lastId);

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            ids.Add(Convert.ToInt64(reader["ID"]));

        return ids;
    }

    private async Task<(List<VlpKuf> Rows, int ReadCount, int RejectedCount)>
        ReadVlpKufBatch(List<long> kufIds, CancellationToken token)
    {
        if (kufIds.Count == 0)
            return ([], 0, 0);

        var rows = new List<VlpKuf>();
        var readCount = 0;
        var rejectedCount = 0;
        await using var connection = new OracleConnection(BuildOracleConnectionString());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandTimeout = 120;
        var names = kufIds.Select((_, i) => $":id{i}").ToArray();
        command.CommandText = $"""
            SELECT VLPZAGLAVLJE, KUF, IZNOS, AUTOMATSKI
            FROM IIS.VLPKUF
            WHERE KUF IN ({string.Join(", ", names)})
            ORDER BY VLPZAGLAVLJE, KUF
            """;
        for (var i = 0; i < kufIds.Count; i++)
            command.Parameters.Add($"id{i}", OracleDbType.Int64).Value = kufIds[i];

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var kufId = ReadRequired<long>(reader, "KUF");
            var vlpId = ReadRequired<long>(reader, "VLPZAGLAVLJE");
            readCount++;
            try
            {
                var row = new VlpKuf
                {
                    VlpZaglavlje = vlpId,
                    KufId = kufId,
                    Iznos = ReadRequired<decimal>(reader, "IZNOS"),
                    Automatski = ReadRequired<string>(reader, "AUTOMATSKI")
                };
                ValidateVlpKuf(row);
                rows.Add(row);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or InvalidCastException or
                OverflowException or FormatException or ArgumentException)
            {
                rejectedCount++;
                ReportBadRecord(
                    "VLPKUF", $"VLPZAGLAVLJE={vlpId}; KUF={kufId}",
                    JsonSerializer.Serialize(new { VLPZAGLAVLJE = vlpId, KUF = kufId }),
                    $"Neispravan VLPKUF zapis: {exception.Message}", exception);
            }
        }
        return (rows, readCount, rejectedCount);
    }

    private async Task<int> InsertVlpKufPackage(List<VlpKuf> rows, CancellationToken token)
    {
        if (rows.Count == 0)
            return 0;

        await using var connection = new SqlConnection(connectionStrings.Transfer);
        await connection.OpenAsync(token);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var existing = await ReadExistingVlpKufKeys(connection, transaction, rows, token);
            var newRows = FilterExistingVlpKufRows(rows, existing);
            if (newRows.Count > 0)
            {
                using var table = CreateVlpKufDataTable(newRows);
                using var bulkCopy = new SqlBulkCopy(
                    connection,
                    SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls,
                    transaction);
                bulkCopy.DestinationTableName = "dbo.VLPKUF";
                bulkCopy.BatchSize = Math.Min(newRows.Count, 1000);
                bulkCopy.BulkCopyTimeout = 120;
                foreach (DataColumn column in table.Columns)
                    bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);

                await bulkCopy.WriteToServerAsync(table, token);
            }
            await transaction.CommitAsync(token);
            return newRows.Count;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                LogAction?.Invoke($"VLPKUF - greška pri poništavanju paketa: {rollbackException.Message}");
            }
            throw;
        }
    }

    private static async Task<HashSet<(long VlpZaglavlje, long KufId)>> ReadExistingVlpKufKeys(
        SqlConnection connection, SqlTransaction transaction,
        List<VlpKuf> rows, CancellationToken token)
    {
        var existing = new HashSet<(long VlpZaglavlje, long KufId)>();
        foreach (var chunk in rows.Select(row => row.KufId).Distinct().OrderBy(id => id).Chunk(500))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 120;
            var names = chunk.Select((_, i) => $"@id{i}").ToArray();
            command.CommandText = $"""
                SELECT VLPZAGLAVLJE, KUF
                FROM dbo.VLPKUF WITH (UPDLOCK, HOLDLOCK)
                WHERE KUF IN ({string.Join(", ", names)});
                """;
            for (var i = 0; i < chunk.Length; i++)
                command.Parameters.Add(names[i], SqlDbType.BigInt).Value = chunk[i];

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                existing.Add((
                    Convert.ToInt64(reader["VLPZAGLAVLJE"]),
                    Convert.ToInt64(reader["KUF"])));
            }
        }
        return existing;
    }

    private static List<VlpKuf> FilterExistingVlpKufRows(
        List<VlpKuf> rows, HashSet<(long VlpZaglavlje, long KufId)> existing)
    {
        return rows
            .Where(row => !existing.Contains((row.VlpZaglavlje, row.KufId)))
            .OrderBy(row => row.VlpZaglavlje)
            .ThenBy(row => row.KufId)
            .ToList();
    }

    private static void ValidateVlpKuf(VlpKuf row)
    {
        if (row.VlpZaglavlje < -9999999999L || row.VlpZaglavlje > 9999999999L ||
            row.KufId < -9999999999L || row.KufId > 9999999999L)
            throw new InvalidDataException("VLPZAGLAVLJE ili KUF ne staje u NUMERIC(10,0).");

        if (Math.Abs(row.Iznos) >= 100000000000000m || decimal.Round(row.Iznos, 2) != row.Iznos)
            throw new InvalidDataException($"IZNOS={row.Iznos} ne staje u NUMERIC(16,2) bez zaokruživanja.");

        if (row.Automatski is null || row.Automatski.Length != 1)
            throw new InvalidDataException("AUTOMATSKI mora sadržati tačno jedan znak.");
    }

    private static DataTable CreateVlpKufDataTable(List<VlpKuf> rows)
    {
        var table = new DataTable();
        table.Columns.Add("VLPZAGLAVLJE", typeof(decimal)).AllowDBNull = false;
        table.Columns.Add("KUF", typeof(decimal)).AllowDBNull = false;
        table.Columns.Add("IZNOS", typeof(decimal)).AllowDBNull = false;
        table.Columns.Add("AUTOMATSKI", typeof(string)).AllowDBNull = false;
        table.Columns["AUTOMATSKI"]!.MaxLength = 1;
        table.PrimaryKey = [table.Columns["VLPZAGLAVLJE"]!, table.Columns["KUF"]!];
        foreach (var row in rows)
            table.Rows.Add(row.VlpZaglavlje, row.KufId, row.Iznos, row.Automatski);
        return table;
    }


    private static void ValidateKuf(Kuf row)
    {
        if (Math.Abs(row.Odnos) >= 10000000000000000m ||
            decimal.Round(row.Odnos, 12) != row.Odnos)
            throw new InvalidDataException(
                $"ODNOS={row.Odnos} ne staje u DECIMAL(28,12) bez zaokruživanja.");
    }

    private static Kuf MapKuf(OracleDataReader reader)
    {
        return new Kuf
        {
            Id = ReadRequired<long>(reader, "ID"),
            Firma = ReadRequired<string>(reader, "FIRMA"),
            Dokument = ReadRequired<long>(reader, "DOKUMENT"),
            Godina = ReadRequired<long>(reader, "GODINA"),
            Broj = ReadRequired<long>(reader, "BROJ"),
            Storno = ReadRequired<string>(reader, "STORNO"),
            TipDobavljaca = ReadRequired<string>(reader, "TIPDOBAVLJACA"),
            KomitentTip = ReadRequired<string>(reader, "KOMITENTTIP"),
            Komitent = ReadRequired<long>(reader, "KOMITENT"),
            ZiroRacun = ReadRequired<long>(reader, "ZIRORACUN"),
            DatumDokumenta = ReadRequired<DateTime>(reader, "DATUMDOKUMENTA"),
            DatumValute = ReadRequired<DateTime>(reader, "DATUMVALUTE"),
            DatumPrijema = ReadRequired<DateTime>(reader, "DATUMPRIJEMA"),
            DatumRacuna = ReadRequired<DateTime>(reader, "DATUMRACUNA"),
            EksterniBroj = ReadRequired<string>(reader, "EKSTERNIBROJ"),
            PozivNaBrojMali = ReadNullableString(reader, "POZIVNABROJMALI"),
            PozivNaBroj = ReadRequired<string>(reader, "POZIVNABROJ"),
            Komentar = ReadNullableString(reader, "KOMENTAR"),
            Valuta = ReadRequired<string>(reader, "VALUTA"),
            Odnos = ReadRequired<decimal>(reader, "ODNOS"),
            Nalog = ReadNullable<long>(reader, "NALOG"),
            Korisnik = ReadRequired<string>(reader, "KORISNIK"),
            Vreme = ReadRequired<DateTime>(reader, "VREME"),
            Likvidiran = ReadRequired<string>(reader, "LIKVIDIRAN"),
            Likvidirao = ReadNullableString(reader, "LIKVIDIRAO"),
            VremeLikvidacije = ReadNullable<DateTime>(reader, "VREMELIKVIDACIJE"),
            TipUlaznogRacuna = ReadRequired<long>(reader, "TIPULAZNOGRACUNA"),
            Porez = ReadRequired<string>(reader, "POREZ"),
            DatumZaPorez = ReadRequired<DateTime>(reader, "DATUMZAPOREZ"),
            PoreskiObveznik = ReadRequired<string>(reader, "PORESKIOBVEZNIK"),
            DatumDpo = ReadRequired<DateTime>(reader, "DATUMDPO"),
            DatumKnjizenja = ReadRequired<DateTime>(reader, "DATUMKNJIZENJA"),
            BrojOtpremnice = ReadNullableString(reader, "BROJOTPREMNICE"),
            UkupanIznos = ReadRequired<decimal>(reader, "UKUPANIZNOS"),
        };
    }

    private static DataTable CreateKufDataTable(List<Kuf> rows)
    {
        var table = new DataTable();
        table.Columns.Add("ID", typeof(decimal));
        table.Columns.Add("FIRMA", typeof(string));
        table.Columns.Add("DOKUMENT", typeof(decimal));
        table.Columns.Add("GODINA", typeof(decimal));
        table.Columns.Add("BROJ", typeof(decimal));
        table.Columns.Add("STORNO", typeof(string));
        table.Columns.Add("TIPDOBAVLJACA", typeof(string));
        table.Columns.Add("KOMITENTTIP", typeof(string));
        table.Columns.Add("KOMITENT", typeof(decimal));
        table.Columns.Add("ZIRORACUN", typeof(decimal));
        table.Columns.Add("DATUMDOKUMENTA", typeof(DateTime));
        table.Columns.Add("DATUMVALUTE", typeof(DateTime));
        table.Columns.Add("DATUMPRIJEMA", typeof(DateTime));
        table.Columns.Add("DATUMRACUNA", typeof(DateTime));
        table.Columns.Add("EKSTERNIBROJ", typeof(string));
        table.Columns.Add("POZIVNABROJMALI", typeof(string));
        table.Columns.Add("POZIVNABROJ", typeof(string));
        table.Columns.Add("KOMENTAR", typeof(string));
        table.Columns.Add("VALUTA", typeof(string));
        table.Columns.Add("ODNOS", typeof(decimal));
        table.Columns.Add("NALOG", typeof(decimal));
        table.Columns.Add("KORISNIK", typeof(string));
        table.Columns.Add("VREME", typeof(DateTime));
        table.Columns.Add("LIKVIDIRAN", typeof(string));
        table.Columns.Add("LIKVIDIRAO", typeof(string));
        table.Columns.Add("VREMELIKVIDACIJE", typeof(DateTime));
        table.Columns.Add("TIPULAZNOGRACUNA", typeof(decimal));
        table.Columns.Add("POREZ", typeof(string));
        table.Columns.Add("DATUMZAPOREZ", typeof(DateTime));
        table.Columns.Add("PORESKIOBVEZNIK", typeof(string));
        table.Columns.Add("DATUMDPO", typeof(DateTime));
        table.Columns.Add("DATUMKNJIZENJA", typeof(DateTime));
        table.Columns.Add("BROJOTPREMNICE", typeof(string));
        table.Columns.Add("UKUPANIZNOS", typeof(decimal));
        table.Columns.Add("VLPZAGLAVLJE", typeof(decimal));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.Id,
                row.Firma,
                row.Dokument,
                row.Godina,
                row.Broj,
                row.Storno,
                row.TipDobavljaca,
                row.KomitentTip,
                row.Komitent,
                row.ZiroRacun,
                row.DatumDokumenta,
                row.DatumValute,
                row.DatumPrijema,
                row.DatumRacuna,
                row.EksterniBroj,
                DbValue(row.PozivNaBrojMali),
                row.PozivNaBroj,
                DbValue(row.Komentar),
                row.Valuta,
                row.Odnos,
                DbValue(row.Nalog),
                row.Korisnik,
                row.Vreme,
                row.Likvidiran,
                DbValue(row.Likvidirao),
                DbValue(row.VremeLikvidacije),
                row.TipUlaznogRacuna,
                row.Porez,
                row.DatumZaPorez,
                row.PoreskiObveznik,
                row.DatumDpo,
                row.DatumKnjizenja,
                DbValue(row.BrojOtpremnice),
                row.UkupanIznos,
                DbValue(row.VlpZaglavlje));
        }
        return table;
    }

    private static T ReadRequired<T>(OracleDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
            throw new InvalidDataException($"Obavezno polje {column} je NULL.");

        object value;
        if (typeof(T) == typeof(long))
            value = Convert.ToInt64(reader.GetValue(ordinal));
        else if (typeof(T) == typeof(decimal))
            value = reader.GetDecimal(ordinal);
        else if (typeof(T) == typeof(DateTime))
            value = reader.GetDateTime(ordinal);
        else if (typeof(T) == typeof(string))
            value = reader.GetString(ordinal);
        else
            throw new NotSupportedException($"Nepodržan tip: {typeof(T).Name}.");
        return (T)value;
    }

    private static T? ReadNullable<T>(OracleDataReader reader, string column) where T : struct
    {
        return reader.IsDBNull(reader.GetOrdinal(column)) ? null : ReadRequired<T>(reader, column);
    }

    private static string? ReadNullableString(OracleDataReader reader, string column)
    {
        return reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private void ReportBadRecord(
        string table, string key, string data, string message, Exception exception)
    {
        LogAction?.Invoke($"{table}, {key}: {message}");
        if (BadRecordAction is null)
            throw new InvalidOperationException(
                "KUF prenos nema podešen prijemnik neispravnih zapisa. " + message, exception);
        BadRecordAction(table, key, data, message, exception);
    }

    private string BuildOracleConnectionString()
    {
        return new OracleConnectionStringBuilder
        {
            UserID = connectionStrings.AswUser,
            Password = connectionStrings.AswPassword,
            DataSource = connectionStrings.AswDataSource
        }.ConnectionString;
    }
}

