namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class ArtikalOsobine
{
    public long IdArtikla { get; set; }

    public string? OsnovnaSifra { get; set; }

    public string? Opcija { get; set; }

    public string? KataloskaOznaka { get; set; }

    public string? MaloprodajnaJm { get; set; }

    public string? VeleprodajnaJm { get; set; }

    public string? NarucivanjeJm { get; set; }

    public string? IzdavanjeJm { get; set; }

    public string? TipVarijante { get; set; }

    public string? KomitentTip { get; set; }

    public long Komitent { get; set; }

    public string? Akciza { get; set; }

    public string? Taksa { get; set; }

    public string? TarifniStav { get; set; }

    public string? StampatiBarkod { get; set; }

    public string? Referent { get; set; }

    public string? Serija { get; set; }

    public string? Konsignacija { get; set; }

    public string? RokTrajanja { get; set; }

    public long RokTrajanjaDana { get; set; }

    public string? Brend { get; set; }

    public string? ProizvodjacTip { get; set; }

    public long Proizvodjac { get; set; }

    public string? ReferentNabavke { get; set; }

    public string? TipZalihe { get; set; }

    public string? Aktivan { get; set; }

    public long? Asortiman { get; set; }

    public string? Kategorija { get; set; }

    public string? TransportJm { get; set; }

    public decimal? OdnosPolica { get; set; }

    public decimal? OdnosPaleta { get; set; }

    public decimal Tezina { get; set; }

    public decimal Zapremina { get; set; }

    public decimal Duzina { get; set; }

    public decimal Sirina { get; set; }

    public decimal Visina { get; set; }

    public decimal? VisinaPalete { get; set; }

    public string? Drzava { get; set; }

    public string? Valuta { get; set; }

    public int? RokIsporuke { get; set; }

    public string? SerijaMlp { get; set; }

    public string? RokTrajanjaMlp { get; set; }

    public string? EkoloskaNaknada { get; set; }

    public string? JedinicnaMpJm { get; set; }

    public string? MlpJmPar { get; set; }

    public string? Opis { get; set; }
}