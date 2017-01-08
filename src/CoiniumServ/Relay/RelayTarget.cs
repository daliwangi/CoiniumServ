using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CoiniumServ.Relay
{
    public class RelayTarget:IRelayTarget
    {
        public bool enabled { get; private set; }

        public string url { get; private set; }

        public int port { get; private set; }

        public string workerId{get;private set;}

        public string password { get; private set; }

        public int refreshInterval { get; private set; }

        public RelayTarget(dynamic target)
        {
            enabled = target.enabled;
            url = target.url;
            port = (int)target.port;
            workerId = target.workerid;
            password = target.password;
            refreshInterval = (int)target.refreshInterval;
        }

    }
}
