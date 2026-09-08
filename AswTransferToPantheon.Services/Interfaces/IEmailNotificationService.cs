using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Interfaces;

public interface IEmailNotificationService
{
    Task SendTestEmail(CancellationToken token);

    Task SendBadRecordsSummaryEmail(string groupName, string taskName, List<BadRecordInfo> badRecords, CancellationToken token);

    Task SendTaskErrorEmail(string groupName, string taskName, string message, Exception exception, CancellationToken token, string notificationKey = "Task.Error");
    Task SendCreatedArticlesSummaryEmail(string groupName, string taskName, List<CreatedArticleInfo> articles, CancellationToken token);
    Task SendCreatedDocumentsSummaryEmail(string groupName, string taskName, List<CreatedDocumentInfo> documents, CancellationToken token);
    Task SendDocumentCreationErrorsSummaryEmail(string groupName, string taskName, List<BadRecordInfo> errors, CancellationToken token);

    Task SendCreatedIdentiCentrosinergijaSummaryEmail(string groupName, string taskName, List<CreatedIdentCentrosinergijaInfo> identi, CancellationToken token);
}