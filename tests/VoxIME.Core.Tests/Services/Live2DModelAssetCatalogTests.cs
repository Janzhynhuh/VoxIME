using VoxIME.Core.Services;

namespace VoxIME.Core.Tests.Services;

public sealed class Live2DModelAssetCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "voxime-catalog-" + Guid.NewGuid().ToString("N"));

    public Live2DModelAssetCatalogTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Load_MergesModelReferencesAndVTubeFolders()
    {
        var exp3 = Path.Combine(_root, "Exp3");
        var animation = Path.Combine(_root, "Animation");
        Directory.CreateDirectory(exp3);
        Directory.CreateDirectory(animation);
        File.WriteAllText(Path.Combine(exp3, "脸红.exp3.json"), "{}");
        File.WriteAllText(Path.Combine(exp3, "爱心眼.exp3.json"), "{}");
        File.WriteAllText(Path.Combine(animation, "IDLE.motion3.json"), "{}");
        File.WriteAllText(Path.Combine(animation, "举手.motion3.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "model.model3.json"), """
        {
          "Version": 3,
          "FileReferences": {
            "Expressions": [ { "Name": "registered", "File": "custom/registered.exp3.json" } ],
            "Motions": { "Idle": [ { "File": "Animation/IDLE.motion3.json" } ] }
          }
        }
        """);

        var catalog = Live2DModelAssetCatalog.Load(_root);

        Assert.NotNull(catalog.ModelJsonPath);
        Assert.Contains(catalog.Expressions, item => item.Name == "registered" && item.File == "custom/registered.exp3.json");
        Assert.Contains(catalog.Expressions, item => item.Name == "脸红" && item.File == "Exp3/脸红.exp3.json");
        Assert.Contains(catalog.Expressions, item => item.Name == "爱心眼");
        Assert.Contains(catalog.Motions, item => item.Group == "Idle" && item.File == "Animation/IDLE.motion3.json");
        Assert.Contains(catalog.Motions, item => item.Group == "举手");
        Assert.Equal(catalog.Motions.Select(item => item.Group + "|" + item.File).Distinct().Count(), catalog.Motions.Count);
    }

    [Fact]
    public void Load_MissingOrInvalidDirectory_ReturnsEmpty()
    {
        Assert.Empty(Live2DModelAssetCatalog.Load(null).Expressions);
        Assert.Empty(Live2DModelAssetCatalog.Load(Path.Combine(_root, "missing")).Expressions);
        Assert.Empty(Live2DModelAssetCatalog.Empty.Motions);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 测试临时目录清理失败不影响结果。
        }
    }
}
