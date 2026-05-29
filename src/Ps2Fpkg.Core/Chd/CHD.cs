using System;
using System.IO;

namespace CHDReaderTest;

public static class CHD
{
    public static void TestCHD(string filename)
    {
        Console.WriteLine("");
        Console.WriteLine($"Testing :{filename}");
        using (Stream s = File.Open(filename, FileMode.Open, FileAccess.Read))
        {
            if (!CHDVersion.CheckHeader(s, out uint length, out uint version))
                return;

            Console.WriteLine($@"CHD Version {version}");

            bool valid = true;
            switch (version)
            {
                case 1:
                    valid = CHDV1.go(s);
                    break;
                case 2:
                    valid = CHDV2.go(s);
                    break;
                case 3:
                    valid = CHDV3.go(s);
                    break;
                case 4:
                    valid = CHDV4.go(s);
                    break;
                case 5:
                    valid = CHDV5.go(s);
                    break;
                default:
                    Console.WriteLine($"Unknown version {version}");
                    return;
            }
            var fc = Console.ForegroundColor;
            if (!valid)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Failed");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Valid");
            }
            Console.ForegroundColor = fc;
        }
    }
}

[Flags]
public enum mapFlags
{
    MAP_ENTRY_FLAG_TYPE_MASK = 0x000f,      /* what type of hunk */
    MAP_ENTRY_FLAG_NO_CRC = 0x0010,         /* no CRC is present */

    MAP_ENTRY_TYPE_INVALID = 0x0000,        /* invalid type */
    MAP_ENTRY_TYPE_COMPRESSED = 0x0001,     /* standard compression */
    MAP_ENTRY_TYPE_UNCOMPRESSED = 0x0002,   /* uncompressed data */
    MAP_ENTRY_TYPE_MINI = 0x0003,           /* mini: use offset as raw data */
    MAP_ENTRY_TYPE_SELF_HUNK = 0x0004,      /* same as another hunk in this file */
    MAP_ENTRY_TYPE_PARENT_HUNK = 0x0005     /* same as a hunk in the parent file */
}

enum chd_error
{
    CHDERR_NONE,
    CHDERR_NO_INTERFACE,
    CHDERR_OUT_OF_MEMORY,
    CHDERR_INVALID_FILE,
    CHDERR_INVALID_PARAMETER,
    CHDERR_INVALID_DATA,
    CHDERR_FILE_NOT_FOUND,
    CHDERR_REQUIRES_PARENT,
    CHDERR_FILE_NOT_WRITEABLE,
    CHDERR_READ_ERROR,
    CHDERR_WRITE_ERROR,
    CHDERR_CODEC_ERROR,
    CHDERR_INVALID_PARENT,
    CHDERR_HUNK_OUT_OF_RANGE,
    CHDERR_DECOMPRESSION_ERROR,
    CHDERR_COMPRESSION_ERROR,
    CHDERR_CANT_CREATE_FILE,
    CHDERR_CANT_VERIFY,
    CHDERR_NOT_SUPPORTED,
    CHDERR_METADATA_NOT_FOUND,
    CHDERR_INVALID_METADATA_SIZE,
    CHDERR_UNSUPPORTED_VERSION,
    CHDERR_VERIFY_INCOMPLETE,
    CHDERR_INVALID_METADATA,
    CHDERR_INVALID_STATE,
    CHDERR_OPERATION_PENDING,
    CHDERR_NO_ASYNC_OPERATION,
    CHDERR_UNSUPPORTED_FORMAT
};
