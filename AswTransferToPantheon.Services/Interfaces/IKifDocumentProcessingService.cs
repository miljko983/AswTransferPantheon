using System;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Interfaces;

public interface IKifDocumentProcessingService
{
    Task<KifDocumentRunResult> ExecuteAsync(DateTime datum, KifDocumentProcessingConfiguration configuration,
                                            Action<string> log, CancellationToken token);
}
