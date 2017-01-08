using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using CoiniumServ.Shares;
using System.Collections;

namespace CoiniumServ.Relay
{
    [JsonArray]
    public class RelayShare:IRelayShare,IEnumerable<object>
    {
        [JsonIgnore]
        public string UserName{get;private set; }

        //JobID as hex string
        [JsonIgnore]
        public string JobID { get;private set; }
        
        [JsonIgnore]
        public string ExtraNonce2 { get;private set; }
        
        [JsonIgnore]
        public string NTime { get;private set; }  

        [JsonIgnore]
        public string Nonce { get;private set; }

        public RelayShare(string userName, string jobId, string extraNonce2, string nTime, string nonce)
        {
            //It's necessary to change the username,JobID etc in RelayManager and StratumService
            UserName = userName;
            JobID = jobId;
            ExtraNonce2 = extraNonce2;
            NTime = nTime;
            Nonce = nonce;
        }

        public IEnumerator<object> GetEnumerator()
        {
            var data = new List<object>
            {
                UserName,
                JobID,
                ExtraNonce2,
                NTime,
                Nonce
            };
            return data.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
