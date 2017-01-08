using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CoiniumServ.Utils.Commands;
using CoiniumServ.Relay;

namespace CoiniumServ.Server.Commands
{
        [CommandGroup("relay", "Allow you to start share relaying or stop it:start/stop/relayto")]
        public class RelayCommand : CommandGroup
        {
            [Command("start", "Start relaying.")]
            public void Start(string[] @params)
            {
                RelayManager.SetRelay(true);
            }

            [Command("stop", "end relaying.")]
            public void Stop(string[] @params)
            {
                RelayManager.SetRelay(false);
            }

            [Command("switchupstream","switch upstream pool")]
            public void Switchupstream(string[] @params)
            {
                RelayManager.ManualSwitchUpstream();
            }
        }

}
