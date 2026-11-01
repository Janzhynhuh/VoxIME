using System.IO;
using System.IO.Compression;

namespace VoxIME.Core.Services;

/// <summary>模型库中的一个 Live2D 模型。</summary>
public sealed record Live2DModelInfo(string Name, string Directory, string ModelJsonPath)
{
    /// <summary>下拉框显示用名称。</summary>
    public string DisplayName => Name;
}

/// <summary>
/// Live2D 模型库：把用户选择的模型文件夹或 zip 复制到统一目录（默认 <c>%APPDATA%\VoxIME\models</c>），
/// 便于设置页用下拉框在多个模型之间切换。模型保持普通文件夹结构，不修改用户原目录。
/// </summary>
public sealed class Live2DModelLibrary
{
    public const string ModelFilePattern = "*.model3.json";
    public const string ModelFileSuffix = ".model3.json";

    public Live2DModelLibrary(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("模型库目录不能为空。", nameof(rootDirectory));
        }

        var full = Path.GetFullPath(rootDirectory.Trim());
        RootDirectory = full.Length > 3
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    /// <summary>模型库根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>扫描模型库；库不存在时返回空列表，不会抛异常。</summary>
    public IReadOnlyList<Live2DModelInfo> Scan()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return Array.Empty<Live2DModelInfo>();
        }

        var result = new List<Live2DModelInfo>();
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory)
                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            var modelJson = FindModelJson(directory);
            if (modelJson is null)
            {
                continue;
            }

            result.Add(new Live2DModelInfo(Path.GetFileName(directory), directory, modelJson));
        }

        return result;
    }

    /// <summary>导入一个模型文件夹（复制到模型库，同名自动追加序号）。</summary>
    public Live2DModelInfo Import(string sourceDirectory)
    {
        var source = NormalizePath(sourceDirectory, "模型目录");
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("模型目录不存在：" + source);
        }

        if (Contains(source))
        {
            throw new InvalidOperationException("该目录已经在模型库中，无需重复导入。");
        }

        return ImportCore(source, preferredName: null);
    }

    /// <summary>导入一个包含模型的 zip 压缩包（解压后按文件夹导入）。</summary>
    public Live2DModelInfo ImportZip(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            throw new FileNotFoundException("找不到要导入的压缩包。", zipPath);
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "voxime-model-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            try
            {
                ZipFile.ExtractToDirectory(zipPath, tempRoot);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException("压缩包格式无效或已损坏：" + ex.Message, ex);
            }

            var source = ResolveZipModelRoot(tempRoot)
                ?? throw new InvalidOperationException("压缩包中没有找到 *.model3.json 模型文件。");
            var preferredName = string.Equals(source, tempRoot, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(zipPath)
                : Path.GetFileName(source);
            return ImportCore(source, preferredName);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>删除模型库内的一个模型（只允许删除库内目录）。</summary>
    public void Delete(string modelDirectory)
    {
        var target = NormalizePath(modelDirectory, "模型目录");
        if (!Contains(target) || string.Equals(target, RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只能删除模型库内的模型。");
        }

        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException("模型目录不存在：" + target);
        }

        Directory.Delete(target, recursive: true);
    }

    /// <summary>判断路径是否位于模型库内（含库根目录）。</summary>
    public bool Contains(string path)
    {
        var full = NormalizePath(path, "路径");
        return string.Equals(full, RootDirectory, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private Live2DModelInfo ImportCore(string source, string? preferredName)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("模型目录不存在：" + source);
        }

        var modelJson = FindModelJson(source)
            ?? throw new InvalidOperationException("所选目录中没有找到 *.model3.json 模型文件。");

        var name = SanitizeName(preferredName);
        if (name.Length == 0)
        {
            // 优先用模型文件名（Hiyori.model3.json → Hiyori），比文件夹名更稳定、可读。
            var modelFileName = Path.GetFileName(modelJson);
            var modelBaseName = modelFileName.EndsWith(ModelFileSuffix, StringComparison.OrdinalIgnoreCase)
                ? modelFileName[..^ModelFileSuffix.Length]
                : Path.GetFileNameWithoutExtension(modelFileName);
            name = SanitizeName(modelBaseName);
        }

        if (name.Length == 0)
        {
            name = SanitizeName(Path.GetFileName(source));
        }

        if (name.Length == 0)
        {
            name = "model";
        }

        Directory.CreateDirectory(RootDirectory);
        var target = CreateUniqueDirectory(name);
        CopyDirectory(source, target);

        var targetJson = Path.Combine(target, Path.GetRelativePath(source, modelJson));
        return new Live2DModelInfo(Path.GetFileName(target), target, targetJson);
    }

    private string CreateUniqueDirectory(string name)
    {
        var candidate = name;
        var index = 2;
        while (Directory.Exists(Path.Combine(RootDirectory, candidate)))
        {
            candidate = $"{name} ({index})";
            index++;
        }

        var target = Path.Combine(RootDirectory, candidate);
        Directory.CreateDirectory(target);
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(file, destination, overwrite: false);
        }
    }

    private static string? ResolveZipModelRoot(string tempRoot)
    {
        if (Directory.EnumerateFiles(tempRoot, ModelFilePattern, SearchOption.TopDirectoryOnly).Any())
        {
            return tempRoot;
        }

        var candidates = Directory.EnumerateDirectories(tempRoot)
            .Where(d => !Path.GetFileName(d).StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
            .Where(d => FindModelJson(d) is not null)
            .ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            > 1 => throw new InvalidOperationException("压缩包中包含多个模型，请分别打包后导入。"),
            _ => null
        };
    }

    private static string? FindModelJson(string directory)
    {
        try
        {
            var direct = Directory.EnumerateFiles(directory, ModelFilePattern, SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return direct ?? Directory.EnumerateFiles(directory, ModelFilePattern, SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string NormalizePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{label}不能为空。");
        }

        var full = Path.GetFullPath(path.Trim());
        return full.Length > 3
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length > 60 ? result[..60].TrimEnd('.', ' ') : result;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响导入结果。
        }
    }
}
