using AswTransferToPantheon.Services.Interfaces;

namespace AswTransferToPantheon.Services.Implementation
{
    public class KifTransferService : IKifTransferService
    {
        public async Task Transfer(int batchSize, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            // udri dalje
        }
    }
}
