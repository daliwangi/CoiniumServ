using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CoiniumServ.Relay
{
    public interface IRelayConfig
    {
        List<IRelayTarget> targetNodes { get; }
    }
}
