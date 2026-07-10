using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Interfaces;

public interface IEmailNotificationService
{
    Task SendTestEmail(CancellationToken token);

    Task SendBadRecordsSummaryEmail(string groupName, string taskName, List<BadRecordInfo> badRecords, CancellationToken token);

    Task SendTaskErrorEmail(string groupName, string taskName, string message, Exception exception, CancellationToken token);
}