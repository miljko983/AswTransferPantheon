namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class KifStavka
{
    public long IdKifa { get; set; }

    public long BrojStavke { get; set; }

    public long Artikal { get; set; }

    public string? Opis { get; set; }

    public decimal? Kolicina { get; set; }

    public decimal? Cena { get; set; }

    public decimal? Popust { get; set; }

    public decimal? Nabavna { get; set; }

    public decimal? Iznos { get; set; }

    public decimal? IznosSaPorezom { get; set; }

    public decimal? IznosBezPopusta { get; set; }

    public string? TarifnaGrupa { get; set; }

    public string? Taksa { get; set; }

    public decimal? TaksaIznos { get; set; }

    public decimal? IznosSaPopustom { get; set; }

    public string? OrgJed { get; set; }

    public long? EfPdvIzuzeceVrsta { get; set; }
}