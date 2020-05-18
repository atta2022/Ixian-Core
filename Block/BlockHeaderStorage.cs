﻿using IXICore.Meta;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IXICore
{
    /// <summary>
    ///  Ixian DLT Block Header Storage.
    /// </summary>
    /// 
    class BlockHeaderStorage
    {
        public static string path = "headers"; // temporary set to public for beta testers, change back to private before release

        private static object lockObject = new object();

        private static Dictionary<string, object[]> fileCache = new Dictionary<string, object[]>();

        private static bool stopped = false;

        private static List<BlockHeader> blockHeaderCache = new List<BlockHeader>();

        public static void init(string storage_path = "")
        {
            if (storage_path != "")
            {
                path = storage_path;
            }
            string db_path = path + Path.DirectorySeparatorChar + "0000";
            if (!Directory.Exists(db_path))
            {
                Directory.CreateDirectory(db_path);
            }
            stopped = false;
        }

        public static void stop()
        {
            stopped = true;
            cleanupFileCache(true);
        }

        /// <summary>
        ///  Saves block header to local storage
        /// </summary>
        /// <param name="block_header">Block header to save</param>
        /// <exception cref="Exception">Exception occured while saving block header.</exception>
        public static bool saveBlockHeader(BlockHeader block_header)
        {
            if(stopped)
            {
                return false;
            }
            lock (lockObject)
            {
                ulong block_num = block_header.blockNum;

                ulong file_block_num = ((ulong)(block_num / CoreConfig.maxBlockHeadersPerDatabase)) * CoreConfig.maxBlockHeadersPerDatabase;

                string db_path = path + Path.DirectorySeparatorChar + "0000" + Path.DirectorySeparatorChar + file_block_num + ".dat";

                FileStream fs = getStorageFile(db_path, true); // File.Open(db_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                if (fs.Length > 0)
                {
                    fs.Seek(-8, SeekOrigin.End);

                    byte[] file_block_header_bytes = new byte[8];
                    fs.Read(file_block_header_bytes, 0, 8);
                    if (BitConverter.ToUInt64(file_block_header_bytes, 0) + 1 != block_num)
                    {
                        return false;
                    }
                }

                try
                {
                    // insert dummy block for first block
                    if(block_num == 1)
                    {
                        BlockHeader dummy_block_header = new BlockHeader();
                        dummy_block_header.blockNum = 0;
                        dummy_block_header.blockChecksum = new byte[1];

                        byte[] dummy_block_header_bytes = dummy_block_header.getBytes();
                        byte[] dummy_block_header_len_bytes = BitConverter.GetBytes(dummy_block_header_bytes.Length);

                        fs.Write(dummy_block_header_len_bytes, 0, dummy_block_header_len_bytes.Length);
                        fs.Write(dummy_block_header_bytes, 0, dummy_block_header_bytes.Length);

                        byte[] dummy_block_num_bytes = BitConverter.GetBytes(dummy_block_header.blockNum);
                        fs.Write(dummy_block_num_bytes, 0, dummy_block_num_bytes.Length);
                    }

                    byte[] block_header_bytes = block_header.getBytes();
                    byte[] block_header_len_bytes = BitConverter.GetBytes(block_header_bytes.Length);

                    fs.Write(block_header_len_bytes, 0, block_header_len_bytes.Length);
                    fs.Write(block_header_bytes, 0, block_header_bytes.Length);

                    byte[] block_num_bytes = BitConverter.GetBytes(block_header.blockNum);
                    fs.Write(block_num_bytes, 0, block_num_bytes.Length);
                }
                catch (Exception e)
                {
                    Logging.error("Exception occured while saving block header: {0}", e);
                }

                return true;
            }
        }

        /// <summary>
        ///  Returns specified block header from local storage or null if it doesn't exist.
        /// </summary>
        /// <param name="block_num">Block height of the block header to fetch.</param>
        /// <exception cref="Exception">Exception occured while trying to get block header.</exception>
        /// <returns>Requested block header or null if block header doesn't exist.</returns>
        public static BlockHeader getBlockHeader(ulong block_num)
        {
            if (stopped)
            {
                return null;
            }

            lock(blockHeaderCache)
            {
                var tmp_bh = blockHeaderCache.Find(x => x.blockNum == block_num);
                if (tmp_bh != null)
                {
                    return tmp_bh;
                }
            }

            lock (lockObject)
            {
                BlockHeader block_header = null;

                try
                {
                    ulong file_block_num = ((ulong)(block_num / CoreConfig.maxBlockHeadersPerDatabase)) * CoreConfig.maxBlockHeadersPerDatabase;

                    string db_path = path + Path.DirectorySeparatorChar + "0000" + Path.DirectorySeparatorChar + file_block_num + ".dat";

                    FileStream fs = getStorageFile(db_path, true); //File.Open(db_path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);

                    fs.Seek(0, SeekOrigin.Begin);

                    for (ulong i = file_block_num; i <= block_num; i++)
                    {
                        if(fs.Position == fs.Length)
                        {
                            Logging.warn("Reached end of file while getting block header #{0} from local storage.", block_num);
                            break;
                        }

                        byte[] block_header_len_bytes = new byte[4];
                        fs.Read(block_header_len_bytes, 0, 4);

                        byte[] block_header_bytes = new byte[BitConverter.ToInt32(block_header_len_bytes, 0)];
                        fs.Read(block_header_bytes, 0, block_header_bytes.Length);

                        byte[] block_num_bytes = new byte[8];
                        fs.Read(block_num_bytes, 0, 8);

                        if (i == block_num)
                        {
                            block_header = new BlockHeader(block_header_bytes);

                            if(block_header.blockNum != block_num)
                            {
                                block_header = null;
                                Logging.error("Incorrect block header number #{0} received from storage, expecting #{1}", block_header.blockNum, block_num);
                            }else
                            {
                                lock (blockHeaderCache)
                                {
                                    blockHeaderCache.Add(block_header);
                                    if(blockHeaderCache.Count > 20)
                                    {
                                        blockHeaderCache.RemoveAt(0);
                                    }
                                }
                            }
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Logging.error("Exception occured while trying to get block header #{0}: {1}", block_num, e);
                }

                return block_header;
            }
        }

        /// <summary>
        /// Returns last block header from local storage.
        /// </summary>
        public static BlockHeader getLastBlockHeader()
        {
            if (stopped)
            {
                return null;
            }
            lock (lockObject)
            {
                ulong file_block_num = 0;
                ulong file_scan_block_num = 0;
                bool found = false;
                bool found_at_least_one = false;
                string db_path = "";
                string db_path_root = path + Path.DirectorySeparatorChar + "0000" + Path.DirectorySeparatorChar;

                var files = Directory.GetFiles(db_path_root, "*.dat", SearchOption.TopDirectoryOnly).OrderBy(x => UInt64.Parse(Path.GetFileNameWithoutExtension(x)));

                string file = null;
                if (files.Count() > 0)
                {
                    file = files.First();
                }

                if (file != null)
                {
                    file_scan_block_num = file_block_num = UInt64.Parse(Path.GetFileNameWithoutExtension(file));
                }

                while (!found)
                {
                    db_path = db_path_root + file_scan_block_num + ".dat";
                    
                    if (verifyStorageFile(file_scan_block_num, db_path))
                    {
                        file_scan_block_num += CoreConfig.maxBlockHeadersPerDatabase;
                        file_block_num = file_scan_block_num;
                        found_at_least_one = true;
                    }
                    else if(File.Exists(db_path) && !found_at_least_one)
                    {
                        cleanupFileCache(true);
                        File.Delete(db_path);
                        file_scan_block_num += CoreConfig.maxBlockHeadersPerDatabase;
                    }
                    else
                    {
                        if (file_block_num >= CoreConfig.maxBlockHeadersPerDatabase)
                        {
                            file_block_num -= CoreConfig.maxBlockHeadersPerDatabase;
                        }else
                        {
                            file_block_num = 0;
                        }
                        db_path = db_path_root + file_block_num + ".dat";
                        found = true;
                    }
                }

                if (!found_at_least_one)
                {
                    deleteCache();
                    return null;
                }

                FileStream fs = getStorageFile(db_path, true);

                fs.Seek(0, SeekOrigin.Begin);

                BlockHeader block_header = null;

                try
                {
                    ulong block_num = file_block_num + CoreConfig.maxBlockHeadersPerDatabase;
                    for (ulong i = file_block_num; i < block_num; i++)
                    {
                        byte[] block_header_len_bytes = new byte[4];
                        fs.Read(block_header_len_bytes, 0, 4);

                        byte[] block_header_bytes = new byte[BitConverter.ToInt32(block_header_len_bytes, 0)];
                        fs.Read(block_header_bytes, 0, block_header_bytes.Length);

                        byte[] block_num_bytes = new byte[8];
                        fs.Read(block_num_bytes, 0, 8);

                        BlockHeader cur_block_header = new BlockHeader(block_header_bytes);

                        if (BitConverter.ToUInt64(block_num_bytes, 0) != cur_block_header.blockNum)
                        {
                            break;
                        }

                        if (block_header != null && cur_block_header.blockNum != block_header.blockNum + 1)
                        {
                            break;
                        }

                        block_header = cur_block_header;
                        
                        if (fs.Position == fs.Length)
                        {
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Logging.error("Exception occured while trying to get last block header: {0}", e);
                }
                if (block_header != null)
                {
                    removeAllBlocksAfter(block_header.blockNum);
                }
                else
                {
                    deleteCache();
                }

                return block_header;
            }
        }

        private static bool verifyStorageFile(ulong file_block_num, string db_path)
        {
            if(!File.Exists(db_path))
            {
                return false;
            }

            try
            {

                FileStream fs = getStorageFile(db_path, true);

                byte[] block_header_len_bytes = new byte[4];
                fs.Read(block_header_len_bytes, 0, 4);

                byte[] block_header_bytes = new byte[BitConverter.ToInt32(block_header_len_bytes, 0)];
                fs.Read(block_header_bytes, 0, block_header_bytes.Length);

                byte[] block_num_bytes = new byte[8];
                fs.Read(block_num_bytes, 0, 8);

                BlockHeader cur_block_header = new BlockHeader(block_header_bytes);

                if (BitConverter.ToUInt64(block_num_bytes, 0) != cur_block_header.blockNum)
                {
                    return false;
                }

                if (file_block_num != cur_block_header.blockNum)
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                Logging.error("Exception occured while reading from blockheader storage file: " + e);
                return false;
            }
            return true;
        }

        public static void removeAllBlocksBefore(ulong block_num)
        {
            lock(lockObject)
            {
                cleanupFileCache(true);

                ulong file_block_num = ((ulong)(block_num / CoreConfig.maxBlockHeadersPerDatabase)) * CoreConfig.maxBlockHeadersPerDatabase;

                bool first_file = false;

                while (!first_file)
                {
                    string db_path = path + Path.DirectorySeparatorChar + "0000" + Path.DirectorySeparatorChar + file_block_num + ".dat";

                    if(!File.Exists(db_path))
                    {
                        break;
                    }

                    File.Delete(db_path);

                    if (file_block_num > 0)
                    {
                        file_block_num -= CoreConfig.maxBlockHeadersPerDatabase;
                    }
                    else if (file_block_num == 0)
                    {
                        first_file = true;
                    }
                }
            }
        }

        /// <summary>
        ///  Truncates database to specified block header number. All block headers after it will be deleted.
        /// </summary>
        /// <param name="block_num">Block height of the block header to truncate to.</param>
        /// <exception cref="Exception">Exception occured while trying to get block header.</exception>
        public static void removeAllBlocksAfter(ulong block_num)
        {
            lock (lockObject)
            {
                cleanupFileCache(true);

                bool truncate = true;
                ulong file_block_num = ((ulong)(block_num / CoreConfig.maxBlockHeadersPerDatabase)) * CoreConfig.maxBlockHeadersPerDatabase;
                while (truncate)
                {
                    bool delete_file = false;

                    string db_path = path + Path.DirectorySeparatorChar + "0000" + Path.DirectorySeparatorChar + file_block_num + ".dat";

                    FileStream fs = File.Open(db_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                    try
                    {
                        ulong last_block_num = file_block_num;
                        for (ulong i = file_block_num; i <= block_num; i++)
                        {
                            if (fs.Position == fs.Length)
                            {
                                Logging.warn("Reached end of file while truncating database to block header #{0}.", block_num);
                                break;
                            }

                            byte[] block_header_len_bytes = new byte[4];
                            fs.Read(block_header_len_bytes, 0, 4);

                            byte[] block_header_bytes = new byte[BitConverter.ToInt32(block_header_len_bytes, 0)];
                            fs.Read(block_header_bytes, 0, block_header_bytes.Length);

                            byte[] block_num_bytes = new byte[8];
                            fs.Read(block_num_bytes, 0, 8);

                            last_block_num = i;
                        }

                        fs.SetLength(fs.Position);

                        if(fs.Position == 0)
                        {
                            delete_file = true;
                        }
                    }
                    catch (Exception e)
                    {
                        Logging.error("Exception occured while trying to truncate block header database to #{0}: {1}", block_num, e);
                    }

                    fs.Close();

                    if(delete_file)
                    {
                        File.Delete(db_path);
                    }

                    file_block_num += CoreConfig.maxBlockHeadersPerDatabase;

                    db_path = path + Path.DirectorySeparatorChar + "0000" + Path.DirectorySeparatorChar + file_block_num + ".dat";
                    if (!File.Exists(db_path))
                    {
                        truncate = false;
                    }
                }
            }
        }

        public static void deleteCache()
        {
            lock (lockObject)
            {
                cleanupFileCache(true);

                string db_path = path + Path.DirectorySeparatorChar + "0000" + Path.DirectorySeparatorChar;
                string[] fileNames = Directory.GetFiles(db_path);
                foreach (string fileName in fileNames)
                {
                    File.Delete(fileName);
                }
            }
        }

        private static void cleanupFileCache(bool force = false)
        {
            lock (fileCache)
            {
                long curTime = Clock.getTimestamp();
                Dictionary<string, object[]> tmp_cache = new Dictionary<string, object[]>(fileCache);
                foreach (var entry in tmp_cache)
                {
                    if (force == true || curTime - (long)entry.Value[1] > 60)
                    {
                        ((FileStream)entry.Value[0]).Close();
                        fileCache.Remove(entry.Key);

                        // Fix for occasional locked database error
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        // End of fix
                    }
                }
            }
            lock (blockHeaderCache)
            {
                blockHeaderCache.Clear();
            }
        }

        private static FileStream getStorageFile(string path, bool cache)
        {
            if (stopped)
            {
                return null;
            }
            lock (fileCache)
            {
                if (fileCache.ContainsKey(path))
                {
                    if (cache)
                    {
                        fileCache[path][1] = Clock.getTimestamp();
                        cleanupFileCache();
                    }
                    return (FileStream)fileCache[path][0];
                }

                FileStream fs = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (cache)
                {
                    fileCache.Add(path, new object[2] { fs, Clock.getTimestamp() });
                }
                
                return fs;
            }
        }

        public static void test()
        {
            deleteCache();

            BlockHeader tmp_bh = getLastBlockHeader();

            BlockHeader bh1 = new BlockHeader();
            bh1.blockNum = 1;
            bh1.blockChecksum = new byte[10];
            saveBlockHeader(bh1);

            BlockHeader bh2 = new BlockHeader();
            bh2.blockNum = 2;
            bh2.blockChecksum = new byte[10];
            saveBlockHeader(bh2);

            BlockHeader bh3 = new BlockHeader();
            bh3.blockNum = 3;
            bh3.blockChecksum = new byte[10];
            saveBlockHeader(bh3);

            BlockHeader bh4 = new BlockHeader();
            bh4.blockNum = 4;
            bh4.blockChecksum = new byte[10];
            saveBlockHeader(bh4);

            tmp_bh = getLastBlockHeader();
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 4");

            tmp_bh = getBlockHeader(1);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 1");

            tmp_bh = getBlockHeader(2);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 2");

            tmp_bh = getBlockHeader(3);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 3");

            tmp_bh = getBlockHeader(4);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 4");

            removeAllBlocksAfter(2);
            Console.WriteLine("Truncated database to 2");

            tmp_bh = getBlockHeader(1);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 1");

            tmp_bh = getBlockHeader(2);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 2");

            tmp_bh = getLastBlockHeader();
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 2");

            tmp_bh = getBlockHeader(3);
            Console.WriteLine("Got block header " + tmp_bh + ", expecting null");

            BlockHeader bh5 = new BlockHeader();
            bh5.blockNum = 1000;
            bh5.blockChecksum = new byte[10];
            saveBlockHeader(bh5);

            tmp_bh = getLastBlockHeader();
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 1000");

            bh5.blockNum = 1001;
            bh5.blockChecksum = new byte[10];
            saveBlockHeader(bh5);

            saveBlockHeader(bh3);

            tmp_bh = getLastBlockHeader();
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 1001");

            tmp_bh = getBlockHeader(1);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 1");

            tmp_bh = getBlockHeader(2);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 2");

            tmp_bh = getBlockHeader(3);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 3");

            tmp_bh = getBlockHeader(1000);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 1000");

            tmp_bh = getBlockHeader(1001);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 1001");

            removeAllBlocksAfter(1000);
            Console.WriteLine("Truncated database to 1000");

            tmp_bh = getLastBlockHeader();
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 1000");

            removeAllBlocksAfter(999);
            Console.WriteLine("Truncated database to 999");

            tmp_bh = getLastBlockHeader();
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 3");

            saveBlockHeader(bh4);

            tmp_bh = getBlockHeader(4);
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 4");

            bh5.blockNum = 6;
            bh5.blockChecksum = new byte[10];
            saveBlockHeader(bh5);

            tmp_bh = getLastBlockHeader();
            Console.WriteLine("Got block header #" + tmp_bh.blockNum + ", expecting 4");
        }
    }
}
