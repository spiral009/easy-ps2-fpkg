using CHDReaderTest.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CHDReaderTest;

internal static class CHDV5
{
    readonly static uint CHD_CODEC_NONE = 0;
    readonly static uint CHD_CODEC_ZLIB = CHD_MAKE_TAG('z', 'l', 'i', 'b');
    readonly static uint CHD_CODEC_LZMA = CHD_MAKE_TAG('l', 'z', 'm', 'a');
    readonly static uint CHD_CODEC_HUFFMAN = CHD_MAKE_TAG('h', 'u', 'f', 'f');
    readonly static uint CHD_CODEC_FLAC = CHD_MAKE_TAG('f', 'l', 'a', 'c');
    readonly static uint CHD_CODEC_AVHUFF = CHD_MAKE_TAG('a', 'v', 'h', 'u');
    /* general codecs with CD frontend */
    readonly static uint CHD_CODEC_CD_ZLIB = CHD_MAKE_TAG('c', 'd', 'z', 'l');
    readonly static uint CHD_CODEC_CD_LZMA = CHD_MAKE_TAG('c', 'd', 'l', 'z');
    readonly static uint CHD_CODEC_CD_FLAC = CHD_MAKE_TAG('c', 'd', 'f', 'l');

    internal static uint CHD_MAKE_TAG(char a, char b, char c, char d)
    {
        return (uint)(((a) << 24) | ((b) << 16) | ((c) << 8) | (d));
    }


    internal struct mapentry
    {
        public compression_type comptype;
        public uint length;
        public ulong offset;
        public ushort crc;
    }


    public static uint CHD_MDFLAGS_CHECKSUM = 0x01;        // indicates data is checksummed

    public static bool go(Stream file)
    {
        using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);

        uint[] compression = new uint[4];
        for (int i = 0; i < 4; i++)
            compression[i] = br.ReadUInt32BE();

        ulong totalbytes = br.ReadUInt64BE();  // total byte size of the image
        ulong mapoffset = br.ReadUInt64BE();
        ulong metaoffset = br.ReadUInt64BE();

        uint hunksize = br.ReadUInt32BE();    // length of a CHD Hunk (Block)
        uint unitbytes = br.ReadUInt32BE();
        byte[] rawsha1 = br.ReadBytes(20);
        byte[] sha1 = br.ReadBytes(20);
        byte[] parentsha1 = br.ReadBytes(20);

        uint hunkCount = (uint)((totalbytes + hunksize - 1) / hunksize);
        //uint unitcount = (uint)((logicalbytes + unitbytes - 1) / unitbytes);

        bool chdCompressed = compression[0] != CHD_CODEC_NONE;
        //uint mapentrybytes = (chdCompressed) ? (uint)12 : 4;

        mapentry[] map;
        chd_error err = chdCompressed ?
                decompress_v5_map(br, mapoffset, hunkCount, hunksize, unitbytes, out map) :
                uncompressed_v5_map(br, mapoffset, hunkCount, out map);

        if (err != chd_error.CHDERR_NONE)
            return false;

        using SHA1 sha1Check = SHA1.Create();

        byte[] buffer = new byte[hunksize];

