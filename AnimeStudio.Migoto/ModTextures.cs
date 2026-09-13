using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using Texture2DDecoder;

namespace AnimeStudio.Migoto
{
    /// <summary>
    /// Turns a mod's own image files into the PNGs the FBX writer hands out.
    ///
    /// It has to be a real decode, not a copy: the exporter writes every texture under
    /// <c>&lt;name&gt;.png</c> and the FBX refers to it by that name, so DDS bytes under a
    /// .png name would leave the model pointing at a file nothing can open.
    ///
    /// The formats are the ones ZZMI mods actually ship, measured across two mod folders:
    /// BC7 for nearly everything, BC6H for the light and material maps, one uncompressed
    /// 32-bit normal map, plus the odd .jpg or .png next to the .dds.
    /// </summary>
    public static class ModTextures
    {
        /// <summary>
        /// Reads one image file. Returns null with a reason in <paramref name="warnings"/>
        /// rather than an unreadable texture.
        /// </summary>
        public static ImportedTexture Load(string path, List<string> warnings)
        {
            var name = Path.GetFileNameWithoutExtension(path) + ".png";
            try
            {
                using var decoded = path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                    ? ReadDds(path, warnings)
                    : SKBitmap.Decode(path);
                if (decoded == null)
                {
                    warnings.Add($"{Path.GetFileName(path)} could not be read, "
                                 + "the material keeps the original texture");
                    return null;
                }
                using var opaque = Opaque(decoded);
                return new ImportedTexture(
                    (opaque ?? decoded).ConvertToStream(ImageFormat.Png), name);
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(path)} could not be read, {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The first mip of a DDS. Only that one is wanted -- the FBX carries a single image
        /// per slot -- and it is the one that starts right after the header.
        /// </summary>
        private static SKBitmap ReadDds(string path, List<string> warnings)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 128 || BitConverter.ToUInt32(data, 0) != 0x20534444)
            {
                warnings.Add($"{Path.GetFileName(path)} is not a DDS file");
                return null;
            }

            var height = BitConverter.ToInt32(data, 12);
            var width = BitConverter.ToInt32(data, 16);
            var fourCC = BitConverter.ToUInt32(data, 84);
            var bits = BitConverter.ToInt32(data, 88);
            var offset = 128;

            var format = FromFourCC(fourCC);
            if (fourCC == 0x30315844)                       // "DX10": the format is in the
            {                                               // extra header behind the first
                if (data.Length < 148)
                {
                    warnings.Add($"{Path.GetFileName(path)} claims a DX10 header but is too short");
                    return null;
                }
                format = FromDxgi(BitConverter.ToUInt32(data, 128));
                offset = 148;
            }
            else if (fourCC == 0 && bits == 32)
            {
                // Uncompressed. The masks say which byte is which; the only layout seen in
                // the wild is straight BGRA, and anything else is safer refused than guessed.
                var red = BitConverter.ToUInt32(data, 92);
                format = red == 0x00ff0000 ? "BGRA8" : red == 0x000000ff ? "RGBA8" : null;
            }

            if (format == null)
            {
                warnings.Add($"{Path.GetFileName(path)}: unsupported DDS format, "
                             + "the material keeps the original texture");
                return null;
            }
            if (width <= 0 || height <= 0)
            {
                warnings.Add($"{Path.GetFileName(path)}: implausible size {width}x{height}");
                return null;
            }

            var body = new byte[data.Length - offset];
            Buffer.BlockCopy(data, offset, body, 0, body.Length);
            var pixels = new byte[width * height * 4];

            var ok = format switch
            {
                "BC1" => TextureDecoder.DecodeDXT1(body, width, height, pixels),
                "BC3" => TextureDecoder.DecodeDXT5(body, width, height, pixels),
                "BC4" => TextureDecoder.DecodeBC4(body, width, height, pixels),
                "BC5" => TextureDecoder.DecodeBC5(body, width, height, pixels),
                "BC6" => TextureDecoder.DecodeBC6(body, width, height, pixels),
                "BC7" => TextureDecoder.DecodeBC7(body, width, height, pixels),
                "BGRA8" => Straight(body, pixels, false),
                "RGBA8" => Straight(body, pixels, true),
                _ => false,
            };
            if (!ok)
            {
                warnings.Add($"{Path.GetFileName(path)}: the {format} data could not be decoded");
                return null;
            }
            MakeOpaque(pixels);
            return ImageExtensions.CreateBitmapFromBgra(pixels, width, height);
        }

        /// <summary>
        /// Throws the alpha channel away.
        ///
        /// It is not opacity. ZZZ's toon shader keeps a mask in there -- a quarter of the
        /// Remielle diffuse is alpha 0 while the model is solid in game -- and an importer
        /// that wires it to transparency deletes whole limbs. Blender does exactly that, and
        /// the arms of the test mod vanished until this was in.
        /// </summary>
        private static void MakeOpaque(byte[] bgra)
        {
            for (int i = 3; i < bgra.Length; i += 4)
                bgra[i] = 255;
        }

        /// <summary>
        /// The same for an image Skia decoded. It has to go through unpremultiplied pixels:
        /// raising the alpha of a premultiplied one leaves the colour already multiplied
        /// down, which turns every masked area black instead of visible.
        /// </summary>
        private static SKBitmap Opaque(SKBitmap source)
        {
            var info = new SKImageInfo(source.Width, source.Height,
                                       SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var pixels = new byte[info.BytesSize];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var image = SKImage.FromBitmap(source);
                if (image == null
                    || !image.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
                    return null;
            }
            finally
            {
                handle.Free();
            }
            MakeOpaque(pixels);
            return ImageExtensions.CreateBitmapFromBgra(pixels, info.Width, info.Height);
        }

        /// <summary>Uncompressed pixels, swapped into the BGRA order the bitmap wants.</summary>
        private static bool Straight(byte[] source, byte[] pixels, bool swapRedAndBlue)
        {
            if (source.Length < pixels.Length)
                return false;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = source[i + (swapRedAndBlue ? 2 : 0)];
                pixels[i + 1] = source[i + 1];
                pixels[i + 2] = source[i + (swapRedAndBlue ? 0 : 2)];
                pixels[i + 3] = source[i + 3];
            }
            return true;
        }

        private static string FromFourCC(uint fourCC) => fourCC switch
        {
            0x31545844 => "BC1",        // DXT1
            0x35545844 => "BC3",        // DXT5
            0x31495441 => "BC4",        // ATI1
            0x55344342 => "BC4",        // BC4U
            0x32495441 => "BC5",        // ATI2
            0x55354342 => "BC5",        // BC5U
            _ => null,
        };

        /// <summary>
        /// The DXGI format numbers, each block format spanning typeless / unorm / sRGB.
        /// The decoders do not care which of the three it is; the bits are the same.
        /// </summary>
        private static string FromDxgi(uint dxgi) => dxgi switch
        {
            70 or 71 or 72 => "BC1",
            79 or 80 or 81 => "BC4",
            82 or 83 or 84 => "BC5",
            94 or 95 or 96 => "BC6",
            97 or 98 or 99 => "BC7",
            76 or 77 or 78 => "BC3",
            87 or 88 or 89 => "BGRA8",
            27 or 28 or 29 => "RGBA8",
            _ => null,
        };
    }
}
