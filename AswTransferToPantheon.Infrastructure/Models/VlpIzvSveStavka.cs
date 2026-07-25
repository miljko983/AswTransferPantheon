namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class VlpIzvSveStavka
{
    public int VlpZaglavlje { get; set; }
    public int RedniBroj { get; set; }
    public int Artikal { get; set; }
    public string JedinicaMere { get; set; } = string.Empty;
    public string TarifnaGrupa { get; set; } = string.Empty;
    public string? Taksa { get; set; }
    public string? Akciza { get; set; }
    public decimal Nabavna { get; set; }
    public decimal Veleprodajna { get; set; }
    public decimal Maloprodajna { get; set; }
    public decimal Rabat { get; set; }
    public string ProdajnaCena { get; set; } = string.Empty;
    public decimal VeleprodajnaVal { get; set; }
    public int? BrojPaketa { get; set; }
    public DateTime? DatumValute { get; set; }
}