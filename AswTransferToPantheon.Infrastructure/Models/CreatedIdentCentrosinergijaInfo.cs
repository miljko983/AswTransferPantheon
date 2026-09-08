namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class CreatedIdentCentrosinergijaInfo
{
    public long SourceID { get; set; }

    public string AcIdent { get; set; } = string.Empty;

    public string AcName { get; set; } = string.Empty;

    public string AcClassif { get; set; } = string.Empty;

    public string AcClassif2 { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}