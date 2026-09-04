namespace AswTransferToPantheon.Services.Interfaces;

public interface INaloziTransferService
{
    Task Transfer(int batchSize, DateTime datumOd, CancellationToken token);

    Action<string>? LogAction { get; set; }

    Action<string, string, string, string, Exception>? BadRecordAction { get; set; }
}