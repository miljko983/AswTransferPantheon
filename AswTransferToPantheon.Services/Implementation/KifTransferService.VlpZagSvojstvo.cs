using AswTransferToPantheon.Services.Helpers;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using System.Globalization;

namespace AswTransferToPantheon.Services.Implementation;

public partial class KifTransferService
{
    private async Task TransferVlpZagSvojstvo(int batchSize, CancellationToken token)
    {
        LogAction?.Invoke("VLPZAGSVOJSTVO - početak prenosa za zaglavlja u Transferu.");
        long inserted = 0, updated = 0, unchanged = 0, invalid = 0, failedBatches = 0;
        decimal? lastId = null;
        var take = Math.Min(batchSize, 500);

        await using var sql = new SqlConnection(connectionStrings.Transfer);
        await sql.OpenAsync(token);

        // Gornja granica stabilizuje prolaz ako se zaglavlja dodaju tokom rada.
        using var maxCommand = sql.CreateCommand();
        maxCommand.CommandTimeout = 120;
        maxCommand.CommandText = "SELECT MAX(ID) FROM dbo.VLPZAGLAVLJA;";
        var maxValue = await maxCommand.ExecuteScalarAsync(token);
        if (maxValue is null or DBNull)
        {
            LogAction?.Invoke("VLPZAGSVOJSTVO: nema prenetih VLP zaglavlja.");
            return;
        }
        var maxId = Convert.ToDecimal(maxValue, CultureInfo.InvariantCulture);

        while (true)
        {
            token.ThrowIfCancellationRequested();
            var ids = new List<decimal>();
            using (var command = sql.CreateCommand())
            {
                command.CommandTimeout = 120;
                command.CommandText = """
                    SELECT DISTINCT TOP (@Take) ID
                    FROM dbo.VLPZAGLAVLJA
                    WHERE (@Last IS NULL OR ID > @Last)
                    AND ID <= @Max
                    AND DATUM >= CONVERT(datetime, '20261101', 112)
                    ORDER BY ID;
                    """;
                command.Parameters.Add("@Take", SqlDbType.Int).Value = take;
                var last = command.Parameters.Add("@Last", SqlDbType.Decimal);
                last.Precision = 38;
                last.Scale = 0;
                last.Value = lastId.HasValue ? (object)lastId.Value : DBNull.Value;
                var max = command.Parameters.Add("@Max", SqlDbType.Decimal);
                max.Precision = 38;
                max.Scale = 0;
                max.Value = maxId;
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    ids.Add(Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
            if (ids.Count == 0) break;
            lastId = ids[^1];

            var validIds = new List<int>();
            foreach (var id in ids)
            {
                if (id != decimal.Truncate(id) || id < int.MinValue || id > int.MaxValue)
                {
                    invalid++;
                    ReportVlpSvojstvoProblem($"VLPZAGLAVLJE={id}", "",
                        new InvalidOperationException("ID zaglavlja ne staje u INT; svojstva nisu preneta."));
                }
                else validIds.Add((int)id);
            }
            if (validIds.Count == 0) continue;

            try
            {
                using var rows = new DataTable();
                rows.Columns.Add("VLPZAGLAVLJE", typeof(int));
                rows.Columns.Add("SVOJSTVO", typeof(long));
                rows.Columns.Add("VREDNOST", typeof(string));
                rows.PrimaryKey = [rows.Columns[0], rows.Columns[1]];

                await using (var oracle = new OracleConnection(BuildOracleConnectionString()))
                {
                    await oracle.OpenAsync(token);
                    using var command = oracle.CreateCommand();
                    command.BindByName = true;
                    command.CommandTimeout = 120;
                    command.CommandText = $"""
                        SELECT VLPZAGLAVLJE, SVOJSTVO, VREDNOST
                        FROM IIS.VLPZAGSVOJSTVO
                        WHERE VLPZAGLAVLJE IN ({string.Join(",", validIds.Select((_, i) => $":p{i}"))})
                        ORDER BY VLPZAGLAVLJE, SVOJSTVO
                        """;
                    for (var i = 0; i < validIds.Count; i++)
                        command.Parameters.Add($"p{i}", OracleDbType.Int32).Value = validIds[i];

                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        token.ThrowIfCancellationRequested();
                        var key = $"VLPZAGLAVLJE={reader.GetValue(0)}; SVOJSTVO={reader.GetValue(1)}";
                        try
                        {
                            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                                throw new InvalidOperationException("Obavezno polje ima NULL vrednost.");
                            var header = reader.GetDecimal(0);
                            var property = reader.GetDecimal(1);
                            var value = reader.GetString(2);
                            if (header != decimal.Truncate(header) || header < int.MinValue || header > int.MaxValue)
                                throw new InvalidOperationException("VLPZAGLAVLJE ne staje u INT.");
                            if (property != decimal.Truncate(property) || property < -9999999999m || property > 9999999999m)
                                throw new InvalidOperationException("SVOJSTVO nije NUMBER(10,0).");
                            if (value.Length > 100)
                                throw new InvalidOperationException("VREDNOST je duža od 100 znakova.");
                            rows.Rows.Add((int)header, (long)property, value);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or OverflowException or ConstraintException or InvalidCastException)
                        {
                            invalid++;
                            ReportVlpSvojstvoProblem(key, "", ex);
                        }
                    }
                }
                if (rows.Rows.Count == 0) continue;
                var result = await SaveVlpZagSvojstvo(sql, rows, token);
                inserted += result.Inserted;
                updated += result.Updated;
                unchanged += rows.Rows.Count - result.Inserted - result.Updated;
                LogAction?.Invoke($"VLPZAGSVOJSTVO paket do ID={lastId}: novih {result.Inserted}, " +
                    $"osveženih {result.Updated}, nepromenjenih {rows.Rows.Count - result.Inserted - result.Updated}.");
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            catch (Exception ex)
            {
                if (TransferErrorHelper.IsCriticalError(ex)) throw;
                failedBatches++;
                ReportVlpSvojstvoProblem($"Paket ID={ids[0]}..{ids[^1]}",
                    string.Join(",", validIds), ex);
                // Neuspešan paket ponovo se čita u sledećem zakazanom prolazu.
                if (sql.State != ConnectionState.Open) throw;
            }
        }
        LogAction?.Invoke($"VLPZAGSVOJSTVO - završen prolaz: novih {inserted}, osveženih {updated}, " +
            $"nepromenjenih {unchanged}, neispravnih {invalid}, neuspešnih paketa {failedBatches}." +
            (invalid > 0 || failedBatches > 0 ? " Prenos nije kompletan; proverite prijavljene greške." : ""));
    }

    private void ReportVlpSvojstvoProblem(string key, string data, Exception exception)
    {
        LogAction?.Invoke($"VLPZAGSVOJSTVO: {key}. {exception.Message}");
        BadRecordAction?.Invoke("VLPZAGSVOJSTVO", key, data, exception.Message, exception);
    }

    private static async Task<(int Inserted, int Updated)> SaveVlpZagSvojstvo(
        SqlConnection connection, DataTable rows, CancellationToken token)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            using (var create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandTimeout = 120;
                create.CommandText = """
                    CREATE TABLE #VlpSvojstvo
                    (
                        VLPZAGLAVLJE int NOT NULL,
                        SVOJSTVO bigint NOT NULL,
                        VREDNOST nvarchar(100) NOT NULL,
                        PRIMARY KEY (VLPZAGLAVLJE, SVOJSTVO)
                    );
                    """;
                await create.ExecuteNonQueryAsync(token);
            }
            using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction))
            {
                bulk.DestinationTableName = "#VlpSvojstvo";
                bulk.BatchSize = 1000;
                bulk.BulkCopyTimeout = 120;
                foreach (DataColumn column in rows.Columns)
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                await bulk.WriteToServerAsync(rows, token);
            }
            using var save = connection.CreateCommand();
            save.Transaction = transaction;
            save.CommandTimeout = 120;
            save.CommandText = """
                UPDATE t WITH (UPDLOCK, HOLDLOCK)
                SET VREDNOST = s.VREDNOST
                FROM dbo.VLPZAGSVOJSTVO t
                JOIN #VlpSvojstvo s
                  ON s.VLPZAGLAVLJE = t.VLPZAGLAVLJE AND s.SVOJSTVO = t.SVOJSTVO
                WHERE CONVERT(varbinary(200), t.VREDNOST) <> CONVERT(varbinary(200), s.VREDNOST);
                DECLARE @Updated int = @@ROWCOUNT;

                INSERT INTO dbo.VLPZAGSVOJSTVO (VLPZAGLAVLJE, SVOJSTVO, VREDNOST)
                SELECT s.VLPZAGLAVLJE, s.SVOJSTVO, s.VREDNOST
                FROM #VlpSvojstvo s
                WHERE NOT EXISTS
                (
                    SELECT 1 FROM dbo.VLPZAGSVOJSTVO t WITH (UPDLOCK, HOLDLOCK)
                    WHERE t.VLPZAGLAVLJE = s.VLPZAGLAVLJE AND t.SVOJSTVO = s.SVOJSTVO
                );
                DECLARE @Inserted int = @@ROWCOUNT;
                DROP TABLE #VlpSvojstvo;
                SELECT @Inserted, @Updated;
                """;
            int inserted, updated;
            await using (var reader = await save.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token))
                    throw new InvalidOperationException("Upis svojstava nije vratio rezultat.");
                inserted = reader.GetInt32(0);
                updated = reader.GetInt32(1);
            }
            await transaction.CommitAsync(token);
            return (inserted, updated);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* Sačuvaj izvornu grešku. */ }
            throw;
        }
    }
}
