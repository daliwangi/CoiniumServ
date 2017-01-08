using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CoiniumServ.Server.Web.Models.Pool
{
    /// <summary>
    /// Needs Caution because the data is managed with big volumn.
    /// </summary>
    public class WorkerHashrateModel:IDisposable
    {
        public Dictionary<string,UInt64> WholeDayData { get; set; }

        public ulong recentMinutes { get; set; }

        public void Dispose()
        {
        }
    }
}
