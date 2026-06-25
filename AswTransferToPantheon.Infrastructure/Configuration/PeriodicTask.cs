using AswTransferToPantheon.Infrastructure.Enums;

namespace AswTransferToPantheon.Infrastructure.Configuration
{
    public class PeriodicTask
    {
        public string Name { get; set; }
        public int BatchSize { get; set; }

        public TimeSpan Start { get; set; }

        public TimeSpan? End { get; set; }

        public int PeriodInMinutes { get; set; }

        public bool ExecuteOnStartup { get; set; }

        public List<TaskType> Tasks { get; set; } = [];
    }
}
