using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Interfaces;

public interface IVlpIzvSveTransferService
{
    Task Transfer(int batchSize, int daysBack, CancellationToken token);

    Action<string>? LogAction { get; set; }

    Action<string, string, string, string, Exception>? BadRecordAction { get; set; }
}