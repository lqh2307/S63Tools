﻿using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Text;

namespace S63Tools;

public class S63Tools
{
    private static readonly byte[] Hex = "0123456789ABCDEF"u8.ToArray();

    public static string CreateUserPermit(byte[] hwId, byte[] key, ushort mId)
    {
        if (hwId.Length != 5)
        {
            throw new Exception("Invalid HW ID length.");
        }

        Span<byte> hwIdBlock = stackalloc byte[8];
        hwId.CopyTo(hwIdBlock);
        hwIdBlock[5] = 3;
        hwIdBlock[6] = 3;
        hwIdBlock[7] = 3;

        var blow = new BlowFish(key);
        var enc = blow.EncryptECB(hwIdBlock);

        var permit = new byte[28];
        for (int i = 0; i < enc.Length; i++)
        {
            Utf8Formatter.TryFormat(enc[i], permit.AsSpan(2 * i), out _, new StandardFormat('X', 2));
        }

        uint crc = Crc32.Compute(permit.AsSpan(0, 16));
        Utf8Formatter.TryFormat(crc, permit.AsSpan(16), out _, new StandardFormat('X', 8));
        Utf8Formatter.TryFormat(mId, permit.AsSpan(24), out _, new StandardFormat('X', 4));

        return Encoding.ASCII.GetString(permit);
    }

    public static byte[] DecryptUserPermit(string userPermit, byte[] key, out ushort mId)
    {
        var permit = Encoding.ASCII.GetBytes(userPermit);
        uint crc = Crc32.Compute(permit.AsSpan(0, 16));

        Utf8Parser.TryParse(permit.AsSpan(16, 8), out uint crc2, out _, 'X');

        if (crc != crc2)
        {
            throw new Exception("Invalid CRC.");
        }

        Utf8Parser.TryParse(permit.AsSpan(24, 4), out mId, out _, 'X');

        var blow = new BlowFish(key);
        Span<byte> hwIdBlock = stackalloc byte[8];
        for (int i = 0; i < hwIdBlock.Length; i++)
        {
            Utf8Parser.TryParse(permit.AsSpan(i * 2, 2), out byte b, out _, 'X');
            hwIdBlock[i] = b;
        }

        hwIdBlock = blow.DecryptCBC(hwIdBlock);

        if (hwIdBlock.Length != 8)
        {
            throw new Exception("Invalid HW ID length.");
        }

        if (hwIdBlock[5] != 3 || hwIdBlock[6] != 3 || hwIdBlock[7] != 3)
        {
            throw new Exception("Invalid HW ID.");
        }

        return hwIdBlock.Slice(0, 5).ToArray();
    }

    public static string CreateCellPermit(byte[] hwId, string cellName, DateTime expiryDate, byte[] ck1, byte[] ck2)
    {
        if (hwId.Length != 5)
        {
            throw new Exception("Invalid HW ID length.");
        }

        if (ck1.Length != 5)
        {
            throw new Exception("Invalid Cell Key 1 length.");
        }

        if (ck2.Length != 5)
        {
            throw new Exception("Invalid Cell Key 2 length.");
        }
            
        var hwId6 = new byte[6];
        hwId.AsSpan().CopyTo(hwId6);
        hwId6[5] = hwId[0];

        var permit = new byte[64];
        Encoding.ASCII.GetBytes(cellName, permit.AsSpan());
        Utf8Formatter.TryFormat(expiryDate.Year, permit.AsSpan(8), out _, new StandardFormat('D', 4));
        Utf8Formatter.TryFormat(expiryDate.Month, permit.AsSpan(12), out _, new StandardFormat('D', 2));
        Utf8Formatter.TryFormat(expiryDate.Day, permit.AsSpan(14), out _, new StandardFormat('D', 2));

        Span<byte> block = stackalloc byte[8];
        var blow = new BlowFish(hwId6);

        ck1.CopyTo(block);
        block[5] = 3;
        block[6] = 3;
        block[7] = 3;

        var eck = blow.EncryptECB(block);
        for (int i = 0; i < eck.Length; i++)
        {
            Utf8Formatter.TryFormat(eck[i], permit.AsSpan(16 + 2 * i), out _, new StandardFormat('X', 2));
        }

        ck2.CopyTo(block);
        block[5] = 3;
        block[6] = 3;
        block[7] = 3;

        eck = blow.EncryptECB(block);
        for (int i = 0; i < eck.Length; i++)
        {
            Utf8Formatter.TryFormat(eck[i], permit.AsSpan(32 + 2 * i), out _, new StandardFormat('X', 2));
        }

        uint crc = Crc32.Compute(permit.AsSpan(0, 48));

        BinaryPrimitives.TryWriteUInt32BigEndian(block, crc);
        block[4] = 4;
        block[5] = 4;
        block[6] = 4;
        block[7] = 4;

        var encHash = blow.EncryptECB(block);
        for (int i = 0; i < encHash.Length; i++)
        {
            Utf8Formatter.TryFormat(encHash[i], permit.AsSpan(48 + 2 * i), out _, new StandardFormat('X', 2));
        }

        return Encoding.ASCII.GetString(permit);
    }

