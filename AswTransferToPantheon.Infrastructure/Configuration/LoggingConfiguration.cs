namespace AswTransferToPantheon.Infrastructure.Configuration;

public sealed class LoggingConfiguration
{
    public string RootPath { get; set; } = @"D:\TransferLog";

    public bool EnableFileLog { get; set; } = true;

    public bool EnableBadRecordLog { get; set; } = true;
}