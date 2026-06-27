namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class Artikal
{
    public long Id { get; set; }
    public string? Sifra { get; set; }
    public string? Naziv { get; set; }
    public string? Prevod { get; set; }
    public string? RobnaGrupa { get; set; }
    public string? TarifnaGrupa { get; set; }
    public string? FazniPorez { get; set; }
    public string? Tip { get; set; }
    public string? SkraceniNaziv { get; set; }
    public string? Vrsta { get; set; }
    public string? Status { get; set; }
    public DateTime? VremeStatusa { get; set; }
    public string? Korisnik { get; set; }
}