using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Gibbed.IO;

namespace CoiniumServ.Utils.Extensions
{
    public static class NumberExtensions
    {
        public static byte[] NumberToVarBytes(this UInt32 numberToConvert)
        {
            var buff = BitConverter.GetBytes(numberToConvert);
            while (buff[0] == 0)
                buff = buff.Slice(1, buff.Length);
            return buff;
        }

        public static byte[] NumberToVarBytes(this UInt64 numberToConvert)
        {
            var buff = BitConverter.GetBytes(numberToConvert);
            while (buff[0] == 0)
                buff = buff.Slice(1, buff.Length);
            return buff;
        }

        public static byte[] NumberToFixedBytes(this UInt32 numberToConvert, int TargetArrayLength)
        {
            var buff = BitConverter.GetBytes(numberToConvert);
            buff=buff.Slice(buff.Length-TargetArrayLength,buff.Length);
            return buff;
        }

        public static byte[] NumberToFixedBytes(this UInt64 numberToConvert, int TargetArrayLength)
        {
            var buff = BitConverter.GetBytes(numberToConvert.BigEndian());
            buff = buff.Slice(buff.Length - TargetArrayLength, buff.Length);
            return buff;
        }
    }
}
