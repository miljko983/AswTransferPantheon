namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class Kuf
{
    public long Id { get; set; }
    public string Firma { get; set; } = string.Empty;
    public long Dokument { get; set; }
    public long Godina { get; set; }
    public long Broj { get; set; }
    public string Storno { get; set; } = string.Empty;
    public string TipDobavljaca { get; set; } = string.Empty;
    public string KomitentTip { get; set; } = string.Empty;
    public long Komitent { get; set; }
    public long ZiroRacun { get; set; }

    public DateTime DatumDokumenta { get; set; }
    public DateTime DatumValute { get; set; }
    public DateTime DatumPrijema { get; set; }
    public DateTime DatumRacuna { get; set; }

    public string EksterniBroj { get; set; } = string.Empty;
    public string? PozivNaBrojMali { get; set; }
    public string PozivNaBroj { get; set; } = string.Empty;
    public string? Komentar { get; set; }
    public string Valuta { get; set; } = string.Empty;
    public decimal Odnos { get; set; }
    public long? Nalog { get; set; }
    public string Korisnik { get; set; } = string.Empty;
    public DateTime Vreme { get; set; }
    public string Likvidiran { get; set; } = string.Empty;
    public string? Likvidirao { get; set; }
    public DateTime? VremeLikvidacije { get; set; }

    public long TipUlaznogRacuna { get; set; }
    public string Porez { get; set; } = string.Empty;
    public DateTime DatumZaPorez { get; set; }
    public string PoreskiObveznik { get; set; } = string.Empty;
    public DateTime DatumDpo { get; set; }
    public DateTime DatumKnjizenja { get; set; }
    public string? BrojOtpremnice { get; set; }
    public decimal UkupanIznos { get; set; }

    public long? VlpZaglavlje { get; set; }
}