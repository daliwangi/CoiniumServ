using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CoiniumServ.Relay
{
    public interface IRelayShare
    {
        string UserName { get; }
        string JobID { get; }
        string ExtraNonce2 { get; }
        string NTime { get; }
        string Nonce { get; }
    }
}
