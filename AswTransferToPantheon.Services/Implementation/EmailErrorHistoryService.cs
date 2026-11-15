using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AswTransferToPantheon.Services.Implementation;

// Zajednicka evidencija. Pozivalac bira za koja obavestenja se koristi.
public sealed class EmailErrorHistoryService
{
    private readonly IEmailErrorHistoryStore store;

    public EmailErrorHistoryService(IOptions<ConnectionStrings> connectionStrings)
        : this(new SqlEmailErrorHistoryStore(connectionStrings.Value.Transfer))
    {
    }

    internal EmailErrorHistoryService(IEmailErrorHistoryStore store)
    {
        this.store = store;
    }

    // Citanje ne upisuje istoriju i ne menja originalnu listu za log.
    public async Task<List<BadRecordInfo>> GetRecordsToSendAsync(
        string applicationName,
        string taskName,
        string notificationKey,
        IReadOnlyList<BadRecordInfo> records,
        DateTime notificationDate,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scope = CreateScope(applicationName, taskName, notificationKey);
        var entries = CreateEntries(records);
        var tracked = UniqueTracked(entries);

        var firstSentDates = tracked.Count == 0
            ? new Dictionary<string, DateTime>(StringComparer.Ordinal)
            : await store.ReadFirstSentDatesAsync(scope, tracked, token);

        token.ThrowIfCancellationRequested();
        var date = notificationDate.Date;

        return entries
            .Where(entry =>
                !entry.CanTrack
                || !firstSentDates.TryGetValue(entry.Hash, out var firstDate)
                || firstDate.Date >= date)
            .Select(entry => entry.Record)
            .ToList();
    }

    // Pozvati ISKLJUCIVO kada je SMTP prihvatio mail.
    // sentAt.Date je lokalni kalendarski datum stvarnog uspesnog slanja.
    public async Task RecordSuccessfulSendAsync(
        string applicationName,
        string taskName,
        string notificationKey,
        IReadOnlyList<BadRecordInfo> sentRecords,
        DateTimeOffset sentAt,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scope = CreateScope(applicationName, taskName, notificationKey);
        var entries = UniqueTracked(CreateEntries(sentRecords));

        if (entries.Count == 0)
            return;

        await store.RecordSentAsync(scope, entries, sentAt, token);
    }

    private static EmailErrorHistoryScope CreateScope(
        string applicationName, string taskName, string notificationKey)
    {
        static string Required(string value, int maxLength, string name)
        {
            var text = value?.Trim() ?? "";
            if (text.Length == 0 || text.Length > maxLength)
                throw new ArgumentException(
                    $"Polje {name} mora imati od 1 do {maxLength} znakova.", name);
            return text;
        }

        return new EmailErrorHistoryScope(
            Required(applicationName, 100, nameof(applicationName)),
            Required(taskName, 100, nameof(taskName)),
            Required(notificationKey, 200, nameof(notificationKey)));
    }

    private static List<EmailErrorHistoryEntry> CreateEntries(
        IReadOnlyList<BadRecordInfo> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var entries = new List<EmailErrorHistoryEntry>(records.Count);

        static string Normalize(string? value) =>
            (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);

            var snapshot = new BadRecordInfo
            {
                TableName = record.TableName ?? "",
                Key = record.Key ?? "",
                Message = record.Message ?? "",
                Exception = record.Exception ?? "",
                Data = record.Data ?? ""
            };

            var fields = new[]
            {
                Normalize(snapshot.TableName),
                Normalize(snapshot.Key),
                Normalize(snapshot.Message),
                Normalize(snapshot.Exception)
            };

            if (fields[0].Length > 256)
                throw new ArgumentException(
                    "Naziv tabele za evidenciju mailova ne sme biti duzi od 256 znakova.");

            // JSON niz cuva granice polja; Data i vreme prolaza nisu deo identiteta.
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields));
            var hash = Convert.ToHexString(SHA256.HashData(bytes));

            // Bez tabele/kljuca nije pouzdano utvrditi da je u pitanju isti red.
            var canTrack = fields[0].Length > 0
                && fields[1].Length > 0
                && (fields[2].Length > 0 || fields[3].Length > 0);

            entries.Add(new EmailErrorHistoryEntry(
                hash, fields[0], fields[1], fields[2], fields[3], canTrack, snapshot));
        }

        return entries;
    }

    private static List<EmailErrorHistoryEntry> UniqueTracked(
        IEnumerable<EmailErrorHistoryEntry> entries) =>
        entries.Where(entry => entry.CanTrack)
            .DistinctBy(entry => entry.Hash, StringComparer.Ordinal)
            .ToList();
}

internal sealed record EmailErrorHistoryScope(
    string ApplicationName, string TaskName, string NotificationKey);

internal sealed record EmailErrorHistoryEntry(
    string Hash,
    string TableName,
    string RecordKey,
    string ErrorMessage,
    string ExceptionText,
    bool CanTrack,
    BadRecordInfo Record);

internal interface IEmailErrorHistoryStore
{
    Task<Dictionary<string, DateTime>> ReadFirstSentDatesAsync(
        EmailErrorHistoryScope scope,
        IReadOnlyList<EmailErrorHistoryEntry> entries,
        CancellationToken token);

    Task RecordSentAsync(
        EmailErrorHistoryScope scope,
        IReadOnlyList<EmailErrorHistoryEntry> entries,
        DateTimeOffset sentAt,
        CancellationToken token);
}

internal sealed class SqlEmailErrorHistoryStore : IEmailErrorHistoryStore
{
    private const int CommandTimeoutSeconds = 10;
    private readonly string connectionString;

