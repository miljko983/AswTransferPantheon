namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class ArtikalMap
{
    public long Id { get; set; }

    public string KomitentTip { get; set; } = string.Empty;

    public long Komitent { get; set; }

    public long Artikal { get; set; }

    public string Varijanta { get; set; } = string.Empty;

    public string ArtikalKom { get; set; } = string.Empty;

    public string VarijantaKom { get; set; } = string.Empty;

    public string Naziv { get; set; } = string.Empty;

    public string JedinicaMere { get; set; } = string.Empty;

    public string Kategorija { get; set; } = string.Empty;

    public string? JmKom { get; set; }

    public decimal? KolicinaKom { get; set; }

    public decimal? KolicinaMpjm { get; set; }
}