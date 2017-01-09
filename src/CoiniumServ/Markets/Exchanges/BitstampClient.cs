﻿#region License
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
using System.Linq;
using System.Threading.Tasks;
using Serilog;

namespace CoiniumServ.Markets.Exchanges
{
    public class BitstampClient : ExchangeApi, IBitstampClient
    {
        private const string ApiBase = "https://www.bitstamp.net/api/v2/ticker/";
        private const string PublicApiEndpoint = "btcusd";

        private readonly ILogger _logger;

        public BitstampClient()
        {
            _logger = Log.ForContext<BitstampClient>();
        }

        public async Task<IList<IMarketData>> GetMarkets()
        {
            var list = new List<IMarketData>();

            var data = await Request(ApiBase, PublicApiEndpoint);

            try
            {
                var entry = new MarketData
                {
                    Exchange = Exchange.Poloniex,
                    MarketCurrency = "USD",
                    BaseCurrency = "BTC",
                    Ask = double.Parse(data.ask),
                    Bid = double.Parse(data.bid),
                    VolumeInMarketCurrency = double.Parse(data.volume) * double.Parse(data.last),
                    VolumeInBaseCurrency = double.Parse(data.volume),
                };
                list.Add(entry);
            }
            catch (Exception e)
            {
                _logger.Error(e.Message);
            }

            return list;
        }
    }
}
