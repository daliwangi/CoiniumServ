using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Security.Cryptography;
using CoiniumServ.Utils.Commands;
using CoiniumServ.Utils.Helpers;
using CoiniumServ.Shares;
using CoiniumServ.Jobs;
using CoiniumServ.Daemon.Responses;
using CoiniumServ.Transactions;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;
using AustinHarris.JsonRpc;
using Serilog;
using CoiniumServ.Server.Mining.Stratum.Responses;
using CoiniumServ.Utils.Extensions;
using CoiniumServ.Configuration;

namespace CoiniumServ.Relay
{

    //TODO:auto relay according to current hashrate
    public class RelayManager : IRelayManager
    {
        #region private field
        static private bool _IsRelaying;
        static private bool _manualSwitchKey;
        private string _extraNonce1 = "ffff0000";
        private UInt32 _extraNonce2Size = 4;
        private IRelayConfig _relayConfig;
        private ILogger _logger;
        private Socket RelaySocket;
        private byte[] _buffer = new byte[1024 * 1024];

        //the foreign pool difficulty
        private double _externalDifficulty;

        //total number of shares of current block.
        private double _currentBlockShare;

        //network difficulty,can be used to calculate share profit.
        private decimal _NetworkDiff;

        private int PoolID = 0;

        private List<IPAddress> TargetAddresses;

        private object setTargetLock = new object();
        private object connectPoolLock = new object();

        private InactivePool _PoolInactive;

        private Timer _checkStatusTimer;

        private Timer _checkSwitchPoolRequest;

        private bool _previousStatus = false;

        

        #endregion

        #region events
        //use this event to call job manager's method.
        public event EventHandler ForeignPoolRequestSent;

        public event EventHandler RelayStatusChanged;

        public event EventHandler UpstreamPoolIdle;

        public event EventHandler UpstreamSwitched;
        #endregion

        #region static property
        static public bool IsRelaying
        {
            get
            {
                return _IsRelaying;
            }
            private set
            {
                _IsRelaying = value;
            }
        }

        static public bool ManualSwitchKey
        {
            get
            {
                return _manualSwitchKey;
            }
            private set
            {
                _manualSwitchKey = value;
            }
        }


        #endregion

        #region properties
        public string extraNonce1    //a string in hex;
        {
            get
            {
                return _extraNonce1;
            }
            set { _extraNonce1 = value; }
        }

        public UInt32 extraNonce2Size
        {
            get { return _extraNonce2Size; }
            set { _extraNonce2Size = value; }
        }

        public int TotalExtraNonceSize { get { return extraNonce1.Length / 2 + (int)extraNonce2Size; } }

        public UInt64 FormattedXNonce1 { get; set; }
        public UInt32 FormattedXNonce2Size { get; set; }

        public bool ForeignPoolConnectionEstablished { get { return RelaySocket.Connected; } }
        public byte[] Buffer { get { return _buffer; } }

        public IRelayConfig RelayConfig { get { return _relayConfig; } }

        public double ExternalDiff { get { return _externalDifficulty; } set { _externalDifficulty = value; } }

        //the number of the shares of current block.
        public double BlockShare { get { return _currentBlockShare; } }

        public decimal NetworkDiff { get { return _NetworkDiff; } }

        public int TotalPoolNumber { get { return _relayConfig.targetNodes.ToArray().Length; } }

        public string XNonce1Prefix { get; set; }

        public int PoolIndex { get { return PoolID; } }
        
        #endregion

        #region constructors
        static RelayManager()
        {
            IsRelaying = false;
            _manualSwitchKey = false;
        }

        /*
        public RelayManager(IRelayConfig config)
        {
            _relayConfig = config;
            _logger = Log.ForContext<RelayManager>();
            RelaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _externalDifficulty = 1.0;
        }
         */

