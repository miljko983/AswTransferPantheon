
using AswTransferToPantheon.Infrastructure.Configuration;

namespace AswTransferToPantheon.Services.Interfaces
{
    public interface ITaskSchedulerService
    {
        Action<string> LogAction { get; set; }
        Action<Exception, string> LogErrorAction { get; set; }

        void CancelTasks();
        Task ScheduleTasks();
    }
}
