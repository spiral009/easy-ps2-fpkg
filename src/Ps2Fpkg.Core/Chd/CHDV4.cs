using CHDReaderTest.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CHDReaderTest;

internal static class CHDV4
{
    internal struct mapentry
    {
        public ulong offset;
        public uint crc;
        public uint length;
        public mapFlags flags;
    }

    public static uint CHD_MDFLAGS_CHECKSUM = 0x01;        // indicates data is checksummed

    public static bool go(Stream file)
    {
        using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);

        uint flags = br.ReadUInt32BE();

        uint compression = br.ReadUInt32BE();
        uint totalblocks = br.ReadUInt32BE(); // total number of CHD Blocks

        ulong totalbytes = br.ReadUInt64BE();  // total byte size of the image
        ulong metaoffset = br.ReadUInt64BE();

        uint blocksize = br.ReadUInt32BE();    // length of a CHD Block
        byte[] sha1 = br.ReadBytes(20);
        byte[] parentsha1 = br.ReadBytes(20);
        byte[] rawsha1 = br.ReadBytes(20);

        mapentry[] map = new mapentry[totalblocks];

        for (int i = 0; i < totalblocks; i++)
        {
            map[i].offset = br.ReadUInt64BE();
            map[i].crc = br.ReadUInt32BE();
            map[i].length = (uint)((br.ReadUInt16BE()) | (br.ReadByte() << 16));
            map[i].flags = (mapFlags)br.ReadByte();
        }

        using SHA1 sha1Check = SHA1.Create();

        byte[] buffer = new byte[blocksize];

        int block = 0;
        ulong sizetoGo = totalbytes;
        while (sizetoGo > 0)
        {
            /* progress */
            if ((block % 1000) == 0)
                Console.Write($"Verifying, {(100 - sizetoGo * 100 / totalbytes):N1}% complete...\r");


            /* read the block into the cache */
            chd_error err = readBlock(file, compression, block, map, (uint)blocksize, ref buffer);
            if (err != chd_error.CHDERR_NONE)
                return false;

            int sizenext = sizetoGo > (ulong)blocksize ? (int)blocksize : (int)sizetoGo;

            sha1Check.TransformBlock(buffer, 0, sizenext, null, 0);

            /* prepare for the next block */
            block++;
            sizetoGo -= (ulong)sizenext;

        }
        Console.WriteLine($"Verifying, 100.0% complete...");

        byte[] tmp = new byte[0];
        sha1Check.TransformFinalBlock(tmp, 0, 0);

        // here it is now using the rawsha1 value from the header to validate the raw binary data.
        if (!Util.ByteArrEquals(rawsha1, sha1Check.Hash))
        {
            return false;
        }

        // List<byte[]>metaHashes contains the byte data that is hashed below to validate the meta data
        // each metaHash is 24 bytes:
        // 0-3  : is the byte data for the metaTag
        // 4-23 : is the SHA1 of the metaData

        List<byte[]> metaHashes = new List<byte[]>();

        // loop over the metadata, until metaoffset=0
        while (metaoffset != 0)
        {
            file.Seek((long)metaoffset, SeekOrigin.Begin);
            uint metaTag = br.ReadUInt32BE();
            uint metaLength = br.ReadUInt32BE();
            ulong metaNext = br.ReadUInt64BE();
            uint metaFlags = metaLength >> 24;
            metaLength &= 0x00ffffff;

            byte[] metaData = new byte[metaLength];
            file.Read(metaData, 0, metaData.Length);

            Console.WriteLine($"{(char)((metaTag >> 24) & 0xFF)}{(char)((metaTag >> 16) & 0xFF)}{(char)((metaTag >> 8) & 0xFF)}{(char)((metaTag >> 0) & 0xFF)}  Length: {metaLength}");
            if (Util.isAscii(metaData))
                Console.WriteLine($"Data: {Encoding.ASCII.GetString(metaData)}");
            else
                Console.WriteLine($"Data: Binary Data Length {metaData.Length}");

            // take the 4 byte metaTag, and the metaData
            // SHA1 the metaData to 20 byte SHA1
            // metadata_hash return these 24 bytes in a byte[24]
            if ((metaFlags & CHD_MDFLAGS_CHECKSUM) != 0)
                metaHashes.Add(metadata_hash(metaTag, metaData));

            // set location of next meta data entry in the CHD (set to 0 if finished.)
            metaoffset = metaNext;
        }