        public RelayManager(IConfigManager configManager)
        {
            _relayConfig=configManager.RelayConfig;
            _logger = Log.ForContext<RelayManager>();
            RelaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _externalDifficulty = 1.0;
            TargetAddresses = new List<IPAddress>();

            RelayStatusChanged += onRelayStatusChanged;
        }

        #endregion

        #region static methods
        static public void SetRelay(bool value)
        {
            IsRelaying = value;
        }

        static public void ManualSwitchUpstream()
        {
            ManualSwitchKey = true;
        }
        #endregion

        #region relay stratum methods


        /// <summary>
        /// "mining.submit"
        /// </summary>
        /// <param name="relayShare"></param>
        public bool MiningSubmit(IRelayShare relayShare)
        {
            JsonRequest submitShare = new JsonRequest();
            submitShare.Id = RPCMethods.submit;
            submitShare.Method = "mining.submit";
            submitShare.Params = relayShare;
            string submit = JsonConvert.SerializeObject(submitShare);
            submit += "\n";                       //"\n" must be appended to the end of request string.

            //use the first pool by default.TODO:choose pool to relay to at runtime.
            if(RelaySocket.Connected)
            {
                sendRequest(submitShare);
            }
            else
            {
                IPEndPoint target = setTarget(PoolID);
                if (target == null)
                {
                    _logger.Debug("Connection to pool no.{0} failed,try another pool.", PoolID);
                    bool SwitchSuccess = SwitchUpstream();
                    if (SwitchSuccess)
                    {
                        _logger.Debug("Connected to new pool,no.{0},resubscribe.", PoolID);
                        Subscribe();
                    }
                    return false;
                }
                else
                {
                    Subscribe();
                    return false;
                }
            }
            
            if(ForeignPoolRequestSent!=null)
            {
                ForeignPoolRequestSent(this, EventArgs.Empty);
            }
            return true;
        }

        /// <summary>
        /// mining.authorize
        /// </summary>
        public bool Authorize()
        {
            JsonRequest authorize = new JsonRequest();
            authorize.Id = (long)RPCMethods.authorize;
            authorize.Method = "mining.authorize";
            authorize.Params = new List<object> { _relayConfig.targetNodes[PoolID].workerId, _relayConfig.targetNodes[PoolID].password };

            if(RelaySocket.Connected)
            {
                sendRequest(authorize);
            }
            else
            {
                IPEndPoint target = setTarget(PoolID);
                if (target == null)
                {
                    _logger.Debug("Connection to pool no.{0} failed,try another pool.", PoolID);
                    bool SwitchSuccess = SwitchUpstream();
                    if (SwitchSuccess)
                    {
                        _logger.Debug("Connected to new pool,no.{0},resubscribe.", PoolID);
                        Subscribe();
                    }
                    return false;
                }
                else
                {
                    Subscribe();
                    return false;
                }
            }
            
            if(ForeignPoolRequestSent!=null)
            {
                ForeignPoolRequestSent(this, EventArgs.Empty);
            }
            return true;
        }

        /// <summary>
        /// In "mining.subscribe"，dynamic extranonce1 and extranonce2size is set.
        /// </summary>
        public bool Subscribe()
        {
            JsonRequest Subscribe = new JsonRequest();
            Subscribe.Id = (long)RPCMethods.subscribe;
            Subscribe.Method = "mining.subscribe";
            Subscribe.Params = new List<object> (){"cgminer/3.7.2"};    //if not provided with the software info,relay may be rejected.

            IPEndPoint target = setTarget(PoolID);
            if (target == null)
            {
                _logger.Debug("Connection to pool no.{0} failed,try another pool.", PoolID);
                bool SwitchSuccess = SwitchUpstream();
                if (SwitchSuccess)
                {
                    _logger.Debug("Connected to new pool,no.{0},resubscribe.", PoolID);
                }
                else
                {
                    return false;
                }
            }

            sendRequest(Subscribe);

            if (ForeignPoolRequestSent != null)
            {
                ForeignPoolRequestSent(this, EventArgs.Empty);
            }
            else
                _logger.Debug("No handler set to receive incoming response from foreign pool.");

            return true;
        }


