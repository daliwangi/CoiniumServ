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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using CoiniumServ.Algorithms;
using CoiniumServ.Coin.Coinbase;
using CoiniumServ.Cryptology.Merkle;
using CoiniumServ.Daemon.Responses;
using CoiniumServ.Shares;
using CoiniumServ.Transactions;
using CoiniumServ.Transactions.Utils;
using CoiniumServ.Utils.Extensions;
using CoiniumServ.Utils.Helpers;
using CoiniumServ.Utils.Numerics;
using Gibbed.IO;

namespace CoiniumServ.Jobs
{
    public class Job : IJob
    {
        public ulong Id { get; private set; }

        public string RelayId { get; private set; }

        public int Height { get; private set; }

        public string PreviousBlockHash { get; private set; }

        public string PreviousBlockHashReversed { get; private set; }

        public string CoinbaseInitial { get; private set; }

        public string CoinbaseFinal { get; private set; }

        public string Version { get; private set; }

        public string EncodedDifficulty { get; private set; }

        public BigInteger Target { get; private set; }

        public double Difficulty { get; private set; }

        public string NTime { get; private set; }

        public bool CleanJobs { get; set; }

        public int CreationTime { get; private set; }

        public IHashAlgorithm HashAlgorithm { get; private set; }

        public IBlockTemplate BlockTemplate { get; private set; }

        public IGenerationTransaction GenerationTransaction { get; private set; }

        public IMerkleTree MerkleTree { get; private set; }

        /// <summary>
        /// List of shares submitted by miners in order to determine duplicate shares.
        /// </summary>
        private readonly IList<UInt64> _shares;

        public Job(ulong jobNumber,int height, string id, string previousBlockHash, string coinbaseInitial, string coinbaseFinal,
            BlockTemplateTransaction[] Transactions, string version, string encodedDiff, string nTime, bool cleanJobs ,
            IHashAlgorithm hashAlgorithm)
        {
            Id = jobNumber;
            Height = height;
            RelayId = id;
            PreviousBlockHash = previousBlockHash;
            PreviousBlockHashReversed = PreviousBlockHash.HexToByteArray().ReverseByteOrder().ToHexString();
            CoinbaseInitial = coinbaseInitial;
            CoinbaseFinal = coinbaseFinal;
            MerkleTree = new MerkleTree(Transactions.GetHashList(true),true);   //don't reverse bytes buffer here in GetHashList()
            Version = version;
            EncodedDifficulty = encodedDiff;
            NTime = nTime;
            CleanJobs = cleanJobs;
            HashAlgorithm = hashAlgorithm;

            _shares = new List<UInt64>();
            CreationTime = TimeHelpers.NowInUnixTimestamp();

            // set the target
            Target = EncodedDifficulty.BigIntFromBitsHex();

            // set the block diff
            Difficulty = ((double)new BigRational(BigInteger.Parse("00000000ffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber), Target));

        }

        /// <summary>
        /// Creates a new instance of JobNotification.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="algorithm"></param>
        /// <param name="blockTemplate"></param>
        /// <param name="generationTransaction"></param>
        public Job(UInt64 id, IHashAlgorithm algorithm, IBlockTemplate blockTemplate, IGenerationTransaction generationTransaction)
        {
            // init the values.
            Id = id;
            RelayId = null;
            HashAlgorithm = algorithm;
            BlockTemplate = blockTemplate;
            Height = blockTemplate.Height;
            GenerationTransaction = generationTransaction;
            PreviousBlockHash = blockTemplate.PreviousBlockHash;
            PreviousBlockHashReversed = blockTemplate.PreviousBlockHash.HexToByteArray().ReverseByteOrder().ToHexString();
            CoinbaseInitial = generationTransaction.Initial.ToHexString();
            CoinbaseFinal = generationTransaction.Final.ToHexString();
            CreationTime = TimeHelpers.NowInUnixTimestamp();

            _shares = new List<UInt64>();

            // calculate the merkle tree
            MerkleTree = new MerkleTree(BlockTemplate.Transactions.GetHashList());

            // set version
            Version = BitConverter.GetBytes(blockTemplate.Version.BigEndian()).ToHexString();

            // set the encoded difficulty (bits)
            EncodedDifficulty = blockTemplate.Bits;

            // set the target
            Target = string.IsNullOrEmpty(blockTemplate.Target)
                ? EncodedDifficulty.BigIntFromBitsHex()
                : BigInteger.Parse(blockTemplate.Target, NumberStyles.HexNumber);

            // set the block diff
            Difficulty = ((double)new BigRational(AlgorithmManager.Diff1, Target));

            // set the ntime
            NTime = BitConverter.GetBytes(blockTemplate.CurTime.BigEndian()).ToHexString();
        }

        public IEnumerator<object> GetEnumerator()
        {
            var data = new List<object>
            {
                string.IsNullOrEmpty(RelayId)?Id.ToString("x"):RelayId,
                PreviousBlockHashReversed,
                CoinbaseInitial,
                CoinbaseFinal,
                MerkleTree.Branches,
                Version,
                EncodedDifficulty,
                NTime,
                CleanJobs
            };

            return data.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public bool RegisterShare(IShare share)
        {
            var submissionId = (UInt64) (share.ExtraNonce1 + share.ExtraNonce2 + share.NTime + share.Nonce); // simply hash the share by summing them..

            if(_shares.Contains(submissionId)) // if our list already contain the share
                return false; // it basically means we hit a duplicate share.

            _shares.Add(submissionId); // if the code flows here, that basically means we just recieved a new share.
            return true;
        }
    }
}
