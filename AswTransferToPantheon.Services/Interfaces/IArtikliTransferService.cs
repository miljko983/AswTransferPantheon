using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Interfaces;

public interface IArtikliTransferService
{
    Action<string> LogAction { get; set; }

    Action<string, string, string, string, Exception>?
        BadRecordAction
    { get; set; }

    Action<CreatedArticleInfo>?
        CreatedArticleAction
    { get; set; }

    Task TransferArtikliPaket(
        int batchSize,
        CancellationToken token);
}