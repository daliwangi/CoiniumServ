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
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using CoiniumServ.Algorithms;
using CoiniumServ.Daemon;
using CoiniumServ.Daemon.Exceptions;
using CoiniumServ.Daemon.Responses;
using CoiniumServ.Jobs.Tracker;
using CoiniumServ.Mining;
using CoiniumServ.Pools;
using CoiniumServ.Relay;
using CoiniumServ.Server.Mining.Getwork;
using CoiniumServ.Server.Mining.Stratum;
using CoiniumServ.Utils.Extensions;
using CoiniumServ.Shares;
using CoiniumServ.Transactions;
using Serilog;
using Newtonsoft.Json;
using AustinHarris.JsonRpc;

namespace CoiniumServ.Jobs.Manager
{
    public class JobManager : IJobManager
    {
        private readonly IDaemonClient _daemonClient;

        private readonly IJobTracker _jobTracker;

        private readonly IShareManager _shareManager;

        private readonly IMinerManager _minerManager;

        private readonly IHashAlgorithm _hashAlgorithm;

        private readonly IRelayManager _relayManager;

        private readonly IJobCounter _jobCounter;

        private readonly IPoolConfig _poolConfig;

        private IExtraNonce _extraNonce;

        private readonly ILogger _logger;

        public IExtraNonce extraNonce { get { return _extraNonce; } }

        private object _pollerLock = new object();

        public event EventHandler ForeignPoolSubscribed;

        public event EventHandler RelayingStopped;

        private Timer _reBroadcastTimer; // timer for rebroadcasting jobs after an pre-configured idle perioud.

        private Timer _blockPollerTimer; // timer for polling new blocks.

        private Timer _getForeignMsg;    //receive foreign pool jsonrequests.

        private TimerCallback _foreignPoolPoller;

        //private Timer _relayStatusChecking;

        //private bool _formerRelayStatus;

        public JobManager(IPoolConfig poolConfig, IDaemonClient daemonClient, IJobTracker jobTracker, IShareManager shareManager,
            IMinerManager minerManager, IHashAlgorithm hashAlgorithm,IRelayManager relayManager)
        {
            _daemonClient = daemonClient;
            _jobTracker = jobTracker;
            _shareManager = shareManager;
            _minerManager = minerManager;
            _relayManager = relayManager;
            _hashAlgorithm = hashAlgorithm;
            _poolConfig = poolConfig;
            
            _jobCounter = new JobCounter(); // todo make this ioc based too.

            _logger = Log.ForContext<JobManager>().ForContext("Component", poolConfig.Coin.Name);
        }

        public void Initialize(UInt32 instanceId)
        {
            _extraNonce = new ExtraNonce(instanceId);
            _shareManager.BlockFound += OnBlockFound;
            _minerManager.MinerAuthenticated += OnMinerAuthenticated;
            _relayManager.ForeignPoolRequestSent += OnForeignPoolRequestSent;
            _relayManager.RelayStatusChanged += _onRelayStatusChanged;
            _relayManager.UpstreamPoolIdle += _onUpstreamPoolIdle;
            _relayManager.UpstreamSwitched += _onUpstreamSwitched;

            //keep polling foreign pool for new jobs.
            _foreignPoolPoller += pollForeignPool;

            // create the timers as disabled.
            _reBroadcastTimer = new Timer(IdleJobTimer, null,Timeout.Infinite, Timeout.Infinite);
            _blockPollerTimer = new Timer(BlockPoller, null, Timeout.Infinite, Timeout.Infinite);
            _getForeignMsg = new Timer(_foreignPoolPoller, null, Timeout.Infinite, Timeout.Infinite);

            CreateAndBroadcastNewJob(true); // broadcast a new job initially - which will also setup the timers.
        }

        private void OnBlockFound(object sender, EventArgs e)
        {
            _logger.Verbose("As we have just found a new block, rebroadcasting new work");
            CreateAndBroadcastNewJob(false);
        }

