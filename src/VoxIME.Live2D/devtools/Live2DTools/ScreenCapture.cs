using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace VoxIME.Live2D.DevTools;

/// <summary>屏幕捕获/像素对比/拼图工具（开发期验证专用）。</summary>
internal static class ScreenCapture
{
    /// <summary>从屏幕物理像素坐标捕获一块区域。</summary>
    public static Bitmap CaptureRegion(int x, int y, int width, int height)
    {
        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);
        var bitmap = new Bitmap(safeWidth, safeHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(x, y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    public static void SavePng(Bitmap bitmap, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bitmap.Save(fullPath, ImageFormat.Png);
    }

    public static void SavePng(System.Windows.Media.Imaging.BitmapSource source, string path, int dpi = 96)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = File.Create(fullPath);
        encoder.Save(stream);
    }

    public static PixelBuffer GetPixels(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new PixelBuffer(bytes, data.Stride, bitmap.Width, bitmap.Height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>对比两张同尺寸截图：变化像素比例 + 四角差异（用于透明性证明）。</summary>
    public static DiffResult Compare(Bitmap withWindow, Bitmap withoutWindow, int colorThreshold = 24)
    {
        using var a = withWindow;
        using var b = withoutWindow;
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException("两张截图尺寸不一致，无法比较。");
        }

        var pa = GetPixels(a);
        var pb = GetPixels(b);
        long changed = 0;
        long total = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < pa.Height; y++)
        {
            var row = y * pa.Stride;
            for (var x = 0; x < pa.Width; x++)
            {
                var i = row + x * 4;
                var diff = Math.Abs(pa.Data[i] - pb.Data[i])
                           + Math.Abs(pa.Data[i + 1] - pb.Data[i + 1])
                           + Math.Abs(pa.Data[i + 2] - pb.Data[i + 2]);
                if (diff > colorThreshold)
                {
                    changed++;
                    if (x < minX) { minX = x; }
                    if (y < minY) { minY = y; }
                    if (x > maxX) { maxX = x; }
                    if (y > maxY) { maxY = y; }
                }

                total++;
            }
        }

        // 四角 8x8 区域最大差异：窗口透明时四角应与“窗口隐藏后”的桌面像素一致。
        var corners = new (int X, int Y)[]
        {
            (2, 2),
            (pa.Width - 10, 2),
            (2, pa.Height - 10),
            (pa.Width - 10, pa.Height - 10)
        };
        var maxCornerDiff = 0;
        foreach (var (cx, cy) in corners)
        {
            for (var dy = 0; dy < 8; dy++)
            {
                for (var dx = 0; dx < 8; dx++)
                {
                    var x = Math.Clamp(cx + dx, 0, pa.Width - 1);
                    var y = Math.Clamp(cy + dy, 0, pa.Height - 1);
                    var i = y * pa.Stride + x * 4;
                    var diff = Math.Abs(pa.Data[i] - pb.Data[i])
                               + Math.Abs(pa.Data[i + 1] - pb.Data[i + 1])
                               + Math.Abs(pa.Data[i + 2] - pb.Data[i + 2]);
                    if (diff > maxCornerDiff)
                    {
                        maxCornerDiff = diff;
                    }
                }
            }
        }

        return new DiffResult(
            total == 0 ? 0 : (double)changed / total,
            changed,
            total,
            maxCornerDiff <= 12,
            maxCornerDiff,
            maxX < 0 ? null : new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1));
    }

    /// <summary>把多张透明 PNG 拼成带标题/标签的棋盘格背景长图（用于兜底形象状态展示）。</summary>
    public static void ComposeMontage(
        IReadOnlyList<string> imagePaths,
        IReadOnlyList<string> labels,
        string outputPath,
        int cellWidth = 360,
        int cellHeight = 540,
        string title = "VoxIME 内置兜底形象（WPF 矢量绘制）")
    {
        var headerHeight = 54;
        var width = cellWidth * imagePaths.Count;
        var height = cellHeight + headerHeight + 20;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // 棋盘格背景（表现透明区域）
        const int tile = 16;
        for (var y = 0; y < height; y += tile)
        {
            for (var x = 0; x < width; x += tile)
            {
                var dark = ((x / tile) + (y / tile)) % 2 == 0;
                using var brush = new SolidBrush(dark ? Color.FromArgb(255, 38, 38, 48) : Color.FromArgb(255, 46, 46, 58));
                graphics.FillRectangle(brush, x, y, tile, tile);
            }
        }

        using (var titleFont = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var titleBrush = new SolidBrush(Color.FromArgb(255, 232, 232, 240)))
        {
            graphics.DrawString(title, titleFont, titleBrush, new PointF(18, 14));
        }

        using var labelFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(Color.FromArgb(255, 179, 157, 255));
        for (var i = 0; i < imagePaths.Count; i++)
        {
            using var image = Image.FromFile(imagePaths[i]);
            var destRect = new Rectangle(i * cellWidth, headerHeight, cellWidth - 12, cellHeight);
            graphics.DrawImage(image, destRect);

            var label = i < labels.Count ? labels[i] : "";
            var labelSize = graphics.MeasureString(label, labelFont);
            graphics.FillRectangle(
                new SolidBrush(Color.FromArgb(140, 18, 18, 26)),
                i * cellWidth + (cellWidth - labelSize.Width) / 2 - 10,
                height - 34,
                labelSize.Width + 20,
                24);
            graphics.DrawString(label, labelFont, labelBrush,
                i * cellWidth + (cellWidth - labelSize.Width) / 2, height - 32);
        }

        SavePng(bitmap, outputPath);
    }

    internal readonly record struct PixelBuffer(byte[] Data, int Stride, int Width, int Height);

    internal readonly record struct PixelRect(int X, int Y, int Width, int Height);

    internal readonly record struct DiffResult(
        double ChangedRatio,
        long ChangedPixels,
        long TotalPixels,
        bool CornersEqual,
        int CornerMaxDiff,
        PixelRect? ChangedBounds);
}
