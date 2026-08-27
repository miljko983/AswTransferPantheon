using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Interfaces;

public interface IDocumentCreationService_CL_WMS
{
    Action<string>? LogAction { get; set; }
    Action<CreatedDocumentInfo>? CreatedDocumentAction { get; set; }

    Action<BadRecordInfo>? CreationErrorAction { get; set; }

    Task Execute(
        string? orgJedLike,
        bool usePriceCalculation,
        int? maxDocuments,
        CancellationToken token);
}