    public static bool TryDecryptCellPermit(string cellPermit, byte[] hwId, out byte[] ck1, out byte[] ck2)
    {
        var permit = Encoding.ASCII.GetBytes(cellPermit);
        uint crc = Crc32.Compute(permit.AsSpan(0, 48));

        Span<byte> hwId6 = stackalloc byte[6];
        hwId.AsSpan().CopyTo(hwId6);
        hwId6[5] = hwId[0];

        Span<byte> block = stackalloc byte[8];
        var blow = new BlowFish(hwId6);

        for (int i = 0; i < 8; i++)
        {
            Utf8Parser.TryParse(permit.AsSpan(48 + i * 2, 2), out byte b, out _, 'X');
            block[i] = b;
        }

        var crcBlock = blow.DecryptCBC(block);
        if (crcBlock[4] != 4 || crcBlock[5] != 4 || crcBlock[6] != 4 || crcBlock[7] != 4)
        {
            // Invalid CRC.
            ck1 = null;
            ck2 = null;
            return false;
        }

        uint crc2 = BinaryPrimitives.ReadUInt32BigEndian(crcBlock);
        if (crc != crc2)
        {
            // Invalid CRC.
            ck1 = null;
            ck2 = null;
            return false;
        }

        for (int i = 0; i < 8; i++)
        {
            Utf8Parser.TryParse(permit.AsSpan(16 + i * 2, 2), out byte b, out _, 'X');
            block[i] = b;
        }

        var ck1Block = blow.DecryptCBC(block);
        if (ck1Block[5] != 3 || ck1Block[6] != 3 || ck1Block[7] != 3)
        {
            // Invalid Cell Key 1.
            ck1 = null;
            ck2 = null;
            return false;
        }

        ck1 = ck1Block.AsSpan(0, 5).ToArray();

        for (int i = 0; i < 8; i++)
        {
            Utf8Parser.TryParse(permit.AsSpan(32 + i * 2, 2), out byte b, out _, 'X');
            block[i] = b;
        }

        var ck2Block = blow.DecryptCBC(block);
        if (ck2Block[5] != 3 || ck2Block[6] != 3 || ck2Block[7] != 3)
        {
            // Invalid Cell Key 2.
            ck1 = null;
            ck2 = null;
            return false;
        }

        ck2 = ck2Block.AsSpan(0, 5).ToArray();
        return true;
    }

