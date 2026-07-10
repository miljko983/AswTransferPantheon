using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;
using System.Text;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class TransferFileLogger : ITransferFileLogger
{
    private static readonly object FileLock = new();

    private readonly LoggingConfiguration configuration;

    public TransferFileLogger(IOptions<LoggingConfiguration> configuration)
    {
        this.configuration = configuration.Value;
    }

    public void Info(string groupName, string taskName, string message)
    {
        if (!configuration.EnableFileLog)
        {
            return;
        }

        Write(groupName, taskName, "INFO", message, null);
    }

    public void Error(string groupName, string taskName, string message, Exception exception)
    {
        if (!configuration.EnableFileLog)
        {
            return;
        }

        Write(groupName, taskName, "ERROR", message, exception);
        Write("Errors", "AllErrors", "ERROR", $"{groupName}/{taskName}: {message}", exception);
    }

    public void BadRecord(
        string groupName,
        string taskName,
        string tableName,
        string key,
        string data,
        Exception exception)
    {
        if (!configuration.EnableBadRecordLog)
        {
            return;
        }

        try
        {
            var safeTableName = SanitizePathPart(tableName);
            var date = DateTime.Now.ToString("yyyy-MM-dd");

            var directory = Path.Combine(configuration.RootPath, "BadRecords", safeTableName);
            Directory.CreateDirectory(directory);

            var filePath = Path.Combine(directory, $"{date}.log");

            var builder = new StringBuilder();

            builder.AppendLine("--------------------------------------------------");
            builder.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            builder.AppendLine($"Group: {groupName}");
            builder.AppendLine($"Task: {taskName}");
            builder.AppendLine($"Table: {tableName}");
            builder.AppendLine($"Key: {key}");
            builder.AppendLine($"Error: {exception.Message}");
            builder.AppendLine("Data:");
            builder.AppendLine(data);
            builder.AppendLine("Exception:");
            builder.AppendLine(exception.ToString());

            lock (FileLock)
            {
                File.AppendAllText(filePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logger ne sme da sruši transfer.
        }
    }

    private void Write(string groupName, string taskName, string level, string message, Exception? exception)
    {
        try
        {
            var safeGroupName = SanitizePathPart(groupName);
            var safeTaskName = SanitizePathPart(taskName);

            var date = DateTime.Now.ToString("yyyy-MM-dd");
            var directory = Path.Combine(configuration.RootPath, safeGroupName, safeTaskName);

            Directory.CreateDirectory(directory);

            var filePath = Path.Combine(directory, $"{date}.log");

            var builder = new StringBuilder();

            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            builder.Append(" [");
            builder.Append(level);
            builder.Append("] ");
            builder.Append(message);
            builder.AppendLine();

            if (exception is not null)
            {
                builder.AppendLine("Exception:");
                builder.AppendLine(exception.ToString());
            }

            lock (FileLock)
            {
                File.AppendAllText(filePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logger nikad ne sme da obori transfer.
        }
    }

    private static string SanitizePathPart(string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }

        return value;
    }
}