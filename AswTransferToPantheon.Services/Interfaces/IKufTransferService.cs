namespace AswTransferToPantheon.Services.Interfaces;

public interface IKufTransferService
{
    Task TransferForNalozi(List<long> nalogIds, int batchSize, CancellationToken token);

    Action<string>? LogAction { get; set; }

    Action<string, string, string, string, Exception>?
    BadRecordAction { get; set; }
}
