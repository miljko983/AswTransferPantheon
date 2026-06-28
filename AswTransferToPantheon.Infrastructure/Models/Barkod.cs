namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class Barkod
{
    public long Id { get; set; }

    public string? BarkodVrednost { get; set; }

    public long IdArtikla { get; set; }

    public string? Varijanta { get; set; }

    public string? JedinicaMere { get; set; }

    public int RedniBroj { get; set; }
}