        // binary sort the metaHashes
        metaHashes.Sort(Util.ByteArrCompare);

        // build the final SHA1
        // starting with the 20 byte rawsha1 from the main CHD data
        // then add the 24 byte for each meta data entry
        using SHA1 sha1Total = SHA1.Create();
        sha1Total.TransformBlock(rawsha1, 0, rawsha1.Length, null, 0);

        for (int i = 0; i < metaHashes.Count; i++)
            sha1Total.TransformBlock(metaHashes[i], 0, metaHashes[i].Length, null, 0);

        sha1Total.TransformFinalBlock(tmp, 0, 0);

        // compare the calculated metaData + rawData SHA1 with sha1 from the CHD header
        return Util.ByteArrEquals(sha1, sha1Total.Hash);
    }


    private static byte[] metadata_hash(uint metaTag, byte[] metaData)
    {
        // make 24 byte metadata hash
        // 0-3  :  metaTag
        // 4-23 :  sha1 of the metaData

        byte[] metaHash = new byte[24];
        metaHash[0] = (byte)((metaTag >> 24) & 0xff);
        metaHash[1] = (byte)((metaTag >> 16) & 0xff);
        metaHash[2] = (byte)((metaTag >> 8) & 0xff);
        metaHash[3] = (byte)((metaTag >> 0) & 0xff);
        byte[] metaDataHash = SHA1.HashData(metaData);
        
        for (int i = 0; i < 20; i++)
            metaHash[4 + i] = metaDataHash[i];

        return metaHash;
    }


    private static chd_error readBlock(Stream file, uint compression, int mapindex, mapentry[] map, uint blocksize, ref byte[] cache)
    {
        bool checkCrc = true;
        mapentry mapEntry = map[mapindex];

        switch (mapEntry.flags & mapFlags.MAP_ENTRY_FLAG_TYPE_MASK)
        {
            case mapFlags.MAP_ENTRY_TYPE_COMPRESSED:
                {
                    file.Seek((long)mapEntry.offset, SeekOrigin.Begin);
                    byte[] source = new byte[mapEntry.length];
                    file.Read(source, 0, source.Length);

                    switch (compression)
                    {
                        case 1: // 1=HDCOMPRESSION_ZLIB
                        case 2: // 2=HDCOMPRESSION_ZLIB_PLUS
                            {
                                chd_error ret = CHDReaders.zlib(source, cache);
                                if (ret != chd_error.CHDERR_NONE)
                                    return ret;
                                break;
                            }
                        case 3: // 3=A/V Huff
                            {
                                chd_error ret = CHDReaders.avHuff(source, cache);
                                if (ret != chd_error.CHDERR_NONE)
                                    return ret;
                                break;
                            }
                        default:
                            {
                                return chd_error.CHDERR_UNSUPPORTED_FORMAT;
                            }
                    }
                    break;
                }

            case mapFlags.MAP_ENTRY_TYPE_UNCOMPRESSED:
                {
                    file.Seek((long)mapEntry.offset, SeekOrigin.Begin);
                    int bytes = file.Read(cache, 0, (int)blocksize);

                    if (bytes != (int)blocksize)
                        return chd_error.CHDERR_READ_ERROR;
                    break;
                }

            case mapFlags.MAP_ENTRY_TYPE_MINI:
                {
                    byte[] tmp = BitConverter.GetBytes(mapEntry.offset);
                    for (int i = 0; i < 8; i++)
                    {
                        cache[i] = tmp[7 - i];
                    }

                    for (int i = 8; i < blocksize; i++)
                    {
                        cache[i] = cache[i - 8];
                    }

                    break;
                }

            case mapFlags.MAP_ENTRY_TYPE_SELF_HUNK:
                {
                    chd_error ret = readBlock(file, compression, (int)mapEntry.offset, map, blocksize, ref cache);
                    if (ret != chd_error.CHDERR_NONE)
                        return ret;
                    // check CRC in the read_block_into_cache call
                    checkCrc = false;
                    break;
                }
            default:
                return chd_error.CHDERR_DECOMPRESSION_ERROR;

        }

        if (checkCrc && (mapEntry.flags & mapFlags.MAP_ENTRY_FLAG_NO_CRC) == 0)
        {
            if (!CRC.VerifyDigest(mapEntry.crc, cache, 0, blocksize))
                return chd_error.CHDERR_DECOMPRESSION_ERROR;
        }
        return chd_error.CHDERR_NONE;
    }

}
