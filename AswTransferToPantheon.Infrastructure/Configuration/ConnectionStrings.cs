using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AswTransferToPantheon.Infrastructure.Configuration
{
    public class ConnectionStrings
    {
        public string AswUser { get; set; } = string.Empty;

        public string AswPassword { get; set; } = string.Empty;

        public string AswDataSource { get; set; } = string.Empty;

        public string Transfer { get; set; } = string.Empty;
    }
}
