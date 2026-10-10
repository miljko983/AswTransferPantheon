namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class VlpKuf
{
    public long VlpZaglavlje { get; set; }
    public long KufId { get; set; }
    public decimal Iznos { get; set; }
    public string Automatski { get; set; } = string.Empty;
}
