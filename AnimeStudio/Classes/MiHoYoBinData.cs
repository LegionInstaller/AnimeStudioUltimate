using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace AnimeStudio
{
    public enum MiHoYoBinDataType
    {
        None,
        Bytes,
        JSON
    }
    public sealed class MiHoYoBinData : Object
    {
        private static Regex ASCII = new Regex("[^\u0020-\u007E]", RegexOptions.Compiled);

        public static bool Exportable;
        public static bool Encrypted;
        public static byte Key;

        public byte[] RawData;

        public MiHoYoBinData(ObjectReader reader) : base(reader)
        {
            int length = reader.ReadInt32();
            // Clamp to what is left of this object, and never past the end of the stream —
            // a bad length field must not turn into a negative/oversized ReadBytes.
            long remaining = reader.byteSize - (reader.Position - reader.byteStart);
            if (remaining > reader.Remaining)
                remaining = reader.Remaining;
            if (remaining < 0)
                remaining = 0;
            if (length <= 0 || length > remaining)
                length = (int)remaining;
            RawData = length > 0 ? reader.ReadBytes(length) : Array.Empty<byte>();
        }

        public string AsString => Type switch
        {
            MiHoYoBinDataType.JSON => JToken.Parse(DataStr).ToString(Formatting.Indented),
            MiHoYoBinDataType.Bytes => ASCII.Replace(DataStr, string.Empty),
            _ => "",
        };
        public new object Dump() => Type switch
        {
            MiHoYoBinDataType.JSON => AsString,
            MiHoYoBinDataType.Bytes => Data,
            _ => null,
        };
        private string DataStr => Encoding.UTF8.GetString(Data);

        public MiHoYoBinDataType Type
        {
            get
            {
                try
                {
                    var asToken = JToken.Parse(DataStr);
                    if (asToken.Type == JTokenType.Object || asToken.Type == JTokenType.Array)
                        return MiHoYoBinDataType.JSON;
                }
                catch (Exception)
                {
                    return MiHoYoBinDataType.Bytes;
                }
                return MiHoYoBinDataType.None;
            }
        }

        private byte[] Data
        {
            get
            {
                if (Encrypted)
                {
                    byte[] bytes = new byte[RawData.Length];
                    for (int i = 0; i < RawData.Length; i++)
                    {
                        bytes[i] = (byte)(RawData[i] ^ Key);
                    }
                    return bytes;
                }
                else return RawData;
            }
        }
    }
}
