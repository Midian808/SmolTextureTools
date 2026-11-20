using System;
using System.IO;
using System.IO.Compression;

namespace Midian.TextureTools
{
    //Writes 16-bit-per-channel PNGs. Unity's EncodeToPNG writes 8 bits per channel even from RGBA64 / RGB48 textures
    public static class Png16Encoder
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        //values: channels (3 = RGB, 4 = RGBA) per pixel, rows bottom-up like Texture2D
        public static byte[] Encode(int width, int height, ushort[] values, int channels)
        {
            if (channels != 3 && channels != 4) throw new ArgumentException("Channels must be 3 (RGB) or 4 (RGBA).");
            if (values.Length != width * height * channels) throw new ArgumentException("Expected " + width * height * channels + " values, got " + values.Length);

            int bytesPerPixel = channels * 2;
            int stride = width * bytesPerPixel;
            var previous = new byte[stride];
            var current = new byte[stride];
            var filtered = new byte[stride];
            var best = new byte[stride];

            var deflated = new MemoryStream();
            uint adler = 1;
            using (var deflate = new DeflateStream(deflated, CompressionLevel.Optimal, true))
            {
                for (int row = 0; row < height; row++)
                {
                    //PNG rows run top-down; channel values are big-endian
                    int start = (height - 1 - row) * width * channels;
                    for (int i = 0; i < width * channels; i++)
                    {
                        ushort value = values[start + i];
                        current[i * 2] = (byte)(value >> 8);
                        current[i * 2 + 1] = (byte)value;
                    }

                    byte filter = ChooseFilter(current, previous, bytesPerPixel, filtered, best);
                    deflate.WriteByte(filter);
                    deflate.Write(best, 0, stride);
                    adler = Adler32(adler, filter);
                    adler = Adler32(adler, best, stride);

                    var swap = previous;
                    previous = current;
                    current = swap;
                }
            }

            //zlib wrapper: header, raw deflate data, Adler-32 of the uncompressed data
            var zlib = new MemoryStream();
            zlib.WriteByte(0x78);
            zlib.WriteByte(0x9C);
            deflated.Position = 0;
            deflated.CopyTo(zlib);
            WriteBigEndian(zlib, adler);
            byte[] compressed = zlib.ToArray();

            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var header = new byte[13];
                WriteBigEndian(header, 0, (uint)width);
                WriteBigEndian(header, 4, (uint)height);
                header[8] = 16;
                header[9] = (byte)(channels == 4 ? 6 : 2);
                WriteChunk(png, "IHDR", header);
                WriteChunk(png, "IDAT", compressed);
                WriteChunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        //Tries each PNG filter on the row and keeps the one with the smallest sum of absolute signed bytes
        private static byte ChooseFilter(byte[] row, byte[] above, int bytesPerPixel, byte[] scratch, byte[] best)
        {
            byte bestFilter = 0;
            long bestScore = long.MaxValue;
            for (byte filter = 0; filter <= 4; filter++)
            {
                long score = 0;
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    int up = above[i];
                    int upLeft = i >= bytesPerPixel ? above[i - bytesPerPixel] : 0;
                    int predicted;
                    switch (filter)
                    {
                        case 1: predicted = left; break;
                        case 2: predicted = up; break;
                        case 3: predicted = (left + up) >> 1; break;
                        case 4: predicted = Paeth(left, up, upLeft); break;
                        default: predicted = 0; break;
                    }
                    byte value = (byte)(row[i] - predicted);
                    scratch[i] = value;
                    score += value < 128 ? value : 256 - value;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = filter;
                    Buffer.BlockCopy(scratch, 0, best, 0, row.Length);
                }
            }
            return bestFilter;
        }

        private static int Paeth(int left, int up, int upLeft)
        {
            int estimate = left + up - upLeft;
            int toLeft = Math.Abs(estimate - left);
            int toUp = Math.Abs(estimate - up);
            int toUpLeft = Math.Abs(estimate - upLeft);
            if (toLeft <= toUp && toLeft <= toUpLeft) return left;
            return toUp <= toUpLeft ? up : upLeft;
        }

        private static uint Adler32(uint adler, byte value)
        {
            uint a = adler & 0xFFFF, b = adler >> 16;
            a = (a + value) % 65521;
            b = (b + a) % 65521;
            return (b << 16) | a;
        }

        private static uint Adler32(uint adler, byte[] data, int count)
        {
            uint a = adler & 0xFFFF, b = adler >> 16;
            for (int i = 0; i < count; i++)
            {
                a += data[i];
                if (a >= 65521) a -= 65521;
                b += a;
                if (b >= 65521) b -= 65521;
            }
            return (b << 16) | a;
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, (uint)data.Length);
            stream.Write(length, 0, 4);

            var typeBytes = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            stream.Write(typeBytes, 0, 4);
            stream.Write(data, 0, data.Length);

            uint crc = 0xFFFFFFFF;
            crc = Crc(crc, typeBytes);
            crc = Crc(crc, data);
            WriteBigEndian(stream, crc ^ 0xFFFFFFFF);
        }

        private static uint Crc(uint crc, byte[] data)
        {
            foreach (byte value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static void WriteBigEndian(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static void WriteBigEndian(Stream stream, uint value)
        {
            var bytes = new byte[4];
            WriteBigEndian(bytes, 0, value);
            stream.Write(bytes, 0, 4);
        }
    }
}
