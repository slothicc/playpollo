using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace PlayPollo.Mapping
{
    public class CoverImage
    {
        public byte[] Png { get; set; }

        public string Hash { get; set; }
    }

    public static class CoverExporter
    {
        /// <summary>Converts any image WPF can decode (jpg, webp via codec, bmp, ...) to PNG. Returns null when there's no usable cover.</summary>
        public static CoverImage ToPng(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                return null;
            }

            try
            {
                var decoder = BitmapDecoder.Create(new Uri(imagePath), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(decoder.Frames[0]);

                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    var png = stream.ToArray();
                    return new CoverImage { Png = png, Hash = Sha256(png) };
                }
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is IOException || ex is FileFormatException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", string.Empty);
            }
        }
    }
}
