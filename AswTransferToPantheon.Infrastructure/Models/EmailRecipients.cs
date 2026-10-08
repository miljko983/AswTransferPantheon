namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class EmailRecipients
{
    public List<string> To { get; init; } = [];

    public List<string> Cc { get; init; } = [];
}