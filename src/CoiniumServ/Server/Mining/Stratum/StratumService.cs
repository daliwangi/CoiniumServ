#region License
// 
//     CoiniumServ - Crypto Currency Mining Pool Server Software
//     Copyright (C) 2013 - 2014, CoiniumServ Project - http://www.coinium.org
//     http://www.coiniumserv.com - https://github.com/CoiniumServ/CoiniumServ
// 
//     This software is dual-licensed: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// 
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//    
//     For the terms of this license, see licenses/gpl_v3.txt.
// 
//     Alternatively, you can license this software under a commercial
//     license or white-label it as set out in licenses/commercial.txt.
// 
#endregion

using AustinHarris.JsonRpc;
using CoiniumServ.Jobs;
using CoiniumServ.Pools;
using CoiniumServ.Server.Mining.Service;
using CoiniumServ.Server.Mining.Stratum.Responses;
using CoiniumServ.Shares;
using CoiniumServ.Relay;
using Serilog;
using System;

namespace CoiniumServ.Server.Mining.Stratum
{
    /// <summary>
    /// Stratum protocol implementation.
    /// </summary>
    public class StratumService : JsonRpcService, IRpcService
    {
        private readonly IShareManager _shareManager;

        private IRelayManager _relayManager;

        private readonly ILogger _logger;

        private object shareLock = new object();

        public StratumService(IPoolConfig poolConfig, IShareManager shareManager, IRelayManager relayManager) :
            base(poolConfig.Coin.Name)  //use the base constructor to instantiate the JsonRpcService,which uses the sessionID 
        //and reflection to get the Handlers for the JsonRpcMethods.
        {
            _shareManager = shareManager;
            _relayManager = relayManager;
            _logger = Log.ForContext<StratumService>();
        }

        /// <summary>
        /// Subscribes a Miner to allow it to recieve work to begin hashing and submitting shares.
        /// </summary>
        /// <param name="signature">software signature</param>
        /// <param name="sessionId">optional parameter supplied by miners whom wants to reconnect and continue their old session</param>
        [JsonRpcMethod("mining.subscribe")]
        public SubscribeResponse SubscribeMiner(string signature = null, string sessionId = null)
        {
            var context = (StratumContext) JsonRpcContext.Current().Value;

            var response = new SubscribeResponse();

            response.ExtraNonce1 = context.Miner.ExtraNonce;
            response.ExtraNonce2Size=Relay.RelayManager.IsRelaying?(int)_relayManager.FormattedXNonce2Size:ExtraNonce.ExpectedExtraNonce2Size;

            context.Miner.Subscribe(signature);
            _logger.Debug("Miner subscribed,extranonce1:{0},expected extranonce2size:{1}", response.ExtraNonce1, response.ExtraNonce2Size);
            return response;
        }

        /// <summary>
        /// Authorise a miner based on their username and password
        /// </summary>
        /// <param name="user">Worker Username (e.g. "coinium.1").</param>
        /// <param name="password">Worker Password (e.g. "x").</param>
        [JsonRpcMethod("mining.authorize")]
        public bool AuthorizeMiner(string user, string password)
        {
            var context = (StratumContext)JsonRpcContext.Current().Value;
            return context.Miner.Authenticate(user, password);
        }

        /// <summary>
        /// Allows a miner to submit the work they have done 
        /// </summary>
        /// <param name="user">Worker Username.</param>
        /// <param name="jobId">Job ID(Should be unique per Job to ensure that share diff is recorded properly) </param>
        /// <param name="extraNonce2">Hex-encoded big-endian extranonce2, length depends on extranonce2_size from mining.notify</param>
        /// <param name="nTime"> UNIX timestamp (32bit integer, big-endian, hex-encoded), must be >= ntime provided by mining,notify and <= current time'</param>
        /// <param name="nonce"> 32bit integer hex-encoded, big-endian </param>
        [JsonRpcMethod("mining.submit")]
        public bool SubmitWork(string user, string jobId, string extraNonce2, string nTime, string nonce)
        {
            var context = (StratumContext)JsonRpcContext.Current().Value;

            if (extraNonce2.Length / 2 == (int)_relayManager.FormattedXNonce2Size)       //check if it's a relay share.
            {
                lock (shareLock)
                {
                    string username = _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].workerId;

                    //change extraNonce2 from miner to 4 bytes,and process it as a new share
                    int byteNumToAppend = (int)_relayManager.extraNonce2Size - (int)_relayManager.FormattedXNonce2Size;

                    string xNonce2ToVerify = string.Empty;
                    if (byteNumToAppend > 0)
                        xNonce2ToVerify = context.Miner.ExtraNonce.Substring(context.Miner.ExtraNonce.Length - 2 * byteNumToAppend, 2 * byteNumToAppend) + extraNonce2;
                    else
                        xNonce2ToVerify = extraNonce2;
                    IShare ShareToRelay = _shareManager.ProcessShare(context.Miner, jobId, xNonce2ToVerify, nTime, nonce);
                    bool validity = ShareToRelay.IsValid;

                    //mining.submit("username", "job id", "ExtraNonce2", "nTime", "nOnce").
                    IRelayShare JShare = new RelayShare(username, jobId, xNonce2ToVerify, nTime, nonce);
                    if (validity && CheckShareDiff(ShareToRelay))
                    {
                        _relayManager.MiningSubmit(JShare);
                    }
                    if (!validity)
                    {
                        _logger.Debug("Invalid share.user:{0},jobId:{1},extraNonce2:{2},nTime:{3},nonce:{4}",
                            user, jobId, extraNonce2, nTime, nonce);
                    }
                    return validity;
                }
            }
            else
            {
                lock (shareLock)
                {
                    return _shareManager.ProcessShare(context.Miner, jobId, extraNonce2, nTime, nonce).IsValid;
                }
            }
        }

#region private method
        private bool CheckShareDiff(IShare share)
        {
            return share.Difficulty >= _relayManager.ExternalDiff;
        }
    }
}
        #endregion