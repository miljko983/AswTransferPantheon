namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class ArtikalDobavljac
{
    public long Id { get; set; }

    public long IdArtikla { get; set; }

    public string? KomitentTip { get; set; }

    public long Komitent { get; set; }

    public string? Regija { get; set; }

    public int Prioritet { get; set; }

    public DateTime VremeOd { get; set; }

    public DateTime VremeDo { get; set; }
}