        private void IdleJobTimer(object state)
        {
            _logger.Verbose("As idle job timer expired, rebroadcasting new work");
            CreateAndBroadcastNewJob(true);
        }

        private void BlockPoller(object stats)
        {
            if (_jobTracker.Current == null) // make sure we already have succesfully created a previous job.
                return; // else just skip block-polling until we do so.

            try
            {
                var blockTemplate = _daemonClient.GetBlockTemplate(_poolConfig.Coin.Options.BlockTemplateModeRequired);

                if (blockTemplate.Height == _jobTracker.Current.Height) // if network reports the same block-height with our current job.
                    return; // just return.
                
                _logger.Verbose("A new block {0} emerged in network, rebroadcasting new work", blockTemplate.Height);
                CreateAndBroadcastNewJob(false); // broadcast a new job.
            }
            catch (RpcException) { } // just skip any exceptions caused by the block-pooler queries.
            catch (NullReferenceException) { }

            _blockPollerTimer.Change(_poolConfig.Job.BlockRefreshInterval, Timeout.Infinite); // reset the block-poller timer so we can keep polling.
        }

        private void CreateAndBroadcastNewJob(bool initiatedByTimer)
        {
            var job = GetNewJob(initiatedByTimer); // create a new job.

            if (job != null) // if we were able to create a new job
            {
                var count = BroadcastJob(job); // broadcast to miners.  

                if (!RelayManager.IsRelaying)
                {
                    _getForeignMsg.Change(Timeout.Infinite, Timeout.Infinite);   //stop the timer for polling foreign pool.
                    _blockPollerTimer.Change(_poolConfig.Job.BlockRefreshInterval, Timeout.Infinite); // reset the block-poller timer so we can start or keep polling for a new block in the network.
                }
                else
                {
                    _getForeignMsg.Change(_relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].refreshInterval,Timeout.Infinite);
                    _blockPollerTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }

                if (initiatedByTimer)
                    _logger.Information("Broadcasted new job 0x{0:x} to {1} subscribers as no new blocks found for last {2} seconds", 
                        job.Id, count, _poolConfig.Job.RebroadcastTimeout);
                else
                    _logger.Information("Broadcasted new job 0x{0:x} to {1} subscribers as network found a new block", job.Id, count);
            }

