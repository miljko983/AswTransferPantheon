namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class VlpIzvSveZaglavlje
{
    public int Id { get; set; }
    public string OrgJed { get; set; } = string.Empty;
    public int Dokument { get; set; }
    public int Godina { get; set; }
    public int Broj { get; set; }
    public string Storno { get; set; } = string.Empty;
    public string Skladiste { get; set; } = string.Empty;
    public string? KomitentTip { get; set; }
    public int? Komitent { get; set; }
    public DateTime Datum { get; set; }
    public DateTime Vreme { get; set; }
    public DateTime VremeKreiranja { get; set; }
    public DateTime? DatumValute { get; set; }
    public DateTime? DatumDpo { get; set; }
    public string? EksterniBroj { get; set; }
    public string Porez { get; set; } = string.Empty;
    public string Korisnik { get; set; } = string.Empty;
    public string? Komentar { get; set; }
    public decimal Rabat { get; set; }
    public int VlpZaglavlje { get; set; }
    public int? Kif { get; set; }
    public int? Nalog { get; set; }
    public string Potvrdjen { get; set; } = string.Empty;
    public string? Potvrdio { get; set; }
    public DateTime? VremePotvrde { get; set; }
    public string? BrojIzjave { get; set; }
    public DateTime? DatumIzjave { get; set; }
    public string? TipDobavljaca { get; set; }
    public string? TipKupca { get; set; }
    public string Zavrsen { get; set; } = string.Empty;
    public string? Prodavac { get; set; }
    public string ProdajnaCena { get; set; } = string.Empty;
    public string? Komisionar { get; set; }
    public string Taksa { get; set; } = string.Empty;
    public string Valuta { get; set; } = string.Empty;
    public decimal Kurs { get; set; }
    public string? NadredjeniTip { get; set; }
    public int? NadredjeniKomitent { get; set; }
    public int? Artikal { get; set; }
    public decimal? DodatniTrosak { get; set; }
    public decimal? PorezNaTrosak { get; set; }
    public int? AvansBroj { get; set; }
    public decimal? AvansIznos { get; set; }
    public string? PoreskaKlauzula { get; set; }
    public string? NacinIsporuke { get; set; }
    public string CeneZaKalkulaciju { get; set; } = string.Empty;
    public int? BrojPaketa { get; set; }
    public DateTime? DatumRacuna { get; set; }
    public string? BrojRacuna { get; set; }
    public string Storniran { get; set; } = string.Empty;
}