    public static byte[]? HackUserPermit(string userPermit, out ushort mId, out byte[]? keyBytes)
    {
        Console.WriteLine($"[INFO] Starting HackUserPermit with userPermit: {userPermit}");

        var permit = Encoding.ASCII.GetBytes(userPermit);
        Console.WriteLine($"[DEBUG] Permit bytes: {BitConverter.ToString(permit)}");

        if (permit.Length < 28)
        {
            Console.WriteLine("[ERROR] Invalid user permit length.");
            throw new Exception("Invalid user permit.");
        }

        uint crc = Crc32.Compute(permit.AsSpan(0, 16));
        Console.WriteLine($"[DEBUG] Computed CRC: {crc:X8}");

        Utf8Parser.TryParse(permit.AsSpan(16, 8), out uint crc2, out _, 'X');
        Console.WriteLine($"[DEBUG] Extracted CRC from permit: {crc2:X8}");

        if (crc != crc2)
        {
            Console.WriteLine("[ERROR] CRC mismatch! Permit is invalid.");
            throw new Exception("Invalid CRC.");
        }

        Utf8Parser.TryParse(permit.AsSpan(24, 4), out mId, out _, 'X');
        Console.WriteLine($"[DEBUG] Extracted mId: {mId:X4}");

        Span<byte> hwIdBlockDef = stackalloc byte[8];
        for (int i = 0; i < hwIdBlockDef.Length; i++)
        {
            Utf8Parser.TryParse(permit.AsSpan(i * 2, 2), out byte b, out _, 'X');
            hwIdBlockDef[i] = b;
        }
        Console.WriteLine($"[DEBUG] Extracted hwIdBlockDef: {BitConverter.ToString(hwIdBlockDef.ToArray())}");

        long t = Stopwatch.GetTimestamp();
        var finder = new KeyFinder(hwIdBlockDef);
        
        Console.WriteLine("[INFO] Starting KeyFinder...");
        finder.FindKey();
        
        var elapsed = Stopwatch.GetElapsedTime(t);
        Console.WriteLine($"[INFO] KeyFinder elapsed time: {elapsed.TotalMilliseconds} ms");

        keyBytes = finder.FoundKey;
        Console.WriteLine($"[DEBUG] FoundKey: {BitConverter.ToString(keyBytes ?? Array.Empty<byte>())}");

        var foundHwId = finder.FoundHwId;
        Console.WriteLine($"[DEBUG] FoundHwId: {BitConverter.ToString(foundHwId ?? Array.Empty<byte>())}");

        return foundHwId;
    }

    public static byte[]? HackCellPermit(string permitPath)
    {
        Console.WriteLine($"[INFO] Processing permit file: {permitPath}");

        var lines = File.ReadAllLines(permitPath);
        string? cellPermit = null;

        foreach (string line in lines)
        {
            if (line.StartsWith(':'))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length > 2)
            {
                cellPermit = parts[0];
                Console.WriteLine($"[DEBUG] Found cell permit: {cellPermit}");
            }
        }

        if (cellPermit != null)
        {
            var permit = Encoding.ASCII.GetBytes(cellPermit);
            uint crc = Crc32.Compute(permit.AsSpan(0, 48));
            Console.WriteLine($"[DEBUG] Computed CRC32: {crc:X8}");

            Span<byte> blockData = stackalloc byte[24];
            for (int i = 0; i < 24; i++)
            {
                Utf8Parser.TryParse(permit.AsSpan(16 + i * 2, 2), out byte b, out _, 'X');
                blockData[i] = b;
            }

            Console.WriteLine("[INFO] Initializing KeyFinder...");
            long t = Stopwatch.GetTimestamp();
            var finder = new KeyFinder(blockData, crc);
            finder.FindHardwareId();
            var elapsed = Stopwatch.GetElapsedTime(t);

            Console.WriteLine($"[INFO] KeyFinder completed in {elapsed.TotalMilliseconds} ms.");
            Console.WriteLine($"[DEBUG] Found Hardware ID: {BitConverter.ToString(finder.FoundHwId ?? Array.Empty<byte>())}");

            return finder.FoundHwId;
        }

