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

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AustinHarris.JsonRpc;
using CoiniumServ.Daemon;
using CoiniumServ.Daemon.Responses;
using CoiniumServ.Daemon.Exceptions;
using CoiniumServ.Jobs.Tracker;
using CoiniumServ.Persistance.Layers;
using CoiniumServ.Pools;
using CoiniumServ.Relay;
using CoiniumServ.Server.Mining.Getwork;
using CoiniumServ.Server.Mining.Stratum;
using CoiniumServ.Server.Mining.Stratum.Errors;
using CoiniumServ.Utils.Extensions;
using Serilog;

namespace CoiniumServ.Shares
{
    public class ShareManager : IShareManager
    {
        public event EventHandler BlockFound;

        public event EventHandler ShareSubmitted;

        private readonly IJobTracker _jobTracker;

        private readonly IDaemonClient _daemonClient;

        private readonly IStorageLayer _storageLayer;

        private readonly IPoolConfig _poolConfig;

        private readonly IRelayManager _relayManager;

        private string _poolAccount;

        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ShareManager" /> class.
        /// </summary>
        /// <param name="poolConfig"></param>
        /// <param name="daemonClient"></param>
        /// <param name="jobTracker"></param>
        /// <param name="storageLayer"></param>
        public ShareManager(IPoolConfig poolConfig, IDaemonClient daemonClient, IJobTracker jobTracker, IStorageLayer storageLayer, IRelayManager relayManager)
        {
            _poolConfig = poolConfig;
            _daemonClient = daemonClient;
            _jobTracker = jobTracker;
            _storageLayer = storageLayer;
            _logger = Log.ForContext<ShareManager>().ForContext("Component", poolConfig.Coin.Name);
            _relayManager = relayManager;

            FindPoolAccount();
        }

