using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CoiniumServ.Relay
{
    public interface IRelayTarget
    {
        bool enabled { get;}

        string url { get;}

        int port { get; }

        string workerId{ get;}

        string password { get;}

        int refreshInterval { get; }
    }
}
