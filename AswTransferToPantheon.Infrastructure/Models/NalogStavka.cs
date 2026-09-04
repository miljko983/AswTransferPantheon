namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class NalogStavka
{
    public long Id { get; set; }

    public long? Nalog { get; set; }

    public long RedniBroj { get; set; }

    public string? Konto { get; set; }

    public string? OrgJed { get; set; }

    public string? KomitentTip { get; set; }

    public long? Komitent { get; set; }

    public DateTime DatumDokumenta { get; set; }

    public DateTime? DatumValute { get; set; }

    public string? Valuta { get; set; }

    public decimal Odnos { get; set; }

    public decimal Duguje { get; set; }

    public decimal Potrazuje { get; set; }

    public decimal DugujeVal { get; set; }

    public decimal PotrazujeVal { get; set; }

    public long DokumentId { get; set; }

    public long DokumentStavka { get; set; }

    public string? DokumentVrsta { get; set; }

    public string? EksterniBroj { get; set; }

    public string? Komentar { get; set; }

    public string? Korisnik { get; set; }

    public DateTime VremeKreiranja { get; set; }

    public string? PozivniBroj { get; set; }

    public decimal IznosDokumenta { get; set; }

    public decimal IznosUplate { get; set; }

    public decimal IznosZatvoren { get; set; }

    public string? Referent { get; set; }
}