namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class Cenovnik
{
    public string? Skladiste { get; set; }

    public long Artikal { get; set; }

    public string? TipCene { get; set; }

    public DateTime VremeOd { get; set; }

    public DateTime VremeDo { get; set; }

    public decimal? Cena { get; set; }
}