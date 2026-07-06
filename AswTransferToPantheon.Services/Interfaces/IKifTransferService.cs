using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AswTransferToPantheon.Services.Interfaces
{
    public interface IKifTransferService
    {
        Task Transfer(int batchSize, CancellationToken token);
        Action<string>? LogAction { get; set; }

        Action<string, string, string, string, Exception>? BadRecordAction { get; set; }
    }
}
