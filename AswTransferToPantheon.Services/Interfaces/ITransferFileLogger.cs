namespace AswTransferToPantheon.Services.Interfaces;

public interface ITransferFileLogger
{
    void Info(string groupName, string taskName, string message);

    void Error(string groupName, string taskName, string message, Exception exception);

    void BadRecord(string groupName, string taskName, string tableName, string key, string data, Exception exception);
}