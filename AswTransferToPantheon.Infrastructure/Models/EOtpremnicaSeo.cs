namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class EOtpremnicaSeo
{
    public long Id { get; set; }

    public long VlpZaglavlje { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? RequestId { get; set; }

    public string? ExtDocumentId { get; set; }

    public DateTime Vreme { get; set; }

    public string Korisnik { get; set; } = string.Empty;

    public DateTime RequestDate { get; set; }

    public string? PvRequestId { get; set; }

    public DateTime? PvRequestDate { get; set; }

    public string? PzRequestId { get; set; }

    public DateTime? PzRequestDate { get; set; }
}