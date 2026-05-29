using CHDReaderTest.Flac.FlacDeps;
using CHDReaderTest.Utils;
using Compress.Support.Compression.LZMA;
using CUETools.Codecs.Flake;
using System;
using System.IO;
using System.IO.Compression;

namespace CHDReaderTest;

internal static partial class CHDReaders
{



    internal static chd_error zlib(byte[] source, byte[] cache)
    {
        return zlib(source, 0, source.Length, cache);
    }
    internal static chd_error zlib(byte[] source, int start, int compsize, byte[] cache)
    {
        using var memStream = new MemoryStream(source, start, compsize);
        using var compStream = new DeflateStream(memStream, CompressionMode.Decompress, true);
        int bytesRead = 0;
        while (bytesRead < cache.Length)
        {
            int bytes = compStream.Read(cache, bytesRead, cache.Length - bytesRead);
            if (bytes == 0)
                return chd_error.CHDERR_INVALID_DATA;
            bytesRead += bytes;
        }
        return chd_error.CHDERR_NONE;
    }






    internal static chd_error lzma(byte[] source, byte[] cache)
    {
        return lzma(source, 0, source.Length, cache);
    }
    internal static chd_error lzma(byte[] source, int start, int compsize, byte[] cache)
    {
        //hacky header creator
        byte[] properties = new byte[5];
        int posStateBits = 2;
        int numLiteralPosStateBits = 0;
        int numLiteralContextBits = 3;
        int dictionarySize = cache.Length;
        properties[0] = (byte)((posStateBits * 5 + numLiteralPosStateBits) * 9 + numLiteralContextBits);
        for (int j = 0; j < 4; j++)
            properties[1 + j] = (Byte)((dictionarySize >> (8 * j)) & 0xFF);


        using var memStream = new MemoryStream(source, start, compsize);
        using Stream compStream = new LzmaStream(properties, memStream);
        int bytesRead = 0;
        while (bytesRead < cache.Length)
        {
            int bytes = compStream.Read(cache, bytesRead, cache.Length - bytesRead);
            if (bytes == 0)
                return chd_error.CHDERR_INVALID_DATA;
            bytesRead += bytes;
        }

        return chd_error.CHDERR_NONE;
    }





    internal static chd_error huffman(byte[] source, byte[] cache)
    {
        BitStream bitbuf = new BitStream(source);
        HuffmanDecoder hd = new HuffmanDecoder(256, 16, bitbuf);

        if (hd.ImportTreeHuffman() != huffman_error.HUFFERR_NONE)
            return chd_error.CHDERR_INVALID_DATA;

        for (int j = 0; j < cache.Length; j++)
        {
            cache[j] = (byte)hd.DecodeOne();
        }
        return chd_error.CHDERR_NONE;
    }





    internal static chd_error flac(byte[] source, byte[] cache)
    {
        byte endianType = source[0];
        //CHD adds a leading char to indicate endian. Not part of the flac format.
        bool swapEndian = (endianType == 'B'); //'L'ittle / 'B'ig
        return flac(source, 1, cache, swapEndian, out _);
    }
    internal static chd_error flac(byte[] source, int start, byte[] cache, bool swapEndian, out int srcPos)
    {
        //hard coded in libchdr
        int sampleBits = 16;
        int sampleRate = 44100;
        int channels = 2;

        AudioPCMConfig settings = new AudioPCMConfig(sampleBits, channels, sampleRate);
        AudioDecoder audioDecoder = new AudioDecoder(settings); //read the data and decode it in to a 1D array of samples - the buffer seems to want 2D :S
        AudioBuffer audioBuffer = new AudioBuffer(settings, cache.Length); //audio buffer to take decoded samples and read them to bytes.
        int read;
        srcPos = start;
        int dstPos = 0;
        //this may require some error handling. Hopefully the while condition is reliable
        while (dstPos < cache.Length)
        {
            if ((read = audioDecoder.DecodeFrame(source, srcPos, source.Length - srcPos)) == 0)
                break;
            if (audioDecoder.Remaining != 0)
            {
                audioDecoder.Read(audioBuffer, (int)audioDecoder.Remaining);
                Array.Copy(audioBuffer.Bytes, 0, cache, dstPos, audioBuffer.ByteLength);
                dstPos += audioBuffer.ByteLength;
            }
            srcPos += read;
        }

        //Nanook - hack to support 16bit byte flipping - tested passes hunk CRC test
        if (swapEndian)
        {
            byte tmp;
            for (int i = 0; i < cache.Length; i += 2)
            {
                tmp = cache[i];
                cache[i] = cache[i + 1];
                cache[i + 1] = tmp;
            }
        }

        return chd_error.CHDERR_NONE;
    }



    /******************* CD decoders **************************/



    private const int CD_MAX_SECTOR_DATA = 2352;
    private const int CD_MAX_SUBCODE_DATA = 96;
    private static readonly int CD_FRAME_SIZE = CD_MAX_SECTOR_DATA + CD_MAX_SUBCODE_DATA;

    private static readonly byte[] s_cd_sync_header = new byte[] { 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00 };