        int block = 0;
        ulong sizetoGo = totalbytes;
        while (sizetoGo > 0)
        {
            /* progress */
            if ((block % 1000) == 0)
                Console.Write($"Verifying, {(100 - sizetoGo * 100 / totalbytes):N1}% complete...\r");

            err = readBlock(file, compression, block, map, (int)hunksize, buffer);
            if (err != chd_error.CHDERR_NONE)
                return false;

            int sizenext = sizetoGo > (ulong)hunksize ? (int)hunksize : (int)sizetoGo;

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

    // === easy-ps2-fpkg addition: extract the full decompressed image to a stream (DVD CHD -> ISO) ===
    internal static void Extract(Stream file, Stream outStream, Action<string> log)
    {
        using BinaryReader br = new BinaryReader(file, Encoding.UTF8, true);
        uint[] compression = new uint[4];
        for (int i = 0; i < 4; i++) compression[i] = br.ReadUInt32BE();
        ulong totalbytes = br.ReadUInt64BE();
        ulong mapoffset = br.ReadUInt64BE();
        ulong metaoffset = br.ReadUInt64BE();
        uint hunksize = br.ReadUInt32BE();
        uint unitbytes = br.ReadUInt32BE();
        br.ReadBytes(20); br.ReadBytes(20); br.ReadBytes(20); // rawsha1, sha1, parentsha1

        if (HasCdMetadata(file, br, metaoffset))
            throw new Exception("This is a CD-based CHD. Re-extract with `chdman extractcd` (or supply the .iso). DVD-based CHDs are supported directly.");

        uint hunkCount = (uint)((totalbytes + hunksize - 1) / hunksize);
        bool chdCompressed = compression[0] != CHD_CODEC_NONE;
        mapentry[] map;
        chd_error err = chdCompressed
            ? decompress_v5_map(br, mapoffset, hunkCount, hunksize, unitbytes, out map)
            : uncompressed_v5_map(br, mapoffset, hunkCount, out map);
        if (err != chd_error.CHDERR_NONE) throw new Exception("CHD map decode failed: " + err);

        byte[] buffer = new byte[hunksize];
        int block = 0; ulong sizetoGo = totalbytes; int lastpct = -1;
        while (sizetoGo > 0)
        {
            err = readBlock(file, compression, block, map, (int)hunksize, buffer);
            if (err != chd_error.CHDERR_NONE) throw new Exception($"CHD hunk {block} decode failed: {err}");
            int sizenext = sizetoGo > (ulong)hunksize ? (int)hunksize : (int)sizetoGo;
            outStream.Write(buffer, 0, sizenext);
            block++; sizetoGo -= (ulong)sizenext;
            int pct = (int)(100 - sizetoGo * 100 / totalbytes);
            if (log != null && pct / 10 != lastpct / 10) { lastpct = pct; log($"  decompressing CHD {pct}%"); }
        }
    }

    private static bool HasCdMetadata(Stream file, BinaryReader br, ulong metaoffset)
    {
        while (metaoffset != 0)
        {
            file.Seek((long)metaoffset, SeekOrigin.Begin);
            uint metaTag = br.ReadUInt32BE();
            uint metaLength = br.ReadUInt32BE();
            ulong metaNext = br.ReadUInt64BE();
            string tag = $"{(char)((metaTag >> 24) & 0xFF)}{(char)((metaTag >> 16) & 0xFF)}{(char)((metaTag >> 8) & 0xFF)}{(char)(metaTag & 0xFF)}";
            if (tag == "CHCD" || tag.StartsWith("CHT")) return true; // CD track metadata present
            metaoffset = metaNext;
        }
        return false;
    }

    private static chd_error uncompressed_v5_map(BinaryReader br, ulong mapoffset, uint hunkcount, out mapentry[] map)
    {
        br.BaseStream.Seek((long)mapoffset, SeekOrigin.Begin);

        map = new mapentry[hunkcount];
        for (int hunknum = 0; hunknum < hunkcount; hunknum++)
        {
            map[hunknum].comptype = compression_type.COMPRESSION_NONE;
            map[hunknum].offset = br.ReadUInt32BE();
        }
        return chd_error.CHDERR_NONE;

    }

    private static chd_error decompress_v5_map(BinaryReader br, ulong mapoffset, uint hunkcount, uint hunkbytes, uint unitbytes, out mapentry[] map)
    {
        map = new mapentry[hunkcount];

        /* read the reader */
        br.BaseStream.Seek((long)mapoffset, SeekOrigin.Begin);
        uint mapbytes = br.ReadUInt32BE();   //0
        ulong firstoffs = br.ReadUInt48BE(); //4
        ushort mapcrc = br.ReadUInt16BE();   //10
        byte lengthbits = br.ReadByte();     //12
        byte selfbits = br.ReadByte();       //13
        byte parentbits = br.ReadByte();     //14
                                             //15 not used

        byte[] compressed_arr = new byte[mapbytes];
        br.BaseStream.Seek((long)mapoffset + 16, SeekOrigin.Begin);
        br.BaseStream.Read(compressed_arr, 0, (int)mapbytes);

        BitStream bitbuf = new BitStream(compressed_arr);

        /* first decode the compression types */
        HuffmanDecoder decoder = new HuffmanDecoder(16, 8, bitbuf);
        if (decoder == null)
        {
            return chd_error.CHDERR_OUT_OF_MEMORY;
        }

        huffman_error err = decoder.ImportTreeRLE();
        if (err != huffman_error.HUFFERR_NONE)
        {
            return chd_error.CHDERR_DECOMPRESSION_ERROR;
        }

        int repcount = 0;
        compression_type lastcomp = 0;
        for (uint hunknum = 0; hunknum < hunkcount; hunknum++)
        {
            if (repcount > 0)
            {
                map[hunknum].comptype = lastcomp;
                repcount--;
            }
            else
            {
                compression_type val = (compression_type)decoder.DecodeOne();
                if (val == compression_type.COMPRESSION_RLE_SMALL)
                {
                    map[hunknum].comptype = lastcomp;
                    repcount = 2 + (int)decoder.DecodeOne();
                }
                else if (val == compression_type.COMPRESSION_RLE_LARGE)
                {
                    map[hunknum].comptype = lastcomp;
                    repcount = 2 + 16 + ((int)decoder.DecodeOne() << 4);
                    repcount += (int)decoder.DecodeOne();
                }
                else
                    map[hunknum].comptype = lastcomp = val;
            }
        }

        /* then iterate through the hunks and extract the needed data */
        uint last_self = 0;
        ulong last_parent = 0;
        ulong curoffset = firstoffs;
        for (uint hunknum = 0; hunknum < hunkcount; hunknum++)
        {
            ulong offset = curoffset;
            uint length = 0;
            ushort crc = 0;
            switch (map[hunknum].comptype)
            {
                /* base types */
                case compression_type.COMPRESSION_TYPE_0:
                case compression_type.COMPRESSION_TYPE_1:
                case compression_type.COMPRESSION_TYPE_2:
                case compression_type.COMPRESSION_TYPE_3:
                    curoffset += length = bitbuf.read(lengthbits);
                    crc = (ushort)bitbuf.read(16);
                    break;

                case compression_type.COMPRESSION_NONE:
                    curoffset += length = hunkbytes;
                    crc = (ushort)bitbuf.read(16);
                    break;

                case compression_type.COMPRESSION_SELF:
                    last_self = (uint)(offset = bitbuf.read(selfbits));
                    break;

                case compression_type.COMPRESSION_PARENT:
                    offset = bitbuf.read(parentbits);
                    last_parent = offset;
                    break;

                /* pseudo-types; convert into base types */
                case compression_type.COMPRESSION_SELF_1:
                    last_self++;
                    goto case compression_type.COMPRESSION_SELF_0;

                case compression_type.COMPRESSION_SELF_0:
                    map[hunknum].comptype = compression_type.COMPRESSION_SELF;
                    offset = last_self;
                    break;

                case compression_type.COMPRESSION_PARENT_SELF:
                    map[hunknum].comptype = compression_type.COMPRESSION_PARENT;
                    last_parent = offset = (((ulong)hunknum) * ((ulong)hunkbytes)) / unitbytes;
                    break;

                case compression_type.COMPRESSION_PARENT_1:
                    last_parent += hunkbytes / unitbytes;
                    goto case compression_type.COMPRESSION_PARENT_0;
                case compression_type.COMPRESSION_PARENT_0:
                    map[hunknum].comptype = compression_type.COMPRESSION_PARENT;
                    offset = last_parent;
                    break;
            }
            map[hunknum].length = length;
            map[hunknum].offset = offset;
            map[hunknum].crc = crc;
        }


        /* verify the final CRC */
        byte[] rawmap = new byte[hunkcount * 12];
        for (int hunknum = 0; hunknum < hunkcount; hunknum++)
        {
            int rawmapIndex = hunknum * 12;
            rawmap[rawmapIndex] = (byte)map[hunknum].comptype;
            rawmap.PutUInt24BE(rawmapIndex + 1, map[hunknum].length);
            rawmap.PutUInt48BE(rawmapIndex + 4, map[hunknum].offset);
            rawmap.PutUInt16BE(rawmapIndex + 10, map[hunknum].crc);
        }
        if (CRC16.calc(rawmap, (int)hunkcount * 12) != mapcrc)
            return chd_error.CHDERR_DECOMPRESSION_ERROR;

        return chd_error.CHDERR_NONE;
    }


    private static chd_error readBlock(Stream file, uint[] compression, int hunkindex, mapentry[] map, int hunksize, byte[] cache)
    {
        long blockoffs;
        mapentry mapentry = map[hunkindex];

        if (compression[0] == CHD_CODEC_NONE)
        {
            blockoffs = (long)mapentry.offset * hunksize;
            if (blockoffs != 0)
            {
                file.Seek((long)blockoffs, SeekOrigin.Begin);
                file.Read(cache, 0, (int)hunksize);
            }
            else
            {
                for (int j = 0; j < hunksize; j++)
                    cache[j] = 0;
            }
            return chd_error.CHDERR_NONE;
        }

        switch (mapentry.comptype)
        {
            case compression_type.COMPRESSION_TYPE_0:
            case compression_type.COMPRESSION_TYPE_1:
            case compression_type.COMPRESSION_TYPE_2:
            case compression_type.COMPRESSION_TYPE_3:
                file.Seek((long)mapentry.offset, SeekOrigin.Begin);
                byte[] source = new byte[mapentry.length];
                file.Read(source, 0, source.Length);
                //for (int i = 0; i < cache.Length; i++)
                //    cache[i] = 0;

                uint comp = compression[(int)mapentry.comptype];
                chd_error ret = chd_error.CHDERR_UNSUPPORTED_FORMAT;
                if (comp == CHD_CODEC_ZLIB)
                    ret = CHDReaders.zlib(source, cache);
                else if (comp == CHD_CODEC_LZMA)
                    ret = CHDReaders.lzma(source, cache);
                else if (comp == CHD_CODEC_HUFFMAN)
                    ret = CHDReaders.huffman(source, cache);
                else if (comp == CHD_CODEC_FLAC)
                    ret = CHDReaders.flac(source, cache);
                else if (comp == CHD_CODEC_CD_ZLIB)
                    ret = CHDReaders.cdzlib(source, cache);
                else if (comp == CHD_CODEC_CD_LZMA)
                    ret = CHDReaders.cdlzma(source, cache);
                else if (comp == CHD_CODEC_CD_FLAC)
                    ret = CHDReaders.cdflac(source, cache);
                else if (comp == CHD_CODEC_AVHUFF)
                    ret = CHDReaders.avHuff(source, cache);
                else
                    Console.WriteLine("Unknown compression type");

                if (ret != chd_error.CHDERR_NONE)
                    return ret;

                if (CRC16.calc(cache, hunksize) != mapentry.crc)
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;
                break;

            case compression_type.COMPRESSION_NONE:
                //Console.WriteLine("Compression_None");
                file.Seek((long)mapentry.offset, SeekOrigin.Begin);
                if (mapentry.length != hunksize)
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;
                file.Read(cache, 0, (int)hunksize);

                if (CRC16.calc(cache, hunksize) != mapentry.crc)
                    return chd_error.CHDERR_DECOMPRESSION_ERROR;

                break;
            case compression_type.COMPRESSION_SELF:
                //Console.WriteLine("Compression_Self");
                return readBlock(file, compression, (int)mapentry.offset, map, hunksize, cache);

            case compression_type.COMPRESSION_PARENT:
                Console.WriteLine("Compression_Parent");
                return chd_error.CHDERR_INVALID_FILE;
        }


        return chd_error.CHDERR_NONE;
    }

    public enum compression_type
    {
        /* codec #0
         * these types are live when running */
        COMPRESSION_TYPE_0 = 0,
        /* codec #1 */
        COMPRESSION_TYPE_1 = 1,
        /* codec #2 */
        COMPRESSION_TYPE_2 = 2,
        /* codec #3 */
        COMPRESSION_TYPE_3 = 3,
        /* no compression; implicit length = hunkbytes */
        COMPRESSION_NONE = 4,
        /* same as another block in this chd */
        COMPRESSION_SELF = 5,
        /* same as a hunk's worth of units in the parent chd */
        COMPRESSION_PARENT = 6,

        /* start of small RLE run (4-bit length)
         * these additional pseudo-types are used for compressed encodings: */
        COMPRESSION_RLE_SMALL,
        /* start of large RLE run (8-bit length) */
        COMPRESSION_RLE_LARGE,
        /* same as the last COMPRESSION_SELF block */
        COMPRESSION_SELF_0,
        /* same as the last COMPRESSION_SELF block + 1 */
        COMPRESSION_SELF_1,
        /* same block in the parent */
        COMPRESSION_PARENT_SELF,
        /* same as the last COMPRESSION_PARENT block */
        COMPRESSION_PARENT_0,
        /* same as the last COMPRESSION_PARENT block + 1 */
        COMPRESSION_PARENT_1
    };


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


}