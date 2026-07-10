namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class BadRecordInfo
{
    public string TableName { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;

    public string Exception { get; set; } = string.Empty;
}