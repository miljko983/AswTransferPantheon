namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class Nalog
{
    public long Id { get; set; }

    public string? Firma { get; set; }

    public string? VrstaNaloga { get; set; }

    public long Godina { get; set; }

    public long Broj { get; set; }

    public DateTime Datum { get; set; }

    public string? Zatvoren { get; set; }

    public string? Korisnik { get; set; }

    public DateTime VremeKreiranja { get; set; }

    public string? Komentar { get; set; }
}