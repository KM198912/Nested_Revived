using System;
using System.IO;
using System.IO.Compression;

namespace Nested.Assembler
{
    // Minimal PNG reader for .image: colour types 2 (RGB) and 6 (RGBA), 8 bits per
    // channel, no interlacing. Pixels come back as 0xRRGGBB (alpha ignored).
    sealed class PngImage
    {
        public int Width, Height;
        public int[] Pixels;    // row-major, 0xRRGGBB

        public int this[int x, int y] => Pixels[y * Width + x];

        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static PngImage Load(string path)
        {
            byte[] f = File.ReadAllBytes(path);
            for (int i = 0; i < Signature.Length; i++)
                if (f.Length <= i || f[i] != Signature[i])
                    throw new AsmException($"{Path.GetFileName(path)} isn't a PNG file");

            int width = 0, height = 0, depth = 0, colorType = -1, interlace = 0;
            var idat = new MemoryStream();
            int pos = 8;
            while (pos + 8 <= f.Length)
            {
                int length = BigEndian(f, pos);
                string type = System.Text.Encoding.ASCII.GetString(f, pos + 4, 4);
                int data = pos + 8;
                if (length < 0 || data + length > f.Length)
                    throw new AsmException($"{Path.GetFileName(path)}: chunk {type} is cut off");
                if (type == "IHDR")
                {
                    width = BigEndian(f, data);
                    height = BigEndian(f, data + 4);
                    depth = f[data + 8];
                    colorType = f[data + 9];
                    interlace = f[data + 12];
                }
                else if (type == "IDAT")
                    idat.Write(f, data, length);
                else if (type == "IEND")
                    break;
                pos = data + length + 4;    // skip CRC
            }

            if (width <= 0 || height <= 0)
                throw new AsmException($"{Path.GetFileName(path)}: no image header");
            if (depth != 8 || (colorType != 2 && colorType != 6) || interlace != 0)
                throw new AsmException($"{Path.GetFileName(path)}: only 8-bit RGB/RGBA without interlacing is supported " +
                    $"(this one: depth {depth}, colour type {colorType}, interlace {interlace})");

            int channels = colorType == 6 ? 4 : 3;
            int stride = width * channels;
            byte[] raw;
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                z.CopyTo(o);
                raw = o.ToArray();
            }
            if (raw.Length < (stride + 1) * height)
                throw new AsmException($"{Path.GetFileName(path)}: image data is too short");

            var img = new PngImage { Width = width, Height = height, Pixels = new int[width * height] };
            var prev = new byte[stride];
            var line = new byte[stride];
            int at = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = raw[at++];
                Array.Copy(raw, at, line, 0, stride);
                at += stride;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= channels ? line[x - channels] : 0;
                    int b = prev[x];
                    int c = x >= channels ? prev[x - channels] : 0;
                    int add;
                    switch (filter)
                    {
                        case 0: add = 0; break;
                        case 1: add = a; break;
                        case 2: add = b; break;
                        case 3: add = (a + b) >> 1; break;
                        case 4:
                            {
                                int p = a + b - c;
                                int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                                add = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                                break;
                            }
                        default: throw new AsmException($"{Path.GetFileName(path)}: bad filter {filter} in row {y}");
                    }
                    line[x] = (byte)(line[x] + add);
                }
                for (int x = 0; x < width; x++)
                {
                    int o = x * channels;
                    img.Pixels[y * width + x] = line[o] << 16 | line[o + 1] << 8 | line[o + 2];
                }
                var t = prev; prev = line; line = t;
            }
            return img;
        }

        static int BigEndian(byte[] d, int i) => d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];
    }
}