        /// <summary>
        /// add an additional byte to the original extra nonce,and reduce the length of extranonce2size by 1;
        /// </summary>
        /// <param name="original">original subscribe response.</param>
        /// <returns>the formatted new response to send</returns>
        public void FormatExtraNonce()
        {
            var randGen = RandomNumberGenerator.Create();                      //use a random id to distinguish different miner;
            var randBytes = new Byte[2];
            randGen.GetNonZeroBytes(randBytes);
            string StratumId = randBytes.ToHexString();
            string xNonce1 = extraNonce1 + StratumId;
            if(xNonce1.Length>16)
            {
                XNonce1Prefix=xNonce1.Substring(0,xNonce1.Length-16);
                xNonce1 = xNonce1.Substring(xNonce1.Length - 16, 16);
                _logger.Debug("xNonce1 prefix is:{0},xNonce1 is:{1}", XNonce1Prefix, xNonce1);
            }
            else
            {
                XNonce1Prefix = string.Empty;
            }
            FormattedXNonce1 = Convert.ToUInt64(xNonce1, 16);
            FormattedXNonce2Size = extraNonce2Size - 2;
            _logger.Debug("Formated extra nonce1:{0},extra nonce2 size:{1}.", FormattedXNonce1, FormattedXNonce2Size);
            return;
        }

        #endregion

        #region public methods
        /// <summary>
        /// must use receive() method to get the foreigh pool notification
        /// </summary>
        public void RecvForeignPool()
        {
            int totalread = 0;
            int read = 0;
            Array.Clear(_buffer, 0, _buffer.Length);   //reset the buffer to empty
            //wait 15 seconds for the response.
            for(int i=0;i<30;i++)
            {
                if (RelaySocket.Available == 0)
                    Thread.Sleep(500);
                else
                    break;
            }

            if (RelaySocket.Available > 0)
            {
                _PoolInactive.Reset();
                SocketError errorcode;                
                try
                {
                    do
                    {
                        read = RelaySocket.Receive(_buffer, totalread, RelaySocket.Available, SocketFlags.None, out errorcode);
                        totalread += read;
                        if (_buffer[totalread - 1] != '}' && _buffer[totalread - 2] != '}' && Buffer[totalread - 3] != '}' && Buffer[totalread - 4] != '}')
                        {
                            for (int i = 0; i < 10; i++)  //wait for 5 seconds for the remaining part.
                            {
                                if (RelaySocket.Available == 0)
                                    Thread.Sleep(500);
                                else
                                    break;
                            }
                        }
                    } while (RelaySocket.Available > 0);
                    _logger.Debug("Data Received from {0}, it reads:{1}", RelaySocket.RemoteEndPoint.ToString(), _buffer.ToEncodedString().Trim().Trim('\0'));
                }
                catch (SocketException e)
                {
                    _logger.Debug("Socket exception when receiving from foreign pool,error:{0},message:{1}.", e.ErrorCode, e.Message);
                    return;
                }
                catch(ArgumentOutOfRangeException e)
                {
                    _logger.Debug("Argument out of range when receiving from foreign pool,actual value:{0},message:{1},error data:{2},param name:{3}.",
                        e.ActualValue, e.Message, e.Data.ToString(), e.ParamName);
                    if (e.ParamName.Contains("size"))
                    {
                        RelaySocket.ReceiveBufferSize = 1024 * 1024;
                    }
                    return;
                }
                catch (Exception e)
                {
                    _logger.Debug("Exception when receiving from foreign pool,message:{0}.", e.Message);
                    return;
                }
                _logger.Debug("Total {0} bytes received.", totalread);
                if (errorcode != SocketError.Success)
                    _logger.Debug("error code is:{0}", errorcode.ToString());
            }
            else
            {
                _logger.Debug("Nothing to read from foreign pool:{0}",
                    RelaySocket.RemoteEndPoint != null ?
                    RelaySocket.RemoteEndPoint.ToString() :
                    "null remote end point.");
                _PoolInactive.RecentTime=TimeHelpers.NowInUnixTimestamp();
                if(_PoolInactive.FailedPolls++==0)
                {
                    _PoolInactive.StartTime=_PoolInactive.RecentTime;
                }
                else
                {
                    if (_PoolInactive.RecentTime - _PoolInactive.StartTime > 240 || _PoolInactive.FailedPolls >= 80)  //4 minutes or 240 times，time interval is 1 sec.
                    {
                        try
                        {
                            RelaySocket.Shutdown(SocketShutdown.Both);
                            RelaySocket.Disconnect(false);
                            RelaySocket.Dispose();
                            RelaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        }
                        catch (SocketException)
                        {
                            if (RelaySocket.RemoteEndPoint == null)
                            {
                                RelaySocket.Dispose();
                                RelaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                            }
                        }
                        Subscribe();
                    }
                }
                return;
            }
        }

