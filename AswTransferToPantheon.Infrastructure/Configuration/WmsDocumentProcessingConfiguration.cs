using System;

namespace AswTransferToPantheon.Infrastructure.Configuration;

public sealed class WmsDocumentProcessingConfiguration
{
    public bool Enabled { get; set; } = false;

    // Donja granica za NALOZI.DATUM. Gornja je dan poziva procedure.
    public DateTime DateFrom { get; set; } = new(2026, 8, 1);

    public int UserId { get; set; } = 1;

    public bool SamoProvera { get; set; } = false;

    // 0 = bez vremenskog ogranicenja SQL obrade, uz mogucnost otkazivanja.
    public int CommandTimeoutSeconds { get; set; } = 0;
}
