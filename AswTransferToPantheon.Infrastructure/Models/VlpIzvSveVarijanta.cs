namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class VlpIzvSveVarijanta
{
    public int Id { get; set; }
    public int VlpZaglavlje { get; set; }
    public int RedniBroj { get; set; }
    public int RedniBrojVar { get; set; }
    public string Skladiste { get; set; } = string.Empty;
    public int Artikal { get; set; }
    public string Varijanta { get; set; } = string.Empty;
    public DateTime RokTrajanja { get; set; }
    public string Serija { get; set; } = string.Empty;
    public decimal Trazeno { get; set; }
    public decimal Isporuceno { get; set; }
    public decimal KolicinaUlaz { get; set; }
    public decimal KolicinaIzlaz { get; set; }
    public decimal Skladisna { get; set; }
    public decimal Duguje { get; set; }
    public decimal Potrazuje { get; set; }
}