        public bool SendResponse(JsonResponse response)
        {
            string aResponse = JsonConvert.SerializeObject(response);   //check SerializeObject() that the "\n" is appended to the end of the string.
            aResponse = aResponse + "\n";
            byte[] buffer = Encoding.ASCII.GetBytes(aResponse);

            var byteRemaining = buffer.Length;
            try
            {
                while (byteRemaining > 0)
                {
                    var byteSent = RelaySocket.Send(buffer, buffer.Length, 0);
                    byteRemaining -= byteSent;
                }
            }
            catch (SocketException socketException)
            {
                _logger.Error(socketException, "send");
                return false;
            }
            catch (Exception e)
            {
                _logger.Error(e, "send");
                return false;
            }
            return true;
        }

        public void AddRoundShare()
        {
            _currentBlockShare += ExternalDiff;
        }

        public void ResetBlockShare()
        {
            _currentBlockShare = 0;
        }

        public void SetNetworkDiff(decimal NewDiff)
        {
            _NetworkDiff = NewDiff;
        }

        public decimal CalcRoundRevenue(UInt64 height)
        {
            ulong n=height/210000UL;
            decimal CreationFee=50;
            while (n-- > 0)
            {
                CreationFee /= 2;
            }
            _logger.Debug("Current Creation Fee is {0}.", CreationFee);
            decimal revenue = ((decimal)_currentBlockShare / NetworkDiff) * CreationFee;
            return revenue;
        }
        
        public void Initialize()
        {
            _checkStatusTimer = new Timer(checkStat, null, 2000, Timeout.Infinite);
            _checkSwitchPoolRequest = new Timer(checkSwitchPool, null, 5000, Timeout.Infinite);
        }

        public void OnUpstreamPoolIdle(object sender, EventArgs e)
        {
            var handler = UpstreamPoolIdle;
            if(handler!=null)
            {
                handler(sender, EventArgs.Empty);
            }
            bool SwitchSuccess = SwitchUpstream();
            if (SwitchSuccess)
            {
                _logger.Debug("Connected to new pool,no.{0},resubscribe.", PoolID);
                Subscribe();
            }
        }

       
        #endregion

        #region private methods

