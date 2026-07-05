namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class KifDatumValute
{
    public long Id { get; set; }

    public long IdKifa { get; set; }

    public DateTime DatumValute { get; set; }

    public decimal Iznos { get; set; }
}