        /// <summary>
        /// Processes the share.
        /// </summary>
        /// <param name="miner">The miner.</param>
        /// <param name="jobId">The job identifier.</param>
        /// <param name="extraNonce2">The extra nonce2.</param>
        /// <param name="nTimeString">The n time string.</param>
        /// <param name="nonceString">The nonce string.</param>
        /// <returns></returns>
        public IShare ProcessShare(IStratumMiner miner, string jobId, string extraNonce2, string nTimeString, string nonceString)
        {
            var job=_jobTracker.Current;
            ulong id;
            // check if the job exists
            if (string.IsNullOrEmpty(job.RelayId))
            {
                try
                {
                    id = Convert.ToUInt64(jobId, 16);
                    job = _jobTracker.Get(id);
                }
                catch (FormatException e)
                {
                    if (!Relay.RelayManager.IsRelaying)
                    {
                        _logger.Information("{0}.The miner sent a relay share,currently we aren't relaying,just discard it."
                            + "Rebroadcast job in case the miner missed the last broadcasting.",e.Message);
                        //TODO:add a event to be handled in JobManager
                        job = null;
                        id = _jobTracker.Current.Id;
                    }
                    else
                    {
                        _logger.Information("We are relaying,but the relay job hasn't been received" +
                            "but the miner sent a relay share");
                        job = null;
                        id = _jobTracker.Current.Id;
                    }
                }
                catch (Exception e)
                {
                    _logger.Debug("Exception occurred during miner's share submitting:{0}", e.Message);
                    job = null;
                    id = _jobTracker.Current.Id;
                }
            }
            else
            {
                if (job.RelayId == jobId)
                {
                    job = _jobTracker.Current;
                    id = _jobTracker.Current.Id;
                }
                else
                {
                    var staleRelayJob = _jobTracker.Get(jobId);
                    if (staleRelayJob != null)
                    {
                        job = staleRelayJob;
                        id = job.Id;
                    }
                    else
                    {
                        try
                        {
                            id = Convert.ToUInt64(jobId, 16);
                            job = _jobTracker.Get(id);
                        }
                        catch (FormatException e)
                        {
                            if (!Relay.RelayManager.IsRelaying)
                            {
                                _logger.Information("{0}.The miner's jobId can't be identified,discard it,rebroardcast job or disconnect.", e.Message);
                                //TODO:add a event to be handled in JobManager
                                job = null;
                                id = _jobTracker.Current.Id;
                            }
                            else
                            {
                                _logger.Information("{0}.The miner's jobId can't be identified,discard it,rebroardcast job or disconnect.", e.Message);
                                //TODO:add a event to be handled in JobManager
                                job = null;
                                id = _jobTracker.Current.Id;
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Debug("Exception occurred during miner's share submitting:{0}", e.Message);
                            job = null;
                            id = _jobTracker.Current.Id;
                        }
                    }
                }
            }

            IShare share;
            if (!string.IsNullOrEmpty(_jobTracker.Current.RelayId))
            {
                // create the share,extraNonce2 must be a 4-byte string
                share = new Share(miner, id, job, extraNonce2, nTimeString, nonceString,
                    _relayManager.extraNonce1.Length / 2);
            }
            else
                share = new Share(miner, id, job, extraNonce2, nTimeString, nonceString);

            if (share.IsValid)
                HandleValidShare(share);
            else
                HandleInvalidShare(share);

            OnShareSubmitted(new ShareEventArgs(miner));  // notify the listeners about the share.

            return share;
        }

        public IShare ProcessShare(IGetworkMiner miner, string data)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// When new job is received when relaying, persist the last block's shares.
        /// </summary>
        /// <param name="relayRevenue"></param>
        public void PersistBlock(decimal relayRevenue)
        {
            _storageLayer.MoveCurrentShares(_jobTracker.Current.Height - 1);

            Task.Factory.StartNew(i => AddNewBlockToStorage((decimal)i), relayRevenue, TaskCreationOptions.None);
        }

        private void HandleValidShare(IShare share)
        {
            var miner = (IStratumMiner)share.Miner;
            miner.ValidShareCount++;

            _storageLayer.AddShare(share); // commit the share.
            _logger.Debug("Share accepted at {0:0.00}/{1} by miner {2:l}", share.Difficulty, miner.Difficulty, miner.Username);

            //relaying checking added.
            if (!Relay.RelayManager.IsRelaying)
            {
                // check if share is a block candidate
                if (!share.IsBlockCandidate)
                    return;

                // submit block candidate to daemon.
                var accepted = SubmitBlock(share);

                if (!accepted) // if block wasn't accepted
                    return; // just return as we don't need to notify about it and store it.

                OnBlockFound(EventArgs.Empty); // notify the listeners about the new block.

                _storageLayer.AddBlock(share); // commit the block details to storage.
                _storageLayer.MoveCurrentShares(share.Height); // move associated shares to new key.
            }
        }

        private void HandleInvalidShare(IShare share)
        {
            var miner = (IStratumMiner)share.Miner;
            miner.InvalidShareCount++;

            JsonRpcException exception = null; // the exception determined by the stratum error code.
            switch (share.Error)
            {
                case ShareError.DuplicateShare:
                    exception = new DuplicateShareError(share.Nonce);                    
                    break;
                case ShareError.IncorrectExtraNonce2Size:
                    exception = new OtherError("Incorrect extranonce2 size");
                    break;
                case ShareError.IncorrectNTimeSize:
                    exception = new OtherError("Incorrect nTime size");
                    break;
                case ShareError.IncorrectNonceSize:
                    exception = new OtherError("Incorrect nonce size");
                    break;
                case ShareError.JobNotFound:
                    exception = new JobNotFoundError(share.JobId);
                    break;
                case ShareError.LowDifficultyShare:
                    exception = new LowDifficultyShare(share.Difficulty);
                    break;
                case ShareError.NTimeOutOfRange:
                    exception = new OtherError("nTime out of range");
                    break;
            }
            JsonRpcContext.SetException(exception); // set the stratum exception within the json-rpc reply.

            Debug.Assert(exception != null); // exception should be never null when the share is marked as invalid.
            _logger.Debug("Rejected share by miner {0:l},extra nonce1 {1},extra nonce2 {2}, reason: {3:l}", miner.Username, miner.ExtraNonce,share.ExtraNonce2, exception.message);
        }

        private bool SubmitBlock(IShare share)
        {
            // TODO: we should try different submission techniques and probably more then once: https://github.com/ahmedbodi/stratum-mining/blob/master/lib/bitcoin_rpc.py#L65-123

            try
            {
                if (_poolConfig.Coin.Options.SubmitBlockSupported) // see if submitblock() is available.
                    _daemonClient.SubmitBlock(share.BlockHex.ToHexString()); // submit the block.
                else
                    _daemonClient.GetBlockTemplate(share.BlockHex.ToHexString()); // use getblocktemplate() if submitblock() is not supported.

                var block = _daemonClient.GetBlock(share.BlockHash.ToHexString()); // query the block.

                if (block == null) // make sure the block exists
                    return false;

                if (block.Confirmations == -1) // make sure the block is accepted.
                {
                    _logger.Debug("Submitted block [{0}] is orphaned; [{1:l}]", block.Height, block.Hash);
                    return false;
                }

                var expectedTxHash = share.CoinbaseHash.Bytes.ReverseBuffer().ToHexString(); // calculate our expected generation transactions's hash
                var genTxHash = block.Tx.First(); // read the hash of very first (generation transaction) of the block

                if (expectedTxHash != genTxHash) // make sure our calculated generated transaction and one reported by coin daemon matches.
                {
                    _logger.Debug("Submitted block [{0}] doesn't seem to belong us as reported generation transaction hash [{1:l}] doesn't match our expected one [{2:l}]", block.Height, genTxHash, expectedTxHash);
                    return false;
                }

                var genTx = _daemonClient.GetTransaction(block.Tx.First()); // get the generation transaction.

                // make sure we were able to read the generation transaction
                if (genTx == null)
                {
                    _logger.Debug("Submitted block [{0}] doesn't seem to belong us as we can't read the generation transaction on our records [{1:l}]", block.Height, block.Tx.First());
                    return false;
                }

                var poolOutput = genTx.GetPoolOutput(_poolConfig.Wallet.Adress, _poolAccount); // get the output that targets pool's central address.

                // make sure the blocks generation transaction contains our central pool wallet address
                if (poolOutput == null)
                {
                    _logger.Debug("Submitted block [{0}] doesn't seem to belong us as generation transaction doesn't contain an output for pool's central wallet address: {0:}", block.Height, _poolConfig.Wallet.Adress);
                    return false;
                }

                // if the code flows here, then it means the block was succesfully submitted and belongs to us.
                share.SetFoundBlock(block, genTx); // assign the block to share.

                _logger.Information("Found block [{0}] with hash [{1:l}]", share.Height, share.BlockHash.ToHexString());

                return true;
            }
            catch (RpcException e)
            {
                // unlike BlockProcessor's detailed exception handling and decision making based on the error,
                // here in share-manager we only one-shot submissions. If we get an error, basically we just don't care about the rest
                // and flag the submission as failed.
                _logger.Debug("We thought a block was found but it was rejected by the coin daemon; [{0:l}] - reason; {1:l}", share.BlockHash.ToHexString(), e.Message);
                return false;
            }
        }

        private void OnBlockFound(EventArgs e)
        {
            var handler = BlockFound;

            if (handler != null)
                handler(this, e);
        }

        private void OnShareSubmitted(EventArgs e)
        {
            var handler = ShareSubmitted;

            if (handler != null)
                handler(this, e);
        }

        private void FindPoolAccount()
        {
            try
            {
                _poolAccount = !_poolConfig.Coin.Options.UseDefaultAccount // if UseDefaultAccount is not set
                    ? _daemonClient.GetAccount(_poolConfig.Wallet.Adress) // find the account of the our pool address.
                    : ""; // use the default account.
            }
            catch (RpcException e)
            {
                _logger.Error("Error getting account for pool central wallet address: {0:l} - {1:l}", _poolConfig.Wallet.Adress, e.Message);
            }
        }

        /// <summary>
        /// Must be called on another thread in case the new block hasn't been received by daemon client yet.
        /// </summary>
        /// <param name="relayRevenue"></param>
        private void AddNewBlockToStorage(decimal relayRevenue)
        {
            int ExceptionCount = 0;
            do
            {
                try
                {
                    ExceptionCount = 0;
                    string blockHash = _daemonClient.GetBlockHash(_jobTracker.Current.Height - 1);
                    _logger.Debug("Blockhash of previous block is {0}", blockHash);
                    Block block = _daemonClient.GetBlock(blockHash);
                    _storageLayer.AddBlock(block, relayRevenue);
                }
                catch (RpcTimeoutException e)
                {
                    ExceptionCount++;
                    _logger.Information("Rpc timeout exception occurs when persisting,{0},retrying.", e.Message);
                }
                catch (RpcConnectionException e)
                {
                    ExceptionCount++;
                    _logger.Information("Rpc connection exception occurs when persisting,{0},retrying.", e.Message);
                }
                catch (RpcErrorException e)
                {
                    ExceptionCount++;
                    if (e.Message == "Block height out of range")
                    {
                        _logger.Information("Block mined recently hasn't been received yet, retry later.");
                        Thread.Sleep(15000);
                    }
                    else
                    {
                        _logger.Information("Rpc error occurs when persisting,code:{0},message:{1},retrying.", e.Code, e.Message);
                    }
                }
                catch (Exception e)
                {
                    ExceptionCount++;
                    _logger.Information("Unhandled RPC exception occurs when persisting,error:{0},retrying.", e.Message);
                }
            } while (ExceptionCount > 0);
        }

        public void RecordWholeDayData(string UserName,UInt64 hashrate)
        {
            _storageLayer.RecordWholeDay(UserName, hashrate);
        }
    }
}
