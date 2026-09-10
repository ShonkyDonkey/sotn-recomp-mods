using System;

public static class LanczosResizer
{
    public static byte[] ResizeRgbaLanczos3(
        byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        const int channels = 4;
        if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentOutOfRangeException();
        if (sourceWidth == targetWidth && sourceHeight == targetHeight) return (byte[])source.Clone();

        byte[] horizontal = new byte[targetWidth * sourceHeight * channels];
        float scaleX = targetWidth / (float)sourceWidth;
        float filterScaleX = MathF.Min(1f, scaleX);
        float supportX = 3f / filterScaleX;

        for (int y = 0; y < sourceHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                float center = (x + 0.5f) / scaleX - 0.5f;
                int start = Math.Max(0, (int)MathF.Ceiling(center - supportX));
                int end = Math.Min(sourceWidth - 1, (int)MathF.Floor(center + supportX));

                float r = 0f, g = 0f, b = 0f, a = 0f, weightSum = 0f;

                for (int sx = start; sx <= end; sx++)
                {
                    float distance = (sx - center) * filterScaleX;
                    float weight = Lanczos3(distance);
                    if (weight == 0f) continue;

                    int sourceIndex = (y * sourceWidth + sx) * channels;
                    r += source[sourceIndex] * weight;
                    g += source[sourceIndex + 1] * weight;
                    b += source[sourceIndex + 2] * weight;
                    a += source[sourceIndex + 3] * weight;
                    weightSum += weight;
                }

                int dstIndex = (y * targetWidth + x) * channels;
                if (weightSum != 0f)
                {
                    horizontal[dstIndex] = ClampByte(r / weightSum);
                    horizontal[dstIndex + 1] = ClampByte(g / weightSum);
                    horizontal[dstIndex + 2] = ClampByte(b / weightSum);
                    horizontal[dstIndex + 3] = ClampByte(a / weightSum);
                }
            }
        }

        byte[] result = new byte[targetWidth * targetHeight * channels];
        float scaleY = targetHeight / (float)sourceHeight;
        float filterScaleY = MathF.Min(1f, scaleY);
        float supportY = 3f / filterScaleY;

        for (int y = 0; y < targetHeight; y++)
        {
            float center = (y + 0.5f) / scaleY - 0.5f;
            int start = Math.Max(0, (int)MathF.Ceiling(center - supportY));
            int end = Math.Min(sourceHeight - 1, (int)MathF.Floor(center + supportY));

            for (int x = 0; x < targetWidth; x++)
            {
                float r = 0f, g = 0f, b = 0f, a = 0f, weightSum = 0f;

                for (int sy = start; sy <= end; sy++)
                {
                    float distance = (sy - center) * filterScaleY;
                    float weight = Lanczos3(distance);
                    if (weight == 0f) continue;

                    int sourceIndex = (sy * targetWidth + x) * channels;
                    r += horizontal[sourceIndex] * weight;
                    g += horizontal[sourceIndex + 1] * weight;
                    b += horizontal[sourceIndex + 2] * weight;
                    a += horizontal[sourceIndex + 3] * weight;
                    weightSum += weight;
                }

                int dstIndex = (y * targetWidth + x) * channels;
                if (weightSum != 0f)
                {
                    result[dstIndex] = ClampByte(r / weightSum);
                    result[dstIndex + 1] = ClampByte(g / weightSum);
                    result[dstIndex + 2] = ClampByte(b / weightSum);
                    result[dstIndex + 3] = ClampByte(a / weightSum);
                }
            }
        }

        return result;
    }

    private static float Lanczos3(float x)
    {
        x = MathF.Abs(x);
        if (x >= 3f) return 0f;
        if (x < 0.00001f) return 1f;

        float piX = MathF.PI * x;
        return (MathF.Sin(piX) / piX) * (MathF.Sin(piX / 3f) / (piX / 3f));
    }

    private static byte ClampByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)MathF.Round(value);
    }
}