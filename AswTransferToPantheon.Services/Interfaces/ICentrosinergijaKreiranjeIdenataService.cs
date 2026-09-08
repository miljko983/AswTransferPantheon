using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Interfaces;

public interface ICentrosinergijaKreiranjeIdenataService
{
    Action<string>? LogAction { get; set; }

    Action<BadRecordInfo>? ErrorAction { get; set; }

    Task Execute(CancellationToken token);
}