using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;

namespace PRND_InfraredCapture.Models
{
    public static class MathEx
    {
        public static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
    public static class ThermalImageUtil
    {
        public static Bitmap BuildGrayscaleBitmapFromFloatRaw(string filePath, int width, int height)
        {
            float[] floatData = new float[width * height];

            byte[] bytes = File.ReadAllBytes(filePath);
            //float[] floatData = new float[Width * Height];

            // 1. Float 데이터 파싱
            //Buffer.BlockCopy(bytes, 0, floatData, 0, floatData.Length * 4);  //Little Endian
            ParseBigEndianFloatsSafe(bytes, floatData);

            // 2. Clamp
            for (int i = 0; i < floatData.Length; i++)
            {
                floatData[i] = MathEx.Clamp(floatData[i], 0f, 100f);
            }

            // 3. Normalize
            byte[] byteData = floatData.Select(f => (byte)(f / 100 * 255)).ToArray();



            int stride = ((width + 3) / 4) * 4; // 4배수 올림
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);

            // 그레이 팔레트(0=검정 ~ 255=흰색)
            ColorPalette pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;

            // LockBits로 직접 복사
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, width, height),
                                         ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try
            {
                unsafe
                {
                    byte* dst = (byte*)bd.Scan0;
                    fixed (byte* src = byteData)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            // 소스는 stride 패딩 없음(=width) 이고, 타겟은 stride 만큼 이동
                            Buffer.MemoryCopy(src + y * width, dst + y * bd.Stride,
                                              bd.Stride, width);
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            return bmp;
        }
        /// <summary>
        /// baselinePath의 첫 프레임을 기준으로 currentPath - baseline 을 계산한 뒤,
        /// [normMin, normMax] 범위로 정규화하여 8bpp 그레이스케일 Bitmap 생성
        /// </summary>
        public static Bitmap BuildDiffGray8FromFloatRaw(
            string currentPath,
            string baselinePath,
            int width, int height,
            float normMin,       // 예: -5f (Δ°C)
            float normMax,       // 예:  +5f (Δ°C)
            ref float maxDiff
            )
        {
            maxDiff = 0;
            int count = width * height;

            // 1) 파일 읽기
            byte[] curBytes = File.ReadAllBytes(currentPath);
            byte[] baseBytes = File.ReadAllBytes(baselinePath);

            // 2) float 파싱 (파일이 빅엔디언이라고 가정: 필요 시 리틀엔디언으로 변경)
            var cur = new float[count];
            var bas = new float[count];
            ParseBigEndianFloatsSafe(curBytes, cur);   // 또는 Buffer.BlockCopy (리틀엔디언 파일일 때)
            ParseBigEndianFloatsSafe(baseBytes, bas);

            // 3) (current - baseline) 계산하면서 바로 정규화 → byte[] 로
            //    한 번의 패스로 성능 확보
            byte[] gray = new byte[count];
            float span = Math.Max(1e-6f, normMax - normMin);

            for (int i = 0; i < count; i++)
            {
                // (1) 차(Δ) 계산
                float diff = cur[i] - bas[i];
                if( Math.Abs(diff) > Math.Abs(maxDiff))
                    maxDiff = diff;
                // (2) NaN/Inf 안전 처리
                if (float.IsNaN(diff) || float.IsInfinity(diff))
                    diff = 0f;

                // (3) 정규화(클램프 포함)
                if (diff < normMin) diff = normMin;
                else if (diff > normMax) diff = normMax;

                int g = (int)((diff - normMin) * 255f / span + 0.5f);
                if (g < 0) g = 0; else if (g > 255) g = 255;

                gray[i] = (byte)g;
            }

            // 4) 8bpp 인덱스드 비트맵으로 복사
            var bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);

            // 그레이 팔레트(0~255)
            var pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;

            var rect = new Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try
            {
                int srcStride = width;
                for (int y = 0; y < height; y++)
                {
                    IntPtr dstRow = bd.Scan0 + y * bd.Stride;
                    Marshal.Copy(gray, y * srcStride, dstRow, width);
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return bmp;
        }
    

        public static void ParseBigEndianFloatsSafe(byte[] bytes, float[] dst)
        {
            int n = dst.Length;
            if (bytes.Length < n * 4) throw new ArgumentException("bytes too short");

            var swapped = new byte[n * 4];
            int s = 0;
            for (int p = 0; p < n * 4; p += 4)
            {
                swapped[s + 0] = bytes[p + 3];
                swapped[s + 1] = bytes[p + 2];
                swapped[s + 2] = bytes[p + 1];
                swapped[s + 3] = bytes[p + 0];
                s += 4;
            }

            Buffer.BlockCopy(swapped, 0, dst, 0, n * 4);
        }
    }
}
