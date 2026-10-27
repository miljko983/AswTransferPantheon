using System;
using System.Collections.Generic;

namespace AswTransferToPantheon.Infrastructure.Configuration;

public sealed class BoDocumentTaskConfiguration
{
    public string Name { get; set; } = "BODokumenti";

    public bool Enabled { get; set; } = true;

    public TimeSpan Start { get; set; } = new(22, 0, 0);
    // Ako nije zadato, izvršava se jednom u dozvoljenom danu.
    // Na primer, 180 znači ponavljanje na svaka 3 sata od Start do ponoći.
    public int? PeriodInMinutes { get; set; }

    // Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday.
    // Ako nije navedeno ili je lista prazna, raspored važi svakog dana.
    public List<string>? DaysOfWeek { get; set; } = [];

    public bool ExecuteOnStartup { get; set; } = false;

    public int BatchSize { get; set; } = 100;

    public int? UserId { get; set; } = 1;

    public bool SamoProvera { get; set; } = false;

    public bool ObracunNaKraju { get; set; } = true;

    public bool PrikaziVreme { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 3600;
}