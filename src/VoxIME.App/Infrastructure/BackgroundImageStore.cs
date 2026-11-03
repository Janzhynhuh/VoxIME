using System.IO;
using VoxIME.Core;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 自定义背景图片的本地存储：用户选择图片后复制到 <c>%APPDATA%\VoxIME\backgrounds</c>，
/// 避免源文件被移动/删除后背景失效；只保留最近若干张，避免目录无限增长。
/// 图片只保存在本机，不进入仓库、不上传。
/// </summary>
public static class BackgroundImageStore
{
    private const int MaxKeepFiles = 8;
    private const long MaxSourceBytes = 32L * 1024 * 1024;

    private static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>背景图片目录。</summary>
    public static string RootDirectory => Path.Combine(AppInfo.AppDataDirectory, "backgrounds");

    /// <summary>把用户选择的图片复制进背景目录并返回新路径；失败/不支持时返回 null。</summary>
    public static string? Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension, StringComparer.Ordinal))
        {
            throw new NotSupportedException("只支持 PNG / JPG / BMP / GIF 图片。");
        }

        var info = new FileInfo(sourcePath);
        if (info.Length <= 0 || info.Length > MaxSourceBytes)
        {
            throw new InvalidOperationException("图片文件为空或超过 32MB。");
        }

        Directory.CreateDirectory(RootDirectory);
        var target = Path.Combine(
            RootDirectory,
            $"bg_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..32] + extension);
        File.Copy(sourcePath, target);
        TrimOldFiles();
        return target;
    }

    /// <summary>删除临时背景文件（只处理背景目录内的文件）。</summary>
    public static void Delete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(RootDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BackgroundImageStore] 删除背景图片失败：{ex.Message}");
        }
    }

    private static void TrimOldFiles()
    {
        try
        {
            var files = new DirectoryInfo(RootDirectory)
                .GetFiles("bg_*")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaxKeepFiles)
                .ToList();
            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BackgroundImageStore] 清理旧背景图片失败：{ex.Message}");
        }
    }
}
