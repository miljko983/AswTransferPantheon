namespace AswTransferToPantheon.Infrastructure.Configuration;

public sealed class SchedulerConfiguration
{
    public List<DailyTask> DailyTasks { get; set; } = [];

    public List<PeriodicTask> PeriodicTasks { get; set; } = [];
}