    internal static chd_error cdzlib(byte[] source, byte[] dest)
    {
        /* determine header bytes */
        int frames = dest.Length / CD_FRAME_SIZE;
        int complen_bytes = (dest.Length < 65536) ? 2 : 3;
        int ecc_bytes = (frames + 7) / 8;
        int header_bytes = ecc_bytes + complen_bytes;

        /* extract compressed length of base */
        int complen_base = (source[ecc_bytes + 0] << 8) | source[ecc_bytes + 1];
        if (complen_bytes > 2)
            complen_base = (complen_base << 8) | source[ecc_bytes + 2];

        byte[] bSector = new byte[frames * CD_MAX_SECTOR_DATA];
        byte[] bSubcode = new byte[frames * CD_MAX_SUBCODE_DATA];

        chd_error err = zlib(source, (int)header_bytes, complen_base, bSector);
        if (err != chd_error.CHDERR_NONE)
            return err;

        err = zlib(source, header_bytes + complen_base, source.Length - header_bytes - complen_base, bSubcode);
        if (err != chd_error.CHDERR_NONE)
            return err;

        /* reassemble the data */
        for (int framenum = 0; framenum < frames; framenum++)
        {
            Array.Copy(bSector, framenum * CD_MAX_SECTOR_DATA, dest, framenum * CD_FRAME_SIZE, CD_MAX_SECTOR_DATA);
            Array.Copy(bSubcode, framenum * CD_MAX_SUBCODE_DATA, dest, framenum * CD_FRAME_SIZE + CD_MAX_SECTOR_DATA, CD_MAX_SUBCODE_DATA);

            // reconstitute the ECC data and sync header 
            int sectorStart = framenum * CD_FRAME_SIZE;
            if ((source[framenum / 8] & (1 << (framenum % 8))) != 0)
            {
                Array.Copy(s_cd_sync_header, 0, dest, sectorStart, s_cd_sync_header.Length);
                cdRom.ecc_generate(dest, sectorStart);
            }
        }
        return chd_error.CHDERR_NONE;
    }


    internal static chd_error cdlzma(byte[] source, byte[] dest)
    {
        /* determine header bytes */
        int frames = dest.Length / CD_FRAME_SIZE;
        int complen_bytes = (dest.Length < 65536) ? 2 : 3;
        int ecc_bytes = (frames + 7) / 8;
        int header_bytes = ecc_bytes + complen_bytes;

        /* extract compressed length of base */
        int complen_base = ((source[ecc_bytes + 0] << 8) | source[ecc_bytes + 1]);
        if (complen_bytes > 2)
            complen_base = (complen_base << 8) | source[ecc_bytes + 2];

        byte[] bSector = new byte[frames * CD_MAX_SECTOR_DATA];
        byte[] bSubcode = new byte[frames * CD_MAX_SUBCODE_DATA];

        chd_error err = lzma(source, header_bytes, complen_base, bSector);
        if (err != chd_error.CHDERR_NONE)
            return err;

        err = zlib(source, header_bytes + complen_base, source.Length - header_bytes - complen_base, bSubcode);
        if (err != chd_error.CHDERR_NONE)
            return err;

        /* reassemble the data */
        for (int framenum = 0; framenum < frames; framenum++)
        {
            Array.Copy(bSector, framenum * CD_MAX_SECTOR_DATA, dest, framenum * CD_FRAME_SIZE, CD_MAX_SECTOR_DATA);
            Array.Copy(bSubcode, framenum * CD_MAX_SUBCODE_DATA, dest, framenum * CD_FRAME_SIZE + CD_MAX_SECTOR_DATA, CD_MAX_SUBCODE_DATA);

            // reconstitute the ECC data and sync header 
            int sectorStart = framenum * CD_FRAME_SIZE;
            if ((source[framenum / 8] & (1 << (framenum % 8))) != 0)
            {
                Array.Copy(s_cd_sync_header, 0, dest, sectorStart, s_cd_sync_header.Length);
                cdRom.ecc_generate(dest, sectorStart);
            }
        }
        return chd_error.CHDERR_NONE;
    }


    internal static chd_error cdflac(byte[] source, byte[] dest)
    {
        int frames = dest.Length / CD_FRAME_SIZE;

        byte[] bSector = new byte[frames * CD_MAX_SECTOR_DATA];
        byte[] bSubcode = new byte[frames * CD_MAX_SUBCODE_DATA];

        chd_error err = flac(source, 0, bSector, true, out int pos);
        if (err != chd_error.CHDERR_NONE)
            return err;

        err = zlib(source, pos, source.Length - pos, bSubcode);
        if (err != chd_error.CHDERR_NONE)
            return err;

        /* reassemble the data */
        for (int framenum = 0; framenum < frames; framenum++)
        {
            Array.Copy(bSector, framenum * CD_MAX_SECTOR_DATA, dest, framenum * CD_FRAME_SIZE, CD_MAX_SECTOR_DATA);
            Array.Copy(bSubcode, framenum * CD_MAX_SUBCODE_DATA, dest, framenum * CD_FRAME_SIZE + CD_MAX_SECTOR_DATA, CD_MAX_SUBCODE_DATA);
        }
        return chd_error.CHDERR_NONE;
    }
}