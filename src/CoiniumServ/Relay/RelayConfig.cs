using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Dynamic;
using Serilog;

namespace CoiniumServ.Relay
{
    
    //TODO:add logic for deciding if the node pool is valid;
    public class RelayConfig:IRelayConfig
    {
        public List<IRelayTarget> targetNodes { get; private set; }

        public RelayConfig(dynamic config)
        {
            try
            {
                targetNodes=new List<IRelayTarget>();
                if (config.targetnodes is JsonConfig.NullExceptionPreventer)
                {
                    AddDefaults();
                }
                else
                {
                    foreach (var data in config.targetnodes)
                    {
                        var node = new RelayTarget(data);
                        targetNodes.Add(node);
                    }
                }
            }
            catch(Exception e)
            {
                Log.Logger.ForContext<RelayConfig>().Error(e, "Error loading relay configuration");
            }
            
        }

        /// <summary>
        /// the default target pool is eligius.
        /// </summary>
        private void AddDefaults()
        {
            dynamic defaultpool = new ExpandoObject();
            defaultpool.enabled = true;
            defaultpool.url = "stratum.mining.eligius.st";
            defaultpool.port = 3334;
            defaultpool.workerId = "1MffYMaM1MGNEes7RQj4bHRTj1CETUbrB3";
            defaultpool.password = "x";
            defaultpool.refreshInterval = 3000;

            targetNodes.Add(new RelayTarget(defaultpool));
        }

    }
}
