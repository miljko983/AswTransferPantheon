using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AswTransferToPantheon.Services.Interfaces
{
    public interface IArtikliTransferService
    {
        Action<string> LogAction { get; set; }
        Task TransferArtikliPaket(int batchSize, CancellationToken token);
    }
}
