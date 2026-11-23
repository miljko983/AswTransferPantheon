using System;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Interfaces;

public interface IWmsDocumentProcessingService
{
    Task<WmsDocumentRunResult> ExecuteAsync(
        string vrsta,
        DateTime datum,
        WmsDocumentProcessingConfiguration configuration,
        Action<string> log,
        CancellationToken token);
}
