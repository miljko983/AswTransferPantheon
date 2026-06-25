using AswTransferToPantheon.Services.Interfaces;

namespace AswTransferToPantheon.Services.Implementation
{
    public class KifTransfer : IKifTransfer
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