            // no matter we created a job successfully or not, reset the rebroadcast timer, so we can keep trying. 
            if (!RelayManager.IsRelaying)
            {
                _reBroadcastTimer.Change(_poolConfig.Job.RebroadcastTimeout * 1000, Timeout.Infinite);
            }
            else
            {
                _getForeignMsg.Change(_relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].refreshInterval,Timeout.Infinite);
                _reBroadcastTimer.Change(_poolConfig.Job.RebroadcastTimeout * 1000, Timeout.Infinite);
            }
        }

        private IJob GetNewJob(bool initiatedByTimer)
        {
            if (RelayManager.IsRelaying)
            {
                if(initiatedByTimer)
                {
                    _logger.Debug("Upstream idle,switch pools...");
                    _relayManager.OnUpstreamPoolIdle(this, EventArgs.Empty);
                    return null;
                }
                else
                {
                    //the poller keeps getting the latest job from foreign pool.
                    return _jobTracker.Current;
                }
            }
            else
            {
                try
                {
                    var blockTemplate = _daemonClient.GetBlockTemplate(_poolConfig.Coin.Options.BlockTemplateModeRequired);

                    // TODO: convert generation transaction to ioc & DI based.
                    var generationTransaction = new GenerationTransaction(extraNonce, _daemonClient, blockTemplate, _poolConfig);
                    generationTransaction.Create();    //create the Initial and Final part of the generation tx.

                    // create the job notification.
                    var job = new Job(_jobCounter.Next(), _hashAlgorithm, blockTemplate, generationTransaction)
                    {
                        CleanJobs = (_jobTracker.Current == null
                        || blockTemplate.Height != (_jobTracker.Current != null ? _jobTracker.Current.Height : 0)
                        || _jobTracker.GetRecentCleanJob() == null
                        || blockTemplate.CurTime - _jobTracker.GetRecentCleanJob().CreationTime > 30) ?
                        true : false
                    };
                    if (!string.IsNullOrEmpty(_jobTracker.Current != null ? _jobTracker.Current.RelayId : null))
                    {
                        _logger.Debug("Disconnect all,called by GetNewJob().");
                        Task.Factory.StartNew(() => { OnRelayingStopped(); });
                    }
                    _jobTracker.Add(job);

                    return job;
                }
                catch (RpcException rpcException)
                {
                    _logger.Error("New job creation failed: {0:l}", rpcException.Message);
                    return null;
                }
                catch(NullReferenceException e)
                {
                    _logger.Debug("Can't read block template.{0}", e.Message);
                    return null;
                }
            }
        }

        private string ReceiveNotification()
        {
            if (_relayManager.extraNonce1 == "ffff0000"&&!_relayManager.ForeignPoolConnectionEstablished)//the same code snippet used to check if foreigh pool subscription is done
            {
                _relayManager.Subscribe();
            }
            else
            {
                _logger.Debug("foreign pool extra nonce is:{0},extra nonce 2 size is:{1}.",
                    _relayManager.extraNonce1, _relayManager.extraNonce2Size);
            }
            _relayManager.RecvForeignPool();
            //string FgnPoolNote = Encoding.ASCII.GetString(_relayManager.Buffer);      //TODO:caution: It is byte array received, the format may need a change.
            string FgnPoolNote = _relayManager.Buffer.ToEncodedString().Trim().Trim('\0');                //ToEncodedString()采用的是StratumServer的OnDataReceived(）中的方法

            //display the notification details.
            if (!string.IsNullOrEmpty(FgnPoolNote))
            {
                _logger.Debug("notification received from pool:{0}.", FgnPoolNote);
                return FgnPoolNote;
            }
            else
            {
                _logger.Debug("Fail to receive note from foreign pool");
                return null;
            }
        }

        private IJob GetRelayJob(dynamic Notify)
        {

            var parameters = Notify.@params;

            string relayJobId=(string)parameters[0];
            ulong JobID;
            try
            {
                JobID = Convert.ToUInt64(relayJobId, 16);
            }
            catch(Exception e)
            {
                _logger.Debug("Relay job id can't be converted.{0}", e.Message);
                JobID = _jobCounter.Next();
            }
            string PreviousBlockHash = (string)parameters[1];
            if (PreviousBlockHash.EndsWith("000000"))   //TODO:use regex to make it wiser.
                PreviousBlockHash = PreviousBlockHash.HexToByteArray().ReverseByteOrder().ToHexString();

            string coinb2=(string)parameters[3];
            string Version = (string)parameters[5];
            string Bits = (string)parameters[6];
            string CurTime = (string)parameters[7];
            bool CleanJob=(bool)parameters[8];     //must be true.

            //get the Job's Height value.
            string coinbase1 = (string)parameters[2];
            int heightIndex = coinbase1.IndexOf("0000000000000000000000000000000000000000000000000000000000000000ffffffff") + 72 + 2;  //TODO:use regex
            string heightLen = coinbase1.Substring(heightIndex, 2);
            int heightLength = Convert.ToInt32(heightLen, 16);
            string heightbuff = "0x";
            for (int i = 1; i <= heightLength; i++)            //refer to GenerationTrasaction.SignatureScript
            {
                heightbuff += coinbase1.Substring(heightIndex + 2 + 2 * (heightLength - i), 2);   //get the height string in reverse order;
            }
            int Height = Convert.ToInt32(heightbuff, 16);

            //get the merkle tree.The transactions in block template just contain the merkle branch,not all.
            List<BlockTemplateTransaction> merkle_branch = new List<BlockTemplateTransaction>();
            var Transactions = parameters[4];
            foreach (var branch in Transactions)
            {
                merkle_branch.Add(new BlockTemplateTransaction() { Hash = (string)branch });
            }
            var merkleBranch = merkle_branch.ToArray();

            Job relayJob = new Job(JobID,
                Height,
                relayJobId,
                PreviousBlockHash,
                coinbase1, coinb2,
                merkleBranch, Version,
                Bits,
                CurTime,
                CleanJob,
                _hashAlgorithm);
            _logger.Debug("New relay job created.");
            return relayJob;
        }

        /// <summary>
        /// to be added to _foreignPoolPoller delegate
        /// TODO:make this method thread safe.
        /// </summary>
        /// <param name="stats"></param>
        private void pollForeignPool(object stats)
        {
            string FgnPoolNote = null;
            lock (_pollerLock)
            {
                FgnPoolNote = ReceiveNotification();

                if (string.IsNullOrEmpty(FgnPoolNote))
                {
                    //here I use the convention of the original author.
                    _getForeignMsg.Change(2000, Timeout.Infinite);
                    return;
                }

                var lines = Regex.Split(FgnPoolNote, @"\r?\n|\r");

                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line)||!line.EndsWith("}")||!line.StartsWith("{"))
                        continue;
                    else
                    {
                        string method = null;
                        dynamic ForeignPoolRequest;
                        try
                        {
                            ForeignPoolRequest = JsonConvert.DeserializeObject(line);
                            method = (string)ForeignPoolRequest.method;
                            switch (method)
                            {
                                case null:
                                    break;
                                case "mining.set_difficulty":
                                    {
                                        var ExDiff = ForeignPoolRequest.@params;
                                        _relayManager.ExternalDiff = (double)ExDiff[0];
                                        _logger.Debug("Foreign pool diff received:{0}.", _relayManager.ExternalDiff);
                                        break;
                                    }
                                case "mining.notify":
                                    {
                                        var relayJob = GetRelayJob(ForeignPoolRequest);
                                        _jobTracker.Add(relayJob);
                                        CreateAndBroadcastNewJob(false);    //this method can reset the block poller timer and idle job timer,so if I changes 
                                        //the IsRelaying status dynamically, the logic will go right.

                                        //use the block difficulty to calculate the profit.
                                        if (_relayManager.NetworkDiff == 0)
                                        {
                                            decimal globalDiff = _daemonClient.GetDifficulty();
                                            _relayManager.SetNetworkDiff(globalDiff);
                                        }
                                        else
                                        {
                                            //if global diff changes at every 2016 blocks,set new NetworkDiff.
                                            _relayManager.SetNetworkDiff(_jobTracker.Current.Height % 2016 == 1 ? _daemonClient.GetDifficulty() : _relayManager.NetworkDiff);
                                        }
                                        IJob prev;
                                        if (_jobTracker.Current.Height != ((prev = _jobTracker.Get(_jobTracker.Current.Id - 1)) != null ?
                                            prev : _jobTracker.Current).Height)
                                        {
                                            //calc profit for every block.
                                            decimal revenue = _relayManager.CalcRoundRevenue((ulong)(_jobTracker.Current.Height - 1));
                                            _shareManager.PersistBlock(revenue);
                                            _relayManager.ResetBlockShare();
                                        }
                                        break;
                                    }
                                case "client.get_version":
                                    {
                                        JsonResponse getVersionResponse = new JsonResponse();
                                        getVersionResponse.Id = ForeignPoolRequest.Id;
                                        getVersionResponse.Result = "cgminer/3.7.2";
                                        _relayManager.SendResponse(getVersionResponse);
                                        break;
                                    }
                                case "client.reconnect":
                                    break;
                                case "client.show_message":
                                    {
                                        string Msg = null;
                                        var message = ForeignPoolRequest.@params;
                                        Msg = (string)message[0];
                                        if (!string.IsNullOrEmpty(Msg))
                                            _logger.Information("show_message from foreign pool:{0}.", Msg);
                                        break;
                                    }
                                case "mining.set_extranonce":                //add function to broardcast new extranonce to miners
                                    {
                                        var parameters = ForeignPoolRequest.@params;
                                        string xNonce1 = null;
                                        string x2SizeString = null;
                                        xNonce1 = (string)parameters[0];
                                        x2SizeString = (string)parameters[1];
                                        if (xNonce1 != null)
                                        {
                                            _relayManager.extraNonce1 = xNonce1;
                                        }
                                        else
                                        {
                                            _logger.Information("no value from foreign pool's set_extranonce");
                                            break;
                                        }
                                        if (x2SizeString != null)
                                        {
                                            _relayManager.extraNonce2Size = Convert.ToUInt32(x2SizeString);
                                            _relayManager.FormatExtraNonce();
                                        }
                                        else
                                        {
                                            _logger.Information("no value from foreign pool's set_extranonce");
                                            break;
                                        }
                                        _logger.Information("Foreign pool set_extranonce:extranonce1:{0},extranonce2size:{1}",
                                            _relayManager.extraNonce1, _relayManager.extraNonce2Size);
                                        break;
                                    }
                                default:
                                    {
                                        _logger.Information("unable to decide received method.");
                                        break;
                                    }
                            }
                        }
                        catch (JsonSerializationException e)
                        {
                            _logger.Debug("Json deserialization error:{0}.", e.Message);
                            continue;
                        }
                        catch (Exception e)
                        {
                            _logger.Debug("Fail to poll foreign pool,exception:{0}", e.Message);
                            continue;
                        }

                        if (method != null)
                        {
                            _getForeignMsg.Change(_relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].refreshInterval, Timeout.Infinite);
                            continue;
                        }

                        //then,try deserializing the message as json response.
                        var ForeignPoolResponse = ForeignPoolRequest;
                        try
                        {
                            switch ((long)ForeignPoolResponse.id)
                            {
                                case (long)RelayManager.RPCMethods.authorize:
                                    {
                                        if (ForeignPoolResponse.result != null)
                                        {
                                            if ((bool)ForeignPoolResponse.result == true)
                                            {
                                                _logger.Information("Successfully authorized:{0}.",
                                                    _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].url);
                                                break;
                                            }
                                            else
                                            {
                                                _logger.Information("Authorization rejected:{0},reason:{1}",
                                                    _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].url,
                                                    ForeignPoolResponse.error.ToString());
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            _logger.Information("pool responce result is null:{0}",
                                                _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].url);
                                            _logger.Debug("error:{0}.", ForeignPoolResponse.error.ToString());
                                            break;
                                        }
                                    }
                                case (long)RelayManager.RPCMethods.subscribe:
                                    {
                                        if (ForeignPoolResponse.result != null)
                                        {
                                            var subscribeResult = ForeignPoolResponse.result;
                                            if (subscribeResult != null)
                                            {
                                                _relayManager.extraNonce1 = (string)subscribeResult[1];
                                                _relayManager.extraNonce2Size = (uint)subscribeResult[2];
                                                _logger.Debug("Foreign pool extra nonce1:{0},extra nonce2 size:{1}.",
                                                    _relayManager.extraNonce1, _relayManager.extraNonce2Size);
                                                _relayManager.FormatExtraNonce();
                                                //Task.Factory.StartNew(() => { BroardcastRelayExtraNonceInfo(); });  //resend extra nonce info.But beware that not all mining software support this.
                                                _logger.Debug("Disconnect all,called by pollForeignPool,on subscribed.");
                                                Task.Factory.StartNew(() => { OnForeignPoolSubscribed(); });  //disconnect all miners.
                                                _relayManager.Authorize();
                                                break;
                                            }
                                            else
                                            {
                                                var subscribeError = (ForeignPoolResponse.error != null) ? ForeignPoolResponse.error : null;
                                                if (subscribeError != null)
                                                {
                                                    _logger.Debug("Subscription error:{0}.", subscribeError.ToString());
                                                    break;
                                                }
                                                else
                                                {
                                                    _logger.Debug("error:no response content returned from pool{0}.",
                                                        _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].url);
                                                    break;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            _logger.Information("Subscription rejected:{0}.",
                                                _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].url);
                                            break;
                                        }
                                    }
                                case (long)RelayManager.RPCMethods.submit:
                                    {
                                        if (ForeignPoolResponse.result != null)
                                        {
                                            if ((bool)ForeignPoolResponse.result == true)
                                            {
                                                _relayManager.AddRoundShare();

                                                _logger.Information("Share successfully submitted:{0}.",
                                                    _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].url);
                                                break;
                                            }
                                            else
                                            {
                                                _logger.Information("Share rejected:{0}.error:{1}.",
                                                    _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].url,
                                                    ForeignPoolResponse.error != null ?
                                                    ForeignPoolResponse.error.ToString() : string.Empty);
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            _logger.Information("Share rejected:{0}.error:{1}.",
                                                    _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].url,
                                                    ForeignPoolResponse.error != null ?
                                                    ForeignPoolResponse.error.ToString() : string.Empty);
                                            if(ForeignPoolResponse.error!=null)
                                            {
                                                /*if(line.Contains("work")&&line.Contains("unknown"))
                                                {
                                                    _jobTracker.Current.CleanJobs = true;
                                                    BroadcastJob(_jobTracker.Current);
                                                }*/
                                                if(line.Contains("authorized")&&line.Contains("not"))
                                                {
                                                    _relayManager.Subscribe();
                                                }
                                            }
                                            break;
                                        }
                                    }
                                default:
                                    {
                                        _logger.Debug("Message received from pool not deserializable:{0}.",
                                            _relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].url);
                                        break;
                                    }
                            }
                        }
                        catch (JsonSerializationException e)
                        {
                            _logger.Debug("Json deserialization error:{0}.", e.Message);
                            continue;
                        }
                        catch (Exception e)
                        {
                            _logger.Debug("Fail to poll foreign pool,exception:{0}", e.Message);
                            continue;
                        }
                    }
                }
            }
            _getForeignMsg.Change(_relayManager.RelayConfig.targetNodes[_relayManager.PoolIndex].refreshInterval, Timeout.Infinite);
            return;
        }

        /// <summary>
        /// Broadcasts to miners.
        /// </summary>
        /// <example>
        /// sample communication: http://bitcoin.stackexchange.com/a/23112/8899
        /// </example>
        private Int32 BroadcastJob(IJob job)
        {
            try
            {
                var count = 0; // number of subscribers to job is sent.

                foreach (var miner in _minerManager.Miners)
                {
                    var success = SendJobToMiner(miner, job);

                    if (success)
                        count++;
                }
                _logger.Debug("Relay job sent,details:\n id:{0}\n height:{1}\n previous hash:{2}\n coinb1:{3}\n coinb2:{4}\n merkle branch:{5}\n version:{6}\n diff:{7}\n nTime:{8}\n cleanjobs:{9}",
                    job.RelayId == null ? job.Id.ToString("x") : job.RelayId,
                    job.Height,
                    job.PreviousBlockHashReversed,
                    job.CoinbaseInitial,
                    job.CoinbaseFinal,
                    job.MerkleTree.Branches,
                    job.Version,
                    job.EncodedDifficulty,
                    job.NTime,
                    job.CleanJobs);
                return count;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Job broadcast failed:");
                return 0;
            }
        }

        private int BroardcastRelayExtraNonceInfo()
        {
            try
            {
                var count = 0; // number of subscribers to job is sent.

                foreach (var miner in _minerManager.Miners)
                {
                    var success = SendNewExtraNonceToMiner(miner);

                    if (success)
                        count++;
                }
                _logger.Debug("Relay extranonce sent to {0} miners.", count);
                return count;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Relay extranonce broadcast failed:");
                return 0;
            }
        }


        private bool SendNewExtraNonceToMiner(IMiner miner)
        {
            if (miner is IGetworkMiner) // only stratum miners needs to be submitted new jobs.
                return false;

            var stratumMiner = (IStratumMiner)miner;

            if (!stratumMiner.Authenticated)
                return false;

            if (!stratumMiner.Subscribed)
                return false;

            string bitExtraNonce1In64bit = BitConverter.GetBytes(_relayManager.FormattedXNonce1++).ReverseBytes().ToHexString();
            string extraNonce1 = bitExtraNonce1In64bit.Substring(bitExtraNonce1In64bit.Length - 2 * (_relayManager.TotalExtraNonceSize - (int)_relayManager.FormattedXNonce2Size),
                2 * (_relayManager.TotalExtraNonceSize - (int)_relayManager.FormattedXNonce2Size));

            stratumMiner.SendExtraNonceInfo(extraNonce1,_relayManager.FormattedXNonce2Size);
            stratumMiner.setRelayExtraNonce1(extraNonce1);

            return true;
        }

        private bool SendJobToMiner(IMiner miner, IJob job)
        {
            if (miner is IGetworkMiner) // only stratum miners needs to be submitted new jobs.
                return false;

            var stratumMiner = (IStratumMiner) miner;

            if (!stratumMiner.Authenticated)
                return false;

            if (!stratumMiner.Subscribed)
                return false;

            stratumMiner.SendJob(job);

            return true;
        }

        private void OnMinerAuthenticated(object sender, EventArgs e)
        {
            var miner = ((MinerEventArgs)e).Miner;

            if (miner == null)
                return;

            if (_jobTracker.Current != null)    // if we have a valid job,
            {
                bool originalCleanJobs = _jobTracker.Current.CleanJobs;
                _jobTracker.Current.CleanJobs = true;
                SendJobToMiner(miner, _jobTracker.Current); // send it to newly connected miner.
                _jobTracker.Current.CleanJobs = originalCleanJobs;
            }
        }

        /// <summary>
        /// this event handler is used to receive response as soon as the foreign pool stratum method sends a request to foreign pool.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnForeignPoolRequestSent(object sender,EventArgs e)
        {
            _getForeignMsg.Change(0, Timeout.Infinite);
        }

        private void OnForeignPoolSubscribed()
        {
            var handler = ForeignPoolSubscribed;
            if(handler!=null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnRelayingStopped()
        {
            var handler = RelayingStopped;
            if(handler!=null)
            {
                handler(this._relayManager, EventArgs.Empty);
            }
        }

        private void _onRelayStatusChanged(object sender,EventArgs e)
        {
            if(RelayManager.IsRelaying)
            {
                _blockPollerTimer.Change(Timeout.Infinite, Timeout.Infinite);
                if (_jobTracker.Current != null)
                {
                    _jobTracker.Current.MerkleTree.Branches.Clear();
                    _jobTracker.Current.CleanJobs = true;
                    BroadcastJob(_jobTracker.Current);
                }
                _getForeignMsg.Change(0, Timeout.Infinite);
            }
            else
            {
                _logger.Debug("Disconnect all when stop relaying, called by _onRelayStatusChanged().");
                Task.Factory.StartNew(() => { OnRelayingStopped(); });
                _getForeignMsg.Change(Timeout.Infinite, Timeout.Infinite);
                _relayManager.FormattedXNonce2Size = 0;    //share manager use it to check if new shares are relay shares.
                CreateAndBroadcastNewJob(false);
            }
        }

        private void _onUpstreamPoolIdle(object sender,EventArgs e)
        {
            return;   //Add some necessary logic when it's necessary. e.g. when the upstream pool try to ban us.
        }

        private void _onUpstreamSwitched(object sender,EventArgs arg)
        {
            try
            {
                _jobTracker.Current.CleanJobs = true;
                CreateAndBroadcastNewJob(false);
            }
            catch(Exception e)
            {
                _logger.Debug("Exception occured when sending cleanjob==true job to miners after upstream pool switched,{0}.",
                    e.Message);
            }
        }
    }
}
