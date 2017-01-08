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
using System.Collections.Generic;
using System.Globalization;
using CoiniumServ.Utils.Helpers;

namespace CoiniumServ.Persistance.Layers.Hybrid
{
    public partial class HybridStorage
    {
        public IDictionary<string, double> GetHashrateData(int since)
        {
            var hashrates = new Dictionary<string, double>();

            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return hashrates;

                var key = string.Format("{0}:hashrate", _coin);
                string[] results={null};

                lock(_redisLock)
                {
                    results = _redisProvider.Client.ZRangeByScore(key, since, double.PositiveInfinity);
                    if (results == null)
                        return hashrates;
                }

                foreach (var result in results)
                {
                    var data = result.Split(':');
                    var share = double.Parse(data[0].Replace(',', '.'), CultureInfo.InvariantCulture);
                    var worker = data[1].Substring(0, data[1].Length - 8);

                    if (!hashrates.ContainsKey(worker))
                        hashrates.Add(worker, 0);

                    hashrates[worker] += share;
                }
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while getting hashrate data: {0:l}", e.Message);
            }

            return hashrates;
        }

        public void DeleteExpiredHashrateData(int until)
        {
            if (!IsEnabled || !_redisProvider.IsConnected)
                return;

            lock (_redisLock)
            {
                try
                {
                    var key = string.Format("{0}:hashrate", _coin);
                    _redisProvider.Client.ZRemRangeByScore(key, int.MinValue, until);
                }
                catch (Exception e)
                {
                    _logger.Error("An exception occured while deleting expired hashrate data: {0:l}", e.Message);
                }
            }
        }

        /// <summary>
        /// if the query worker id is contained in the redis tuple,it is accounted.
        /// </summary>
        /// <param name="since"></param>
        /// <param name="workerId"></param>
        /// <returns></returns>
        public UInt64 CalculateWorkerHashRate(int since, string workerId)
        {
            double workerHashrateData = 0;
            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return 0;

                string[] allHashrateTuples={null};
                lock (_redisLock)
                {
                    var workerHashrateKey = string.Format("{0}:hashrate", _coin);
                    allHashrateTuples = _redisProvider.Client.ZRangeByScore(workerHashrateKey, since, double.PositiveInfinity);
                    if (allHashrateTuples == null)
                    {
                        _logger.Debug("Can't get hashrate data for user.");
                        return 0;
                    }
                }
                foreach (var tuple in allHashrateTuples)
                {
                    if (tuple.Contains(workerId))
                    {
                        var data = tuple.Split(':');
                        var difficulty = double.Parse(data[0].Replace(',', '.'), CultureInfo.InvariantCulture);
                        workerHashrateData += difficulty;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while calculating worker hashrate: {0:l}", e.Message);
            }
            var result = Convert.ToUInt64(workerHashrateData / 300);
            return result;
        }

        public Dictionary<string,ulong> GetUserHashrateData(string UserName)
        {
            var Data = new Dictionary<string, ulong>();
            lock (_redisLock)
            {
                var entry = string.Format("{0}:{1}:hashrate", UserName, _coin);
                Dictionary<string, string> redisData=new Dictionary<string,string>();
                redisData = _redisProvider.Client.HGetAll(entry);
                foreach (var pair in redisData)
                {
                    var addedTime = int.Parse(pair.Value.Split(':')[1]);
                    if (TimeHelpers.NowInUnixTimestamp() - addedTime > 86400)   //check for obsolete value in case that the miner has stopped mining.
                    {
                        _redisProvider.Client.HSet(entry,pair.Key,string.Format("0:{0}",TimeHelpers.NowInUnixTimestamp()));   //using async
                        Data[pair.Key] = 0ul;
                        continue;
                    }
                    else
                    {
                        Data[pair.Key] = ulong.Parse(pair.Value.Split(':')[0]);
                    }
                }
            }
            return Data;
        }
    }
}
