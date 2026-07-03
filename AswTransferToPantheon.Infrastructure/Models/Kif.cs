namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class Kif
{
    public long Id { get; set; }

    public string? Firma { get; set; }

    public long? Dokument { get; set; }

    public long? Godina { get; set; }

    public long? Broj { get; set; }

    public string? Storno { get; set; }

    public string? KomitentTip { get; set; }

    public long? Komitent { get; set; }

    public DateTime? DatumDokumenta { get; set; }

    public DateTime? DatumValute { get; set; }

    public DateTime? DatumRacuna { get; set; }

    public string? Valuta { get; set; }

    public decimal? Odnos { get; set; }

    public decimal? Popust { get; set; }

    public string? Porez { get; set; }

    public string? TipKupca { get; set; }

    public string? TipIzlaznogRacuna { get; set; }

    public string? Korisnik { get; set; }

    public DateTime? VremeKreiranja { get; set; }

    public string? Likvidiran { get; set; }

    public string? Likvidirao { get; set; }

    public string? BrojIzjave { get; set; }

    public DateTime? DatumIzjave { get; set; }

    public long? Nalog { get; set; }

    public string? EksterniBroj { get; set; }

    public long? NacinPlacanja { get; set; }

    public DateTime? DatumZaPorez { get; set; }

    public string? Taksa { get; set; }

    public DateTime? DatumPrometaDobaraOd { get; set; }

    public DateTime? DatumPrometaDobaraDo { get; set; }

    public string? PoreskaKlauzula { get; set; }

    public string? PozivNaBrojMali { get; set; }

    public string? PozivNaBroj { get; set; }

    public string? PoreskiObveznik { get; set; }

    public long? AvansBroj { get; set; }

    public decimal? AvansIznos { get; set; }

    public string? IncotermsKlauzula { get; set; }
}