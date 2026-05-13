using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement custom fast/in memory mapped disk access
    /// [ThreadSafe]
    /// </summary>
    internal class DiskService : IDisposable
    {
        private readonly MemoryCache _cache;
        private readonly EngineState _state;

        private IStreamFactory _dataFactory;
        private readonly IStreamFactory _logFactory;

        private StreamPool _dataPool;
        private readonly StreamPool _logPool;
        private readonly Lazy<Stream> _writer;

        private long _dataLength;
        private long _logLength;

        //ArrayPool<T> 是 .NET 专门用来复用数组的对象池，核心目的是：减少内存分配、降低 GC 压力，让程序更快、更稳定。 
        //不用反复 new byte [] /new char []，而是从池里 “借” 数组，用完 “还” 回去，避免频繁 GC。
        //byte[] buffer = new byte[4096]; // 每次都分配新内存  问题：  频繁创建 → 大量小对象垃圾 GC 频繁回收 → 程序卡顿、性能下降
        // 从数组池借一页，避免GC（高性能）
        private static readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

        public DiskService(
            EngineSettings settings,
            EngineState state,
            int[] memorySegmentSizes)
        {
            _cache = new MemoryCache(memorySegmentSizes);
            _state = state;


            // get new stream factory based on settings
            _dataFactory = settings.CreateDataFactory();
            _logFactory = settings.CreateLogFactory();

            // create stream pool
            _dataPool = new StreamPool(_dataFactory, false);
            _logPool = new StreamPool(_logFactory, true);

            // get lazy disk writer (log file) - created only when used
            _writer = _logPool.Writer;

            var isNew = _dataFactory.GetLength() == 0L;

            // create new database if not exist yet
            if (isNew)
            {
                LOG($"creating new database: '{Path.GetFileName(_dataFactory.Name)}'", "DISK");

                // _dataPool.Writer.Value懒加载
                // Lazy<T>是把 “对象的创建时机” 推迟到第一次使用时，同时保证线程安全 —— 它内部会处理多线程同时访问.Value 的情况，只会执行一次创建逻辑
                this.Initialize(_dataPool.Writer.Value, settings.Collation, settings.InitialSize);
            }

            // if not readonly, force open writable datafile
            if (settings.ReadOnly == false)
            {
                // 提前触发 Writer 初始化，避免后面用时卡顿
                // 等价于：我不在乎 CanRead 是 true 还是 false
                // 我只想让 Writer.Value 被访问一次 → 强制完成初始化
                _ = _dataPool.Writer.Value.CanRead;
            }

            // get initial data file length
            _dataLength = _dataFactory.GetLength() - PAGE_SIZE;

            // get initial log file length (should be 1 page before)
            if (_logFactory.Exists())
            {
                _logLength = _logFactory.GetLength() - PAGE_SIZE;
            }
            else
            {
                _logLength = -PAGE_SIZE;
            }
        }

        /// <summary>
        /// Get memory cache instance
        /// </summary>
        public MemoryCache Cache => _cache;

        /// <summary>
        /// Create a new empty database (use synced mode)
        /// </summary>
        private void Initialize(Stream stream, Collation collation, long initialSize)
        {
            //// 1. 创建内存页
            //var buffer = new PageBuffer(...);
            //// 2. 创建头页
            //var header = new HeaderPage(buffer, 0);
            //// 3. 修改头页（排序规则等）
            //header.Pragmas.Set(...);
            //// 4. ✅ 必须调用：把修改同步到 buffer
            //header.UpdateBuffer();
            //// 5. 才能写入磁盘
            //stream.Write(... );


            //创建一页内存缓冲区  分配一页大小的字节数组，比如 4KB / 8KB / 16KB（数据库标准页大小）。
            //分配一页大小的字节数组，比如 4KB / 8KB / 16KB（数据库标准页大小）。
            var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            //一句话总结：把一块内存（buffer），变成整个数据库文件的「第 0 页 —— 头部页」。
            //数据库文件永远 第 0 页是头部页
            //告诉 HeaderPage：你就用这块 buffer 读写。这是第 0 页，是文件头。文件：[ 第0页(头部) ] [ 数据页1 ] [ 数据页2 ] [ 数据页3 ] ...
            //等于：拿一块 4KB 内存 → 做成文件的第一张名片（头部）。
            //创建一个头部页对象，管理第 0 页的内存，用来存整个数据库的元信息。
            var header = new HeaderPage(buffer, 0);

            // update collation
            //一句话总结：给数据库的头部页，设置 “字符串排序规则”，没有就用默认，有就不覆盖。
            //header.Pragmas.Set(排序规则键, 最终排序规则, 不覆盖已存在的值);
            header.Pragmas.Set(Pragmas.COLLATION, (collation ?? Collation.Default).ToString(), false);

            // update buffer
            //把你在 Header 上改的所有内容，刷新到底层的 byte [] 缓冲区里。
            header.UpdateBuffer();

            stream.Write(buffer.Array, buffer.Offset, PAGE_SIZE);

            if (initialSize > 0)
            {
                if (stream is AesStream) throw LiteException.InitialSizeCryptoNotSupported();
                if (initialSize % PAGE_SIZE != 0) throw LiteException.InvalidInitialSize();
                stream.SetLength(initialSize);
            }

            stream.FlushToDisk();
        }

        /// <summary>
        /// Get a new instance for read data/log pages. This instance are not thread-safe - must request 1 per thread (used in Transaction)
        /// </summary>
        public DiskReader GetReader()
        {
            return new DiskReader(_state, _cache, _dataPool, _logPool);
        }

        /// <summary>
        /// This method calculates the maximum number of items (documents or IndexNodes) that this database can have.
        /// The result is used to prevent infinite loops in case of problems with pointers
        /// Each page support max of 255 items. Use 10 pages offset (avoid empty disk)
        /// </summary>
        public uint MAX_ITEMS_COUNT => (uint)(((_dataLength + _logLength) / PAGE_SIZE) + 10) * byte.MaxValue;

        /// <summary>
        /// When a page are requested as Writable but not saved in disk, must be discard before release
        /// </summary>
        public void DiscardDirtyPages(IEnumerable<PageBuffer> pages)
        {
            // only for ROLLBACK action
            foreach (var page in pages)
            {
                // complete discard page and content
                _cache.DiscardPage(page);
            }
        }

        /// <summary>
        /// Discard pages that contains valid data and was not modified
        /// </summary>
        public void DiscardCleanPages(IEnumerable<PageBuffer> pages)
        {
            foreach (var page in pages)
            {
                // if page was not modified, try move to readable list
                if (_cache.TryMoveToReadable(page) == false)
                {
                    // if already in readable list, just discard
                    _cache.DiscardPage(page);
                }
            }
        }

        /// <summary>
        /// Request for a empty, writable non-linked page.
        /// </summary>
        public PageBuffer NewPage()
        {
            return _cache.NewPage();
        }

        /// <summary>
        /// Write all pages inside log file in a thread safe operation
        /// </summary>
        public int WriteLogDisk(IEnumerable<PageBuffer> pages)
        {
            var count = 0;
            var stream = _writer.Value;

            // do a global write lock - only 1 thread can write on disk at time
            lock(stream)
            {
                foreach (var page in pages)
                {
                    ENSURE(page.ShareCounter == BUFFER_WRITABLE, "to enqueue page, page must be writable");

                    // adding this page into file AS new page (at end of file)
                    // must add into cache to be sure that new readers can see this page
                    page.Position = Interlocked.Add(ref _logLength, PAGE_SIZE);

                    // should mark page origin to log because async queue works only for log file
                    // if this page came from data file, must be changed before MoveToReadable
                    page.Origin = FileOrigin.Log;

                    // mark this page as readable and get cached paged to enqueue
                    var readable = _cache.MoveToReadable(page);

                    // set log stream position to page
                    stream.Position = page.Position;

#if DEBUG
                    _state.SimulateDiskWriteFail?.Invoke(page);
#endif

                    // and write to disk in a sync mode
                    stream.Write(page.Array, page.Offset, PAGE_SIZE);

                    // release page here (no page use after this)
                    page.Release();

                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Get file length based on data/log length variables (no direct on disk)
        /// </summary>
        public long GetFileLength(FileOrigin origin)
        {
            if (origin == FileOrigin.Log)
            {
                return _logLength + PAGE_SIZE;
            }
            else
            {
                return _dataLength + PAGE_SIZE;
            }
        }

        /// <summary>
        /// Mark a file with a single signal to next open do auto-rebuild. Used only when closing database (after close files)
        /// </summary>
        internal void MarkAsInvalidState()
        {
            FileHelper.TryExec(60, () =>
            {
                using (var stream = _dataFactory.GetStream(true, true))
                {
                    // 从数组池借一页，避免GC（高性能）
                    var buffer = _bufferPool.Rent(PAGE_SIZE);
                    stream.Read(buffer, 0, PAGE_SIZE);
                    buffer[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
                    stream.Position = 0;
                    stream.Write(buffer, 0, PAGE_SIZE);
                    // 用完归还
                    _bufferPool.Return(buffer, true);
                }
            });
        }

        #region Sync Read/Write operations

        /// <summary>
        /// Read all database pages inside file with no cache using. PageBuffers dont need to be Released
        /// </summary>
        public IEnumerable<PageBuffer> ReadFull(FileOrigin origin)
        {
            // do not use MemoryCache factory - reuse same buffer array (one page per time)
            // do not use BufferPool because header page can't be shared (byte[] is used inside page return)
            var buffer = new byte[PAGE_SIZE];

            var pool = origin == FileOrigin.Log ? _logPool : _dataPool;
            var stream = pool.Rent();

            try
            {
                // get length before starts (avoid grow during loop)
                var length = this.GetFileLength(origin);

                stream.Position = 0;

                while (stream.Position < length)
                {
                    var position = stream.Position;

                    var bytesRead = stream.Read(buffer, 0, PAGE_SIZE);

                    ENSURE(bytesRead == PAGE_SIZE, "ReadFull must read PAGE_SIZE bytes [{0}]", bytesRead);

                    yield return new PageBuffer(buffer, 0, 0)
                    {
                        Position = position,
                        Origin = origin,
                        ShareCounter = 0
                    };
                }
            }
            finally
            {
                pool.Return(stream);
            }
        }

        /// <summary>
        /// Write pages DIRECT in disk. This pages are not cached and are not shared - WORKS FOR DATA FILE ONLY
        /// </summary>
        public void WriteDataDisk(IEnumerable<PageBuffer> pages)
        {
            var stream = _dataPool.Writer.Value;

            foreach (var page in pages)
            {
                ENSURE(page.ShareCounter == 0, "this page can't be shared to use sync operation - do not use cached pages");

                _dataLength = Math.Max(_dataLength, page.Position);

                stream.Position = page.Position;

                stream.Write(page.Array, page.Offset, PAGE_SIZE);
            }

            stream.FlushToDisk();
        }

        /// <summary>
        /// Set new length for file in sync mode. Queue must be empty before set length
        /// </summary>
        public void SetLength(long length, FileOrigin origin)
        {
            var stream = origin == FileOrigin.Log ? _logPool.Writer : _dataPool.Writer;

            if (origin == FileOrigin.Log)
            {
                Interlocked.Exchange(ref _logLength, length - PAGE_SIZE);
            }
            else
            {
                Interlocked.Exchange(ref _dataLength, length - PAGE_SIZE);
            }

            stream.Value.SetLength(length);
        }

        /// <summary>
        /// Get file name (or Stream name)
        /// </summary>
        public string GetName(FileOrigin origin)
        {
            return origin == FileOrigin.Data ? _dataFactory.Name : _logFactory.Name;
        }

        #endregion

        public void Dispose()
        {
            // get stream length from writer - is safe because only this instance
            // can change file size
            var delete = _logFactory.Exists() && _logPool.Writer.Value.Length == 0;

            // dispose Stream pools
            _dataPool.Dispose();
            _logPool.Dispose();

            if (delete) _logFactory.Delete();

            // other disposes
            _cache.Dispose();
        }
    }
}
