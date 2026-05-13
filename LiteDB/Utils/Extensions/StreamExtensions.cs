using System;
using System.IO;
using System.IO.Pipes;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static class StreamExtensions
    {
        /// <summary>
        /// If Stream are FileStream, flush content direct to disk (avoid OS cache)
        /// </summary>
        public static void FlushToDisk(this Stream stream)
        {
            if (stream is FileStream fstream)
            {
                // 标准：强制写入物理磁盘
                // 关键：true = 刷新到磁盘（而非仅系统缓存）
                fstream.Flush(true);
            }
            else
            {
                //操作系统缓存
                stream.Flush();
            }
        }
    }
}