        /// <summary>
        /// If disconnected from foreign pool, try to connect to another ip of the pool.
        /// </summary>
        /// <param name="NodeIndex"></param>
        /// <returns></returns>
        private IPEndPoint setTarget(int NodeIndex)
        {
            lock (setTargetLock)
            {
                string host;
                int TargetPort;
                if (!RelaySocket.Connected)
                {
                    if (RelaySocket.RemoteEndPoint == null)
                    {
                        host = _relayConfig.targetNodes[NodeIndex].url;
                        IPHostEntry TargetEntry;
                        try
                        {
                            TargetEntry = Dns.GetHostEntry(host);
                        }
                        catch(SocketException e)
                        {
                            _logger.Debug("Dns parsing failed.error:{0}", e.Message, e.ErrorCode);
                            return null;
                        }
                        catch(Exception e)
                        {
                            _logger.Debug("Unknown error while DNS parsing,{0}.", e.Message);
                            return null;
                        }
                        TargetAddresses.AddRange(TargetEntry.AddressList);
                        IPAddress[] TargetAddress = TargetEntry.AddressList;

                        _logger.Debug("DNS parsing result gets {0} IP addresses.", TargetAddress.Length);
                        TargetPort = (int)_relayConfig.targetNodes[NodeIndex].port;

                        for (int i = 0; i < TargetAddress.Length; i++)
                        {
                            IPEndPoint target = new IPEndPoint(TargetAddress[i], TargetPort);
                            try
                            {
                                RelaySocket.Connect(target);
                                if (RelaySocket.Connected)
                                {
                                    _logger.Debug("connected to remote pool.IP:{0}.", ((IPEndPoint)(RelaySocket.RemoteEndPoint)).ToString());
                                    return target;
                                }
                                else
                                {
                                    continue;
                                }
                            }
                            catch (SocketException e)
                            {
                                if (i != TargetAddress.Length - 1)
                                {
                                    _logger.Debug("Connecting to address {0} failed,reason{1}.try next.", i + 1, e.Message);
                                    continue;
                                }
                                else
                                {
                                    _logger.Debug("Connecting to pool No.{0} failed.error:{1},reason:{2}.", NodeIndex + 1, e.ErrorCode, e.Message);
                                    return null;
                                }
                            }
                            catch (Exception e)
                            {
                                _logger.Debug("Connecting to pool No.{0} failed.reason:{2}.", NodeIndex + 1, e.Message);
                                return null;
                            }
                        }
                        return null;
                    }
                    else
                    {
                        IPEndPoint CurrentTarget = (IPEndPoint)RelaySocket.RemoteEndPoint;
                        RelaySocket.Shutdown(SocketShutdown.Both);
                        RelaySocket.Close();
                        RelaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            RelaySocket.Connect(CurrentTarget);
                            if (RelaySocket.Connected)
                            {
                                return CurrentTarget;
                            }
                        }
                        catch (SocketException)
                        {
                            _logger.Debug("Reconnecting attempt failed,pool:{0},switching to another ip.", CurrentTarget.Address.ToString());
                        }
                        catch(Exception e)
                        {
                            _logger.Debug("Exception occured when reconnecting,message:{0}.", e.Message);
                        }
                        foreach (var target in TargetAddresses)
                        {
                            if (target != CurrentTarget.Address)
                            {
                                try
                                {
                                    var targetEndPoint = new IPEndPoint(target, _relayConfig.targetNodes[NodeIndex].port);
                                    RelaySocket.Connect(targetEndPoint);
                                    if (RelaySocket.Connected)
                                        return targetEndPoint;
                                    else
                                        continue;
                                }
                                catch (SocketException e)
                                {
                                    _logger.Debug("Socket exception:{0},message:{1},ip:{2},try next ip.", e.ErrorCode, e.Message, target.ToString());
                                    continue;
                                }
                                catch(Exception e)
                                {
                                    _logger.Debug("Exception for IP:{0},message:{1}.", target.ToString(), e.Message);
                                    continue;
                                }
                            }
                            else
                                continue;
                        }
                        _logger.Debug("All available ips have been tried, connection can't be established.");
                        return null;
                    }
                }
                else
                {
                    return (IPEndPoint)RelaySocket.RemoteEndPoint;
                }
            }
        }

