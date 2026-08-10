using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace OMF_Editor
{
    // Just enough of DDS to show X-Ray textures in the viewport: the formats the
    // game actually ships, top mip only, decoded straight into a bitmap.
    static class DdsImage
    {
        const uint MagicDds = 0x20534444;	// "DDS "

        const uint DdpfFourCc = 0x4;
        const uint DdpfRgb = 0x40;

        const uint FourCcDxt1 = 0x31545844;
        const uint FourCcDxt3 = 0x33545844;
        const uint FourCcDxt5 = 0x35545844;

        // Reads the top mip level and returns it as a 32 bit bitmap.
        public static Bitmap Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 128 || BitConverter.ToUInt32(data, 0) != MagicDds)
                throw new InvalidDataException("not a DDS file");

            int height = BitConverter.ToInt32(data, 12);
            int width = BitConverter.ToInt32(data, 16);
            uint pfFlags = BitConverter.ToUInt32(data, 80);
            uint fourCc = BitConverter.ToUInt32(data, 84);
            int rgbBits = BitConverter.ToInt32(data, 88);
            uint rMask = BitConverter.ToUInt32(data, 92);
            uint gMask = BitConverter.ToUInt32(data, 96);
            uint bMask = BitConverter.ToUInt32(data, 100);
            uint aMask = BitConverter.ToUInt32(data, 104);

            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                throw new InvalidDataException("bad DDS size");

            int offset = 128;
            if ((pfFlags & DdpfFourCc) != 0 && fourCc == 0x30315844)	// "DX10"
                throw new NotSupportedException("DX10 textures are not supported");

            // BGRA, the layout a 32bpp bitmap expects
            byte[] pixels = new byte[width*height*4];

            if ((pfFlags & DdpfFourCc) != 0)
            {
                switch (fourCc)
                {
                    case FourCcDxt1: DecodeDxt1(data, offset, width, height, pixels); break;
                    case FourCcDxt3: DecodeDxt3(data, offset, width, height, pixels); break;
                    case FourCcDxt5: DecodeDxt5(data, offset, width, height, pixels); break;
                    default:
                        throw new NotSupportedException("unsupported DDS compression");
                }
            }
            else if ((pfFlags & DdpfRgb) != 0 && (rgbBits == 32 || rgbBits == 24))
            {
                DecodeRgb(data, offset, width, height, rgbBits/8, rMask, gMask, bMask, aMask, pixels);
            }
            else
            {
                throw new NotSupportedException("unsupported DDS pixel format");
            }

            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y != height; ++y)
                    Marshal.Copy(pixels, y*width*4, locked.Scan0 + y*locked.Stride, width*4);
            }
            finally
            {
                bitmap.UnlockBits(locked);
            }
            return bitmap;
        }

        // ---- uncompressed ----------------------------------------------------

        private static int MaskShift(uint mask)
        {
            if (mask == 0)
                return 0;
            int shift = 0;
            while ((mask & 1) == 0)
            {
                mask >>= 1;
                ++shift;
            }
            return shift;
        }

        private static byte Extract(uint value, uint mask, int shift, byte fallback)
        {
            if (mask == 0)
                return fallback;
            uint channel = (value & mask) >> shift;
            uint range = mask >> shift;
            return range == 0 ? fallback : (byte)(channel*255/range);
        }

        private static void DecodeRgb(byte[] data, int offset, int width, int height, int bytes,
            uint rMask, uint gMask, uint bMask, uint aMask, byte[] pixels)
        {
            int rShift = MaskShift(rMask), gShift = MaskShift(gMask);
            int bShift = MaskShift(bMask), aShift = MaskShift(aMask);

            for (int i = 0; i != width*height; ++i)
            {
                int at = offset + i*bytes;
                if (at + bytes > data.Length)
                    break;

                uint value = 0;
                for (int k = 0; k != bytes; ++k)
                    value |= (uint)data[at + k] << (k*8);

                pixels[i*4 + 0] = Extract(value, bMask, bShift, 0);
                pixels[i*4 + 1] = Extract(value, gMask, gShift, 0);
                pixels[i*4 + 2] = Extract(value, rMask, rShift, 0);
                pixels[i*4 + 3] = Extract(value, aMask, aShift, 255);
            }
        }

        // ---- block compression -----------------------------------------------

        // Writes one 4x4 block of colours, clipped to the image.
        private static void ColorBlock(byte[] data, int at, int width, int height,
            int bx, int by, byte[] pixels, bool punchThrough)
        {
            ushort c0 = (ushort)(data[at] | (data[at + 1] << 8));
            ushort c1 = (ushort)(data[at + 2] | (data[at + 3] << 8));

            byte[] r = new byte[4], g = new byte[4], b = new byte[4], a = new byte[4];
            Unpack565(c0, out r[0], out g[0], out b[0]);
            Unpack565(c1, out r[1], out g[1], out b[1]);
            a[0] = a[1] = a[2] = a[3] = 255;

            if (c0 > c1 || !punchThrough)
            {
                r[2] = (byte)((2*r[0] + r[1])/3);
                g[2] = (byte)((2*g[0] + g[1])/3);
                b[2] = (byte)((2*b[0] + b[1])/3);
                r[3] = (byte)((r[0] + 2*r[1])/3);
                g[3] = (byte)((g[0] + 2*g[1])/3);
                b[3] = (byte)((b[0] + 2*b[1])/3);
            }
            else
            {
                r[2] = (byte)((r[0] + r[1])/2);
                g[2] = (byte)((g[0] + g[1])/2);
                b[2] = (byte)((b[0] + b[1])/2);
                r[3] = g[3] = b[3] = 0;
                a[3] = 0;	// the transparent slot of DXT1
            }

            uint bits = BitConverter.ToUInt32(data, at + 4);
            for (int y = 0; y != 4; ++y)
            {
                for (int x = 0; x != 4; ++x)
                {
                    int px = bx + x, py = by + y;
                    if (px >= width || py >= height)
                        continue;
                    int index = (int)((bits >> ((y*4 + x)*2)) & 3);
                    int at2 = (py*width + px)*4;
                    pixels[at2 + 0] = b[index];
                    pixels[at2 + 1] = g[index];
                    pixels[at2 + 2] = r[index];
                    pixels[at2 + 3] = a[index];
                }
            }
        }

        private static void Unpack565(ushort value, out byte r, out byte g, out byte b)
        {
            r = (byte)(((value >> 11) & 0x1f)*255/31);
            g = (byte)(((value >> 5) & 0x3f)*255/63);
            b = (byte)((value & 0x1f)*255/31);
        }

        private static void DecodeDxt1(byte[] data, int offset, int width, int height, byte[] pixels)
        {
            int at = offset;
            for (int by = 0; by < height; by += 4)
            {
                for (int bx = 0; bx < width; bx += 4, at += 8)
                {
                    if (at + 8 > data.Length)
                        return;
                    ColorBlock(data, at, width, height, bx, by, pixels, true);
                }
            }
        }

        private static void DecodeDxt3(byte[] data, int offset, int width, int height, byte[] pixels)
        {
            int at = offset;
            for (int by = 0; by < height; by += 4)
            {
                for (int bx = 0; bx < width; bx += 4, at += 16)
                {
                    if (at + 16 > data.Length)
                        return;
                    ColorBlock(data, at + 8, width, height, bx, by, pixels, false);

                    for (int y = 0; y != 4; ++y)
                    {
                        for (int x = 0; x != 4; ++x)
                        {
                            int px = bx + x, py = by + y;
                            if (px >= width || py >= height)
                                continue;
                            int nibble = data[at + y*2 + x/2];
                            int alpha = (x & 1) == 0 ? nibble & 0xf : nibble >> 4;
                            pixels[(py*width + px)*4 + 3] = (byte)(alpha*255/15);
                        }
                    }
                }
            }
        }

        private static void DecodeDxt5(byte[] data, int offset, int width, int height, byte[] pixels)
        {
            byte[] alpha = new byte[8];
            int at = offset;
            for (int by = 0; by < height; by += 4)
            {
                for (int bx = 0; bx < width; bx += 4, at += 16)
                {
                    if (at + 16 > data.Length)
                        return;
                    ColorBlock(data, at + 8, width, height, bx, by, pixels, false);

                    alpha[0] = data[at];
                    alpha[1] = data[at + 1];
                    if (alpha[0] > alpha[1])
                    {
                        for (int i = 1; i != 7; ++i)
                            alpha[i + 1] = (byte)(((7 - i)*alpha[0] + i*alpha[1])/7);
                    }
                    else
                    {
                        for (int i = 1; i != 5; ++i)
                            alpha[i + 1] = (byte)(((5 - i)*alpha[0] + i*alpha[1])/5);
                        alpha[6] = 0;
                        alpha[7] = 255;
                    }

                    ulong bits = 0;
                    for (int i = 0; i != 6; ++i)
                        bits |= (ulong)data[at + 2 + i] << (i*8);

                    for (int y = 0; y != 4; ++y)
                    {
                        for (int x = 0; x != 4; ++x)
                        {
                            int px = bx + x, py = by + y;
                            if (px >= width || py >= height)
                                continue;
                            int index = (int)((bits >> ((y*4 + x)*3)) & 7);
                            pixels[(py*width + px)*4 + 3] = alpha[index];
                        }
                    }
                }
            }
        }
    }
}