    public SqlEmailErrorHistoryStore(string connectionString)
    {
        this.connectionString = connectionString;
    }

    private SqlConnection CreateConnection()
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Enlist = false,
            ConnectTimeout = 5
        };

        return new SqlConnection(builder.ConnectionString);
    }

    public async Task<Dictionary<string, DateTime>> ReadFirstSentDatesAsync(
        EmailErrorHistoryScope scope,
        IReadOnlyList<EmailErrorHistoryEntry> entries,
        CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await LoadEntriesAsync(connection, entries, token);

        using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        AddScope(command, scope);
        command.CommandText = """
            SELECT h.ErrorHash, h.FirstSentDate
            FROM dbo._tb_EmailErrorHistory AS h
            INNER JOIN #EmailErrors AS e ON e.ErrorHash = h.ErrorHash
            WHERE h.ApplicationName = @ApplicationName
              AND h.TaskName = @TaskName
              AND h.NotificationKey = @NotificationKey;
            """;

        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(
                Convert.ToHexString((byte[])reader.GetValue(0)),
                reader.GetDateTime(1).Date);
        }

        return result;
    }

    public async Task RecordSentAsync(
        EmailErrorHistoryScope scope,
        IReadOnlyList<EmailErrorHistoryEntry> entries,
        DateTimeOffset sentAt,
        CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await LoadEntriesAsync(connection, entries, token);

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(token);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        AddScope(command, scope);
        command.Parameters.Add("@SentDate", SqlDbType.Date).Value = sentAt.Date;
        command.Parameters.Add("@SentAtUtc", SqlDbType.DateTime2).Value = sentAt.UtcDateTime;
        command.CommandText = """
            UPDATE h WITH (UPDLOCK, HOLDLOCK)
            SET FirstSentDate =
                    CASE WHEN @SentDate < h.FirstSentDate
                         THEN @SentDate ELSE h.FirstSentDate END,
                FirstSentAtUtc =
                    CASE WHEN @SentAtUtc < h.FirstSentAtUtc
                         THEN @SentAtUtc ELSE h.FirstSentAtUtc END,
                LastSentAtUtc =
                    CASE WHEN @SentAtUtc > h.LastSentAtUtc
                         THEN @SentAtUtc ELSE h.LastSentAtUtc END,
                SentCount = h.SentCount + 1
            FROM dbo._tb_EmailErrorHistory AS h
            INNER JOIN #EmailErrors AS e ON e.ErrorHash = h.ErrorHash
            WHERE h.ApplicationName = @ApplicationName
              AND h.TaskName = @TaskName
              AND h.NotificationKey = @NotificationKey;

            INSERT INTO dbo._tb_EmailErrorHistory
            (
                ApplicationName, TaskName, NotificationKey, ErrorHash,
                TableName, RecordKey, ErrorMessage, ExceptionText,
                FirstSentDate, FirstSentAtUtc, LastSentAtUtc, SentCount
            )
            SELECT
                @ApplicationName, @TaskName, @NotificationKey, e.ErrorHash,
                e.TableName, e.RecordKey, e.ErrorMessage, e.ExceptionText,
                @SentDate, @SentAtUtc, @SentAtUtc, 1
            FROM #EmailErrors AS e
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM dbo._tb_EmailErrorHistory AS existing WITH (UPDLOCK, HOLDLOCK)
                WHERE existing.ApplicationName = @ApplicationName
                  AND existing.TaskName = @TaskName
                  AND existing.NotificationKey = @NotificationKey
                  AND existing.ErrorHash = e.ErrorHash
            );
            """;

        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    private static void AddScope(SqlCommand command, EmailErrorHistoryScope scope)
    {
        command.Parameters.Add("@ApplicationName", SqlDbType.NVarChar, 100)
            .Value = scope.ApplicationName;
        command.Parameters.Add("@TaskName", SqlDbType.NVarChar, 100)
            .Value = scope.TaskName;
        command.Parameters.Add("@NotificationKey", SqlDbType.NVarChar, 200)
            .Value = scope.NotificationKey;
    }

    private static async Task LoadEntriesAsync(
        SqlConnection connection,
        IReadOnlyList<EmailErrorHistoryEntry> entries,
        CancellationToken token)
    {
        using (var createCommand = connection.CreateCommand())
        {
            createCommand.CommandTimeout = CommandTimeoutSeconds;
            createCommand.CommandText = """
                CREATE TABLE #EmailErrors
                (
                    ErrorHash binary(32) NOT NULL PRIMARY KEY,
                    TableName nvarchar(256) NOT NULL,
                    RecordKey nvarchar(max) NOT NULL,
                    ErrorMessage nvarchar(max) NOT NULL,
                    ExceptionText nvarchar(max) NOT NULL
                );
                """;
            await createCommand.ExecuteNonQueryAsync(token);
        }

        using var table = new DataTable();
        table.Columns.Add("ErrorHash", typeof(byte[]));
        table.Columns.Add("TableName", typeof(string));
        table.Columns.Add("RecordKey", typeof(string));
        table.Columns.Add("ErrorMessage", typeof(string));
        table.Columns.Add("ExceptionText", typeof(string));

        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            table.Rows.Add(
                Convert.FromHexString(entry.Hash), entry.TableName, entry.RecordKey,
                entry.ErrorMessage, entry.ExceptionText);
        }

        using var copy = new SqlBulkCopy(connection)
        {
            DestinationTableName = "#EmailErrors",
            BatchSize = 1000,
            BulkCopyTimeout = CommandTimeoutSeconds
        };
        foreach (DataColumn column in table.Columns)
            copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);

        await copy.WriteToServerAsync(table, token);
    }
}

