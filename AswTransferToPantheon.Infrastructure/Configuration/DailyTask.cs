using AswTransferToPantheon.Infrastructure.Enums;

namespace AswTransferToPantheon.Infrastructure.Configuration;

public sealed class DailyTask
{
    public int BatchSize { get; set; }

    public string Name { get; set; }

    public TimeSpan Start { get; set; }

    public bool ExecuteOnStartup { get; set; }

    public List<TaskType> ParallelTasks { get; set; } = [];

    public List<TaskType> Tasks { get; set; } = [];
}