        Console.WriteLine("[ERROR] Failed to decrypt the permits.");
        throw new Exception("Failed to decrypt the permits.");
    }

    private class KeyFinder
    {
        private readonly byte[] _blockData;
        private readonly uint _crc;
        private int _i;

        public volatile byte[]? FoundKey;
        public volatile byte[]? FoundHwId;

        public KeyFinder(Span<byte> hwIdBlockDef, uint crc = 0)
        {
            _blockData = hwIdBlockDef.ToArray();
            _crc = crc;
        }

        private void FindKeyThreadFunc()
        {
            var key = new byte[5];
            Span<byte> hwIdBlock = stackalloc byte[8];
            var blow = new BlowFish();

            while (true)
            {
                int i = Interlocked.Increment(ref _i) - 1;
                if (i >= 0x100000 || FoundKey != null)
                {
                    break;
                }

                key[4] = Hex[i & 0xf];
                key[3] = Hex[(i >> 4) & 0xf];
                key[2] = Hex[(i >> 8) & 0xf];
                key[1] = Hex[(i >> 12) & 0xf];
                key[0] = Hex[(i >> 16) & 0xf];

                blow.SetupKey5(key);

                _blockData.CopyTo(hwIdBlock);
                blow.DecryptCBC8(hwIdBlock);

                if (hwIdBlock[5] != 3 || hwIdBlock[6] != 3 || hwIdBlock[7] != 3)
                {
                    continue;
                }

                FoundKey = key;
                FoundHwId = hwIdBlock.Slice(0, 5).ToArray();
                break;
            }
        }

        private void FindHwIdThreadFunc()
        {
            Span<byte> hwId6 = stackalloc byte[6];
            var block1 = _blockData.AsSpan(0, 8);
            var block2 = _blockData.AsSpan(8, 8);
            var blockCrc = _blockData.AsSpan(16, 8);
            while (true)
            {
                int i = Interlocked.Increment(ref _i) - 1;
                if (i >= 0x100000 || FoundHwId != null)
                {
                    break;
                }

                hwId6[4] = Hex[i & 0xf];
                hwId6[3] = Hex[(i >> 4) & 0xf];
                hwId6[2] = Hex[(i >> 8) & 0xf];
                hwId6[1] = Hex[(i >> 12) & 0xf];
                hwId6[0] = Hex[(i >> 16) & 0xf];
                hwId6[5] = hwId6[0];

                var blow = new BlowFish(hwId6);

                var crcBlock = blow.DecryptCBC(blockCrc);
                if (crcBlock[4] != 4 || crcBlock[5] != 4 || crcBlock[6] != 4 || crcBlock[7] != 4)
                {
                    // Invalid CRC.
                    continue;
                }

                uint crc = BinaryPrimitives.ReadUInt32BigEndian(crcBlock);
                if (_crc != crc)
                {
                    // Invalid CRC.
                    continue;
                }

                var ck1Block = blow.DecryptCBC(block1);
                if (ck1Block[5] != 3 || ck1Block[6] != 3 || ck1Block[7] != 3)
                {
                    // Invalid Cell Key 1.
                    continue;
                }

                var ck2Block = blow.DecryptCBC(block2);
                if (ck2Block[5] != 3 || ck2Block[6] != 3 || ck2Block[7] != 3)
                {
                    // Invalid Cell Key 2.
                    continue;
                }

                //ck1 = ck1Block.AsSpan(0, 5).ToArray();
                //ck2 = ck2Block.AsSpan(0, 5).ToArray();
                FoundHwId = hwId6.Slice(0, 5).ToArray();
                break;
            }
        }

        private void Start(ThreadStart threadFunc)
        {
            var threads = new List<Thread>();
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                var t = new Thread(threadFunc);
                threads.Add(t);
                t.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }
        }

        public void FindKey()
        {
            Start(FindKeyThreadFunc);
        }

        public void FindHardwareId()
        {
            Start(FindHwIdThreadFunc);
        }
    }

    public static void LoadPermit(string permitPath, Dictionary<string, (byte[], byte[])> permits, byte[][] hardwareIds)
    {
        Exception exception = null;
        foreach (var hardwareId in hardwareIds)
        {
            try
            {
                if (TryLoadPermit(permitPath, permits, hardwareId))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        }

        if (exception != null)
        {
            throw exception;
        }

        throw new Exception("Failed to load the permits.");
    }

    public static bool TryLoadPermit(string permitPath, Dictionary<string, (byte[], byte[])> permits, byte[] hardwareId)
    {
        var lines = File.ReadAllLines(permitPath);
        foreach (string line in lines)
        {
            if (line.StartsWith(':'))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length > 2)
            {
                string permit = parts[0];
                if (!TryDecryptCellPermit(permit, hardwareId, out var ck1, out var ck2))
                {
                    return false;
                }

                string cellName = permit.Substring(0, 8);
                permits[cellName] = (ck1, ck2);
            }
        }

        return true;
    }
}
