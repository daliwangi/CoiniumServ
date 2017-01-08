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
using CoiniumServ.Pools;
using CoiniumServ.Relay;
using CoiniumServ.Server.Mining.Stratum;
using CoiniumServ.Shares;
using NSubstitute;
using Should.Fluent;
using Xunit;

namespace CoiniumServ.Tests.Server.Mining.Stratum
{
    public class StratumServiceTests
    {
        private readonly IRelayManager _relayManager;
        private readonly IShareManager _shareManager;
        private readonly IPoolConfig _poolConfig;
        private readonly StratumContext _stratumContext;

        public StratumServiceTests()
        {
            _shareManager = Substitute.For<IShareManager>();
            _poolConfig = Substitute.For<IPoolConfig>();

            var miner = Substitute.For<IStratumMiner>();
            _stratumContext = Substitute.For<StratumContext>(miner);

            _relayManager = Substitute.For<IRelayManager>();
            _relayManager.FormattedXNonce1.Returns(2190496396);
            _relayManager.FormattedXNonce2Size.Returns(2u);
        }

        [Fact]
        public void MiningSubscribe_WithOutParameters_ShouldEqual()
        {
            _poolConfig.Coin.Name.Returns("zero-params");
            _stratumContext.Miner.ExtraNonce.Returns("00000000");
            var service = new StratumService(_poolConfig, _shareManager, _relayManager);

            const string request = @"{ 'id' : 1, 'method' : 'mining.subscribe', 'params' : [] }";
            const string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":[[\"mining.set_difficulty\",\"0\",\"mining.notify\",\"0\"],\"00000000\",4],\"id\":1}";

            var task = JsonRpcProcessor.Process(_poolConfig.Coin.Name, request,_stratumContext);
            task.Wait();

            task.Result.Should().Equal(expectedResult);
        }

        [Fact]
        public void MiningSubscribe_WithSignature_ShouldEqual()
        {
            _poolConfig.Coin.Name.Returns("signature");
            _stratumContext.Miner.ExtraNonce.Returns("00000000");
            var service = new StratumService(_poolConfig, _shareManager, _relayManager);

            const string request = @"{ 'id' : 1, 'method' : 'mining.subscribe', 'params' : [ 'cgminer/3.7.2' ] }";
            const string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":[[\"mining.set_difficulty\",\"0\",\"mining.notify\",\"0\"],\"00000000\",4],\"id\":1}";

            var task = JsonRpcProcessor.Process(_poolConfig.Coin.Name, request, _stratumContext);
            task.Wait();

            task.Result.Should().Equal(expectedResult);
        }

        [Fact]
        public void MiningSubscribe_WithSessionId_ShouldEqual()
        {
            _poolConfig.Coin.Name.Returns("session");
            _stratumContext.Miner.ExtraNonce.Returns("00000000");
            var service = new StratumService(_poolConfig, _shareManager, _relayManager);

            const string request = @"{ 'id' : 1, 'method' : 'mining.subscribe', 'params' : [ 'cgminer/3.7.2', '02000000b507a8fd1ea2b7d9cdec867086f6935228aba1540154f83930377ea5a2e37108' ] }";
            const string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":[[\"mining.set_difficulty\",\"0\",\"mining.notify\",\"0\"],\"00000000\",4],\"id\":1}";

            var task = JsonRpcProcessor.Process(_poolConfig.Coin.Name, request, _stratumContext);
            task.Wait();

            task.Result.Should().Equal(expectedResult);
        }

        [Fact]
        public void Test_For_MiningSubmit()
        {
            string user = "144nHpi3z7uweNxbjb7BvDJph1WG6Sr3Uh";
            string jobId = "B41zjdnjqvbbydaliwangi";
            string extraNonce2 = "5e08";
            string nTime = "5567288c";
            string nonce = "5f37dd76";
            string username = "daliwangi.worker1";

            var stratumminer = Substitute.For<IStratumMiner>();
            stratumminer.ExtraNonce.Returns("8290528f");
            var context = new StratumContext(stratumminer);




            

            //the miner's ExtraNonce has been changed to a hex string
            string ForeignExtraNonce2 = context.Miner.ExtraNonce.Substring(context.Miner.ExtraNonce.Length - 4, 4) + extraNonce2;


            //change extraNonce2 from miner to 4 bytes,and process it as a new share
            uint byteNumToAppend = 4 - _relayManager.FormattedXNonce2Size;
            string xNonce2ToVerify = string.Empty;
            if (byteNumToAppend > 0)
                xNonce2ToVerify = context.Miner.ExtraNonce.Substring(context.Miner.ExtraNonce.Length - 2 * (int)byteNumToAppend, 2 * (int)byteNumToAppend) + extraNonce2;
            else
                xNonce2ToVerify = extraNonce2;

            xNonce2ToVerify.Should().Equal("528f5e08");
        }
    }
}
