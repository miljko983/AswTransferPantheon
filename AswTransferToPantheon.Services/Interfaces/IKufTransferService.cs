namespace AswTransferToPantheon.Services.Interfaces;

public interface IKufTransferService
{
    Task Transfer(int batchSize, CancellationToken token);

    Action<string>? LogAction { get; set; }

    Action<string, string, string, string, Exception>?
    BadRecordAction { get; set; }
}