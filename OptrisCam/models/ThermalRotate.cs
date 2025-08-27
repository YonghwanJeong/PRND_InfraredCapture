using System;
using System.Collections.Generic;
using System.Text;

namespace CP.OptrisCam.models
{
    public static class ThermalRotate
    {
        // -------- Temperature (T 픽셀 단위, 예: float/ushort 등) --------
        public static T[] RotateTemperatureCW90<T>(T[] src, int width, int height)
        {
            var dst = new T[src.Length];
            int newW = height, newH = width;
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    // (x,y) -> (x',y') = (height-1 - y, x)
                    int dstX = height - 1 - y;
                    int dstY = x;
                    dst[dstY * newW + dstX] = src[rowBase + x];
                }
            }
            return dst;
        }

        public static T[] RotateTemperature180<T>(T[] src, int width, int height)
        {
            var dst = new T[src.Length];
            int last = src.Length - 1;
            for (int i = 0; i < src.Length; i++)
                dst[last - i] = src[i];
            return dst;
        }

        public static T[] RotateTemperatureCW270<T>(T[] src, int width, int height)
        {
            var dst = new T[src.Length];
            int newW = height, newH = width;
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    // (x,y) -> (x',y') = (y, width-1 - x)
                    int dstX = y;
                    int dstY = width - 1 - x;
                    dst[dstY * newW + dstX] = src[rowBase + x];
                }
            }
            return dst;
        }

        //public static T[] RotateTemperature<T>(T[] src, int width, int height, int angle, out int newWidth, out int newHeight)
        public static T[] RotateTemperature<T>(T[] src, int width, int height, int angle)
        {
            switch (NormalizeAngle(angle))
            {
                case 90:
                    //newWidth = height; newHeight = width;
                    return RotateTemperatureCW90(src, width, height);
                case 180:
                    //newWidth = width; newHeight = height;
                    return RotateTemperature180(src, width, height);
                case 270:
                    //newWidth = height; newHeight = width;
                    return RotateTemperatureCW270(src, width, height);
                default:
                    //newWidth = width; newHeight = height;
                    return (T[])src.Clone();
            }
        }


        private static int NormalizeAngle(int angle)
        {
            angle %= 360;
            if (angle < 0) angle += 360;
            return angle;
        }
    }
}
