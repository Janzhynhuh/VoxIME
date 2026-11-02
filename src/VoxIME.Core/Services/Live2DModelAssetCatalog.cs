using System.Text.Json;

namespace VoxIME.Core.Services;

/// <summary>模型里可用的一个表情资源（Name 用于状态映射，File 相对 model3.json 所在目录）。</summary>
public sealed record Live2DExpressionAsset(string Name, string File);

/// <summary>模型里可用的一个动作资源（Group 为 motion 组名，File 相对 model3.json 所在目录）。</summary>
public sealed record Live2DMotionAsset(string Group, string File);

/// <summary>
/// 扫描 Live2D 模型的表情/动作资源。
/// 除 model3.json 中已登记的 FileReferences.Expressions/Motions 外，还兼容
/// VTube Studio 常见的 <c>Exp3</c> / <c>Animation</c> 文件夹（这些文件经常没有被写进 model3.json）。
/// </summary>
public sealed class Live2DModelAssetCatalog
{
    private static readonly Lazy<Live2DModelAssetCatalog> EmptyCatalog =
        new(() => new Live2DModelAssetCatalog(null, Array.Empty<Live2DExpressionAsset>(), Array.Empty<Live2DMotionAsset>()));

    public static Live2DModelAssetCatalog Empty => EmptyCatalog.Value;

    private Live2DModelAssetCatalog(
        string? modelJsonPath,
        IReadOnlyList<Live2DExpressionAsset> expressions,
        IReadOnlyList<Live2DMotionAsset> motions)
    {
        ModelJsonPath = modelJsonPath;
        Expressions = expressions;
        Motions = motions;
    }

    public string? ModelJsonPath { get; }
    public IReadOnlyList<Live2DExpressionAsset> Expressions { get; }
    public IReadOnlyList<Live2DMotionAsset> Motions { get; }

    /// <summary>读取模型目录下第一个 .model3.json，同时合并 Exp3/Animation 文件夹中的资源。</summary>
    public static Live2DModelAssetCatalog Load(string? modelDirectory)
    {
        var modelFile = FindModelJson(modelDirectory);
        if (modelFile is null)
        {
            return Empty;
        }

        var root = Path.GetDirectoryName(modelFile)!;
        var expressionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var motionList = new List<Live2DMotionAsset>();
        var motionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(modelFile));
            if (document.RootElement.TryGetProperty("FileReferences", out var refs))
            {
                ReadExpressions(refs, root, expressionMap);
                ReadMotions(refs, root, motionList, motionKeys);
            }
        }
        catch (Exception)
        {
            // 模型 JSON 损坏时仍尝试扫描目录，UI 至少能显示可恢复的项。
        }

        DiscoverExpressionFiles(root, expressionMap);
        DiscoverMotionFiles(root, motionList, motionKeys);

        var expressions = expressionMap
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new Live2DExpressionAsset(pair.Key, pair.Value))
            .ToArray();
        var motions = motionList
            .OrderBy(asset => asset.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.File, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new Live2DModelAssetCatalog(modelFile, expressions, motions);
    }

    private static string? FindModelJson(string? modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(modelDirectory, "*.model3.json", SearchOption.AllDirectories)
                .OrderBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ReadExpressions(JsonElement refs, string root, Dictionary<string, string> expressions)
    {
        if (!refs.TryGetProperty("Expressions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(item, "Name");
            var file = GetString(item, "File");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            expressions.TryAdd(name.Trim(), ToRelative(root, file));
        }
    }

    private static void ReadMotions(
        JsonElement refs,
        string root,
        List<Live2DMotionAsset> motions,
        HashSet<string> motionKeys)
    {
        if (!refs.TryGetProperty("Motions", out var groups) || groups.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in group.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var file = GetString(item, "File");
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                var relative = ToRelative(root, file);
                if (motionKeys.Add(group.Name + "|" + relative))
                {
                    motions.Add(new Live2DMotionAsset(group.Name, relative));
                }
            }
        }
    }

    private static void DiscoverExpressionFiles(string root, Dictionary<string, string> expressions)
    {
        foreach (var file in EnumerateAssetFiles(root, "*.exp3.json", "exp3", "expressions"))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^".exp3.json".Length];
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                expressions.TryAdd(name, ToRelative(root, file));
            }
        }
    }

    private static void DiscoverMotionFiles(
        string root,
        List<Live2DMotionAsset> motions,
        HashSet<string> motionKeys)
    {
        foreach (var file in EnumerateAssetFiles(root, "*.motion3.json", "animation", "motions", "motion"))
        {
            var relative = ToRelative(root, file);
            // model3.json 已登记的动作不要再用扫描副本重复注入。
            if (motions.Any(item => string.Equals(item.File, relative, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var stem = Path.GetFileName(file);
            if (stem.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^".motion3.json".Length];
            }

            // Animation/IDLE 之类是 VTube Studio 的一次性动作，不是 model3 声明的 Idle 组；
            // 直接命名为 Idle 会被默认待机逻辑自动播放并抑制眨眼/物理效果。
            // 用 Animation/ 前缀保留原文件名，避免与 model3 原生动作组重名。
            var group = "Animation/" + stem;
            if (motionKeys.Add(group + "|" + relative))
            {
                motions.Add(new Live2DMotionAsset(group, relative));
            }
        }
    }

    private static IEnumerable<string> EnumerateAssetFiles(string root, string pattern, params string[] folderNames)
    {
        var wanted = new HashSet<string>(folderNames, StringComparer.OrdinalIgnoreCase);
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(file =>
                {
                    var directory = Path.GetDirectoryName(file);
                    while (!string.IsNullOrEmpty(directory)
                           && !string.Equals(Path.GetFullPath(directory), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    {
                        if (wanted.Contains(Path.GetFileName(directory)))
                        {
                            return true;
                        }

                        directory = Path.GetDirectoryName(directory);
                    }

                    return false;
                })
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string ToRelative(string root, string file)
    {
        var full = Path.IsPathRooted(file) ? file : Path.Combine(root, file);
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }
}
