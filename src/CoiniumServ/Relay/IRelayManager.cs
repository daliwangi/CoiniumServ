using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AustinHarris.JsonRpc;
using CoiniumServ.Utils.Commands;

namespace CoiniumServ.Relay
{
    public interface IRelayManager
    {                
        string extraNonce1 { get; set; }
        uint extraNonce2Size { get; set; }
        bool Subscribe();
        bool Authorize();
        bool MiningSubmit(IRelayShare RelayShare);
        void FormatExtraNonce();
        UInt64 FormattedXNonce1 { get; set; }
        UInt32 FormattedXNonce2Size { get; set; }
        string XNonce1Prefix { get; set; }
        int TotalExtraNonceSize { get; }
        void RecvForeignPool();   //receive job notification from foreign pool
        byte[] Buffer { get; }
        IRelayConfig RelayConfig { get; }
        bool ForeignPoolConnectionEstablished { get; }
        double ExternalDiff { get; set; }
        bool SendResponse(JsonResponse response);
        double BlockShare { get; }
        void AddRoundShare();
        void ResetBlockShare();
        decimal NetworkDiff { get; }
        void SetNetworkDiff(decimal NewDiff);
        decimal CalcRoundRevenue(ulong height);
        event EventHandler ForeignPoolRequestSent;
        event EventHandler RelayStatusChanged;
        event EventHandler UpstreamPoolIdle;
        event EventHandler UpstreamSwitched;
        void Initialize();
        void OnUpstreamPoolIdle(object sender, EventArgs e);
        int PoolIndex { get; }
    }
}
