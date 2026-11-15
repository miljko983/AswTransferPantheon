namespace AswTransferToPantheon.Infrastructure.Configuration;

public sealed class KifDocumentProcessingConfiguration
{
    public bool Enabled { get; set; } = false;

    public int UserId { get; set; } = 1;

    public bool SamoProvera { get; set; } = false;

    public bool PrikaziNapredak { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 5000;
}