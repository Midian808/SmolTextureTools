using System;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;

namespace Midian.TextureTools.Tests
{
    //Decodes the encoder's output with a separate minimal PNG reader, checking every checksum and value
    public class Png16EncoderTests
    {
        private static uint BigEndian(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static uint Crc32(byte[] bytes, int offset, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= bytes[i];
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }

        //Returns the values (rows bottom-up, like the encoder's input), the bit depth and the colour type
        private static ushort[] Decode(byte[] png, out int width, out int height, out int bitDepth, out int colourType)
        {
            CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, new ArraySegment<byte>(png, 0, 8));
            width = height = bitDepth = colourType = 0;
            var idat = new MemoryStream();
            int position = 8;
            bool ended = false;
            while (!ended)
            {
                int length = (int)BigEndian(png, position);
                string type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
                Assert.AreEqual(BigEndian(png, position + 8 + length), Crc32(png, position + 4, length + 4), "CRC of " + type);
                int data = position + 8;
                switch (type)
                {
                    case "IHDR":
                        width = (int)BigEndian(png, data);
                        height = (int)BigEndian(png, data + 4);
                        bitDepth = png[data + 8];
                        colourType = png[data + 9];
                        break;
                    case "IDAT":
                        idat.Write(png, data, length);
                        break;
                    case "IEND":
                        ended = true;
                        break;
                }
                position += 12 + length;
            }
            Assert.AreEqual(png.Length, position, "nothing after IEND");

            //zlib: 2-byte header, deflate data, Adler-32 trailer
            byte[] zlib = idat.ToArray();
            Assert.AreEqual(0, ((zlib[0] << 8) | zlib[1]) % 31, "zlib header check");
            var inflated = new MemoryStream();
            using (var inflate = new DeflateStream(new MemoryStream(zlib, 2, zlib.Length - 6), CompressionMode.Decompress))
            {
                inflate.CopyTo(inflated);
            }
            byte[] raw = inflated.ToArray();
            uint a = 1, b = 0;
            foreach (byte value in raw)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }
            Assert.AreEqual((b << 16) | a, BigEndian(zlib, zlib.Length - 4), "Adler-32");

            int channels = colourType == 6 ? 4 : 3;
            int bytesPerPixel = channels * 2;
            int stride = width * bytesPerPixel;
            Assert.AreEqual(height * (stride + 1), raw.Length);

            var values = new ushort[width * height * channels];
            var previous = new byte[stride];
            var row = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int filter = raw[y * (stride + 1)];
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    int up = previous[i];
                    int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                    int predicted;
                    switch (filter)
                    {
                        case 0: predicted = 0; break;
                        case 1: predicted = left; break;
                        case 2: predicted = up; break;
                        case 3: predicted = (left + up) / 2; break;
                        case 4:
                        {
                            int p = left + up - upLeft;
                            int pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - upLeft);
                            predicted = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
                            break;
                        }
                        default: throw new AssertionException("unknown filter " + filter);
                    }
                    row[i] = (byte)(raw[y * (stride + 1) + 1 + i] + predicted);
                }
                int start = (height - 1 - y) * width * channels;
                for (int i = 0; i < width * channels; i++) values[start + i] = (ushort)((row[i * 2] << 8) | row[i * 2 + 1]);
                var swap = previous;
                previous = row;
                row = swap;
            }
            return values;
        }

        [TestCase(4)]
        [TestCase(3)]
        public void Encode_RoundTripsEveryValue(int channels)
        {
            //Gradients exercise the predicting filters; noise exercises the rest
            const int width = 23, height = 17;
            var random = new Random(40 + channels);
            var values = new ushort[width * height * channels];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    for (int c = 0; c < channels; c++)
                    {
                        int i = (y * width + x) * channels + c;
                        values[i] = y < height / 2 ? (ushort)(x * 2000 + y * 300 + c) : (ushort)random.Next(65536);
                    }

            byte[] png = Png16Encoder.Encode(width, height, values, channels);

            var decoded = Decode(png, out int w, out int h, out int depth, out int colourType);
            Assert.AreEqual(width, w);
            Assert.AreEqual(height, h);
            Assert.AreEqual(16, depth);
            Assert.AreEqual(channels == 4 ? 6 : 2, colourType);
            CollectionAssert.AreEqual(values, decoded);
        }
    }
}
