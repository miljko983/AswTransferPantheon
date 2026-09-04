namespace AswTransferToPantheon.Infrastructure.Configuration;

public sealed class NaloziTaskConfiguration
{
    public string Name { get; set; } = "Nalozi";

    public int BatchSize { get; set; } = 1000;

    public DateTime DateFrom { get; set; }

    public TimeSpan Start { get; set; }

    public TimeSpan? End { get; set; }

    public int PeriodInMinutes { get; set; }

    public bool ExecuteOnStartup { get; set; }
}