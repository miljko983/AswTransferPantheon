using System;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Interfaces;

public interface IBoDocumentImportService
{
    Task<BoDocumentRunResult> ExecuteAsync(DateTime datum, BoDocumentTaskConfiguration configuration,
                                           Action<string> log, CancellationToken token);
}