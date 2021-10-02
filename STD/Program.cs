using System;
using System.IO;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace STD
{
    internal class Program
    {
        static byte ByteFromTable(ulong[] table, int index)
        {
            return BitConverter.GetBytes(table[index / 8])[index % 8];
        }

        struct UnpackedHeader
        {
            public ulong[] Table;
            public ulong Seed;
            public byte ByteSeed;
            public uint DataOffset;
        }

        static UnpackedHeader UnpackHeader(byte[] header)
        {
            const int TableCount = 32;
            const int SeedOffset = 0xF8;
            const int DataOffsetOffset = 0x100;
            const ulong EndOfTable = 0x88F5D2ED13BFC313;

            UnpackedHeader unpacked = new();

            ulong[] table = new ulong[TableCount];
            unpacked.Table = table;
            unpacked.Seed = BitConverter.ToUInt64(header, SeedOffset);
            unpacked.DataOffset = BitConverter.ToUInt32(header, DataOffsetOffset);

            Vector128<ulong> seedVector = Vector128.Create(unpacked.Seed);

            for (int i = 0; i < TableCount / 2; i++)
            {
                /* Index into the 64-bit integers. */
                var j = i * 2;

                /* Load ith 128 bit integer. */
                Vector128<ulong> vector = Vector128.Create
                (
                    BitConverter.ToUInt64(header, i * 0x10),
                    BitConverter.ToUInt64(header, i * 0x10 + 8)
                );

                /* XOR vector with the seed. */
                vector = Sse2.Xor(vector, seedVector);

                /* Store the values into the table. */
                table[j]    = vector.GetLower().ToScalar();
                table[j+1]  = vector.GetUpper().ToScalar();
            }

            /* This is where the seed would be, so we bake the correct value in. */
            table[^1] = EndOfTable;

            /* Seed of bytes is just the sum of the bytes in the regular seed. */
            for (int i = 0; i < 8; i++)
                unpacked.ByteSeed += (byte) (unpacked.Seed >> i * 8);

            return unpacked;
        }

        static byte[] DecryptFile(FileInfo file)
        {
            /* How much to iterate over in bulk. */
            const int BulkSize = 0x800;
            const int HeaderSize = 0x800;

            using var stream = file.OpenRead();

            /* Read the header. */
            var headerData = new byte[HeaderSize];
            stream.Read(headerData);

            var header = UnpackHeader(headerData);
            var table = header.Table;

            /* Read out actual file data. */
            byte[] fileData = new byte[stream.Length - header.DataOffset];
            stream.Position = header.DataOffset;
            stream.Read(fileData);

            var pos = 0;
            for (int bulk = 0; bulk < fileData.Length / BulkSize; bulk++)
            {
                var seed = header.Seed;

                for (int i = 0; i < 0x100; i += 2)
                {
                    var tblIdx = i & 0x1E;

                    /* Read 2 u64s./ */
                    var lowerEnc = BitConverter.ToUInt64(fileData, pos);
                    var upperEnc = BitConverter.ToUInt64(fileData, pos + 8);

                    /* Decrypt. */
                    var lower = (lowerEnc - table[tblIdx]) ^ seed;
                    var upper = (upperEnc - table[tblIdx | 1]) ^ (lowerEnc + header.Seed);

                    /* Tick seed. */
                    seed = upperEnc + header.Seed;

                    /* Copy decrypted bytes back into buffer. */
                    Array.Copy(BitConverter.GetBytes(lower), 0, fileData, pos, 8);
                    Array.Copy(BitConverter.GetBytes(upper), 0, fileData, pos + 8, 8);

                    /* Move forward by 2 u64s./ */
                    pos += 16;
                }
            }

            var bytesLeft = (fileData.Length % BulkSize) - (fileData.Length & 1);

            byte bseed = header.ByteSeed;
            for (int i = 0; i < bytesLeft; i += 2)
            {
                var tblIdx = i & 0xFE;

                /* Read pair of bytes. */
                var lowerEnc = fileData[pos];
                var upperEnc = fileData[pos + 1];

                /* Decrypt. */
                var lower = (byte)((lowerEnc - ByteFromTable(table, tblIdx)) ^ bseed);
                var upper = (byte)((upperEnc - ByteFromTable(table, tblIdx | 1)) ^ (lowerEnc + header.ByteSeed));

                /* Tick seed. */
                bseed = (byte)(upperEnc + header.ByteSeed);

                /* Write pair back. */
                fileData[pos] = lower;
                fileData[pos + 1] = upper;

                pos += 2;
            }

            /* Handle the last byte, if needed. */
            if ((fileData.Length & 1) == 1)
            {
                fileData[^1] = (byte)((fileData[^1] - ByteFromTable(table, bytesLeft & ~1 & 0xFF)) ^ bseed);
            }

            return fileData;
        }

        static void Main(string[] args)
        {
            if(args.Length != 1)
            {
                Console.WriteLine("Expecting one argument.");
                return;
            }

            var encRoot = new DirectoryInfo(args[0]);
            if(!encRoot.Exists)
            {
                Console.WriteLine($"\"{encRoot.FullName}\" does not exist.");
                return;
            }

            var decRoot = encRoot.Parent.GetDirectory($"{encRoot.Name}-dec");
            foreach (var entry in encRoot.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var fileData = DecryptFile(entry);

                var relative = entry.FullName.Substring(encRoot.FullName.Length);
                /* Chop off any sperator chars at the start. */
                while (relative[0] == Path.DirectorySeparatorChar)
                    relative = relative[1..];

                var outfile = decRoot.GetFile(relative);
                outfile.Directory.Create();
                outfile.Delete();

                using var outstream = outfile.Create();
                outstream.Write(fileData);
            }

        }
    }
}