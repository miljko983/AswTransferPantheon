namespace AswTransferToPantheon.Infrastructure.Configuration;

public sealed class TransferEmailConfiguration
{
    public bool Enabled { get; set; } = true;

    public List<string> To { get; set; } = [];

    public List<string> Cc { get; set; } = [];

    public string Subject { get; set; } = string.Empty;

    public string BodyTemplate { get; set; } = string.Empty;
}