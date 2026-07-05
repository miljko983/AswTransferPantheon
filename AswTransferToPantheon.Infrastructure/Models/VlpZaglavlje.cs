namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class VlpZaglavlje
{
    public long Id { get; set; }
    public string? OrgJed { get; set; }
    public long? Dokument { get; set; }
    public long? Godina { get; set; }
    public long? Broj { get; set; }
    public string? Storno { get; set; }
    public string? Skladiste { get; set; }
    public string? KomitentTip { get; set; }
    public long? Komitent { get; set; }
    public DateTime? Datum { get; set; }
    public DateTime? Vreme { get; set; }
    public DateTime? VremeKreiranja { get; set; }
    public DateTime? DatumValute { get; set; }
    public DateTime? DatumDpo { get; set; }
    public string? EksterniBroj { get; set; }
    public string? Porez { get; set; }
    public string? Korisnik { get; set; }
    public string? Komentar { get; set; }
    public decimal? Rabat { get; set; }
    public long? VlpZaglavljeId { get; set; }
    public long? Kif { get; set; }
    public long? Nalog { get; set; }
    public string? Potvrdjen { get; set; }
    public string? Potvrdio { get; set; }
    public DateTime? VremePotvrde { get; set; }
    public string? BrojIzjave { get; set; }
    public DateTime? DatumIzjave { get; set; }
    public string? TipDobavljaca { get; set; }
    public string? TipKupca { get; set; }
    public string? Zavrsen { get; set; }
    public string? Prodavac { get; set; }
    public string? ProdajnaCena { get; set; }
    public string? Komisionar { get; set; }
    public string? Taksa { get; set; }
    public string? Valuta { get; set; }
    public decimal? Kurs { get; set; }
    public string? NadredjeniTip { get; set; }
    public long? NadredjeniKomitent { get; set; }
    public long? Artikal { get; set; }
    public decimal? DodatniTrosak { get; set; }
    public decimal? PorezNaTrosak { get; set; }
    public long? AvansBroj { get; set; }
    public decimal? AvansIznos { get; set; }
    public string? PoreskaKlauzula { get; set; }
    public string? NacinIsporuke { get; set; }
    public string? CeneZaKalkulaciju { get; set; }
    public long? BrojPaketa { get; set; }
    public DateTime? DatumRacuna { get; set; }
    public string? BrojRacuna { get; set; }
    public string? Storniran { get; set; }
    public string? VlpRazlogPovrata { get; set; }
    public string? IncotermsKlauzula { get; set; }
}