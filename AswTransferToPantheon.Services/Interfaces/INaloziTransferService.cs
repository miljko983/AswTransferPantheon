namespace AswTransferToPantheon.Services.Interfaces;

public interface INaloziTransferService
{
    Task Transfer(int batchSize, DateTime datumOd, CancellationToken token);

    // Scheduler povezuje obradu KUF/VLPKUF sa uspešno upisanim nalozima.
    Func<List<long>, CancellationToken, Task>? KufTransferAction { get; set; }

    Action<string>? LogAction { get; set; }

    Action<string, string, string, string, Exception>? BadRecordAction { get; set; }
}