        /// <summary>
        /// If can't get connected to the IP provided by setTarget() method, try another pool.
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        private bool SwitchUpstream()
        {
            _externalDifficulty = double.PositiveInfinity;   //block share submitting.
            lock (connectPoolLock)
            {
                IPEndPoint reparsedTarget = null;
                int PoolsTried = 0;
                try
                {
                    RelaySocket.Shutdown(SocketShutdown.Both);
                    RelaySocket.Disconnect(false);
                }
                catch(SocketException e)
                {
                    _logger.Debug("Shuting down current upstrem pool connection failed with socket exception.Msg:{0},code:{1}.", 
                        e.Message,e.ErrorCode);
                }
                RelaySocket.Dispose();
                RelaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                do
                {
                    reparsedTarget = setTarget((++PoolID) < TotalPoolNumber ? PoolID : (PoolID = 0));   //change to another upstream pool.
                    PoolsTried++;
                    if (reparsedTarget != null)
                    {                        
                        return true;                        
                    }
                } while (reparsedTarget == null && PoolsTried < TotalPoolNumber);
                _logger.Debug("Can't connect to foreign pools, stop relaying.");

                //TODO:change to solo mining or allert 
                RelayManager.IsRelaying = false;
                return false;
            }
        }

        private bool sendRequest(JsonRequest request)
        {
            string authorizeRequest = JsonConvert.SerializeObject(request);   //check SerializeObject() that the "\n" is appended to the end of the string.
            authorizeRequest += "\n";
            byte[] buffer = Encoding.UTF8.GetBytes(authorizeRequest);

            var byteRemaining = buffer.Length;
            if (RelaySocket.Connected)
            {
                try
                {
                    while (byteRemaining > 0)
                    {
                        var byteSent = RelaySocket.Send(buffer,0, buffer.Length, SocketFlags.None);
                        byteRemaining -= byteSent;
                    }
                }
                catch (SocketException socketException)
                {
                    _logger.Error(socketException, "send");
                    return false;
                }
                catch (Exception e)
                {
                    _logger.Error(e, "send");
                    return false;
                }
                _logger.Debug("request sent:{0}", buffer.ToEncodedString());
                return true;
            }
            else
            {
                _logger.Debug("Connection to foreign pool is not established,aborting request sending.");
                    return false;
            }
        }

        private void checkStat(object status)
        {
            if(RelayManager.IsRelaying!=_previousStatus)
            {
                _previousStatus = RelayManager.IsRelaying;
                OnRelayStatusChanged();
                if (!RelayManager.IsRelaying)
                {
                    extraNonce1 = "ffff0000";     //disconnect all and stop polling in job manager,
                                                  //dispose and recreate socket in relay manager 
                }
            }
            _checkStatusTimer.Change(2000, Timeout.Infinite);
        }

        private void checkSwitchPool(object status)
        {
            if (ManualSwitchKey == true)
            {
                bool result = SwitchUpstream();
                if(result==true)
                {
                    _logger.Debug("Connected to new pool,no.{0},resubscribe.", PoolID);
                    Subscribe();
                    ManualSwitchKey = false;
                }
                else
                {
                    RelayManager.IsRelaying = false;
                    //TODO:stop automatically beginning to relay here:
                }
            }
        }

        private void OnRelayStatusChanged()
        {
            var handler = RelayStatusChanged;
            if(handler!=null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnUpstreamSwitched()
        {
            var handler = UpstreamSwitched;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void onRelayStatusChanged(object sender, EventArgs e)
        {
            DisposeAndCreateNewSocket();
        }

        private void DisposeAndCreateNewSocket()
        {
            try
            {
                RelaySocket.Shutdown(SocketShutdown.Both);
                RelaySocket.Disconnect(false);
            }
            catch (SocketException e)
            {
                _logger.Debug("Shuting down current upstrem pool connection failed with socket exception.Msg:{0},code:{1}.",
                    e.Message, e.ErrorCode);
            }
            RelaySocket.Dispose();
            RelaySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        #endregion

        #region Internal types

        public enum RPCMethods
        {
            subscribe=1,
            authorize=2,
            submit=4,
        }

        internal struct InactivePool
        {
            public int FailedPolls;
            public int StartTime;
            public int RecentTime;
            public void Reset()
            {
                FailedPolls = 0;
                StartTime = 0;
                RecentTime = 0;
            }
        }
#endregion
    }
}

