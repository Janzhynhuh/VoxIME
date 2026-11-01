using System.IO.Compression;
using VoxIME.Core.Services;

namespace VoxIME.Core.Tests.Services;

public sealed class Live2DModelLibraryTests
{
    [Fact]
    public void Scan_ReturnsOnlyFoldersContainingModel3Json()
    {
        var root = CreateTempRoot();
        try
        {
            var library = new Live2DModelLibrary(Path.Combine(root, "library"));
            CreateModel(Path.Combine(library.RootDirectory, "Miku"), "Miku");
            Directory.CreateDirectory(Path.Combine(library.RootDirectory, "not-a-model"));

            var models = library.Scan();

            var model = Assert.Single(models);
            Assert.Equal("Miku", model.Name);
            Assert.EndsWith("Miku.model3.json", model.ModelJsonPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Scan_ReturnsEmptyWhenLibraryDoesNotExist()
    {
        var root = CreateTempRoot();
        try
        {
            var library = new Live2DModelLibrary(Path.Combine(root, "missing"));
            Assert.Empty(library.Scan());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Import_CopiesModelFiles_AndUniquifiesName()
    {
        var root = CreateTempRoot();
        try
        {
            var source = CreateModel(Path.Combine(root, "source"), "Hiyori");
            var library = new Live2DModelLibrary(Path.Combine(root, "library"));

            var first = library.Import(source);
            var second = library.Import(source);

            Assert.Equal("Hiyori", first.Name);
            Assert.Equal("Hiyori (2)", second.Name);
            Assert.True(File.Exists(Path.Combine(first.Directory, "Hiyori.model3.json")));
            Assert.True(File.Exists(Path.Combine(first.Directory, "textures", "tex.png")));
            Assert.True(File.Exists(first.ModelJsonPath));
            // 原目录保持不变
            Assert.True(Directory.Exists(source));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Import_RejectsDirectoryWithoutModel3Json()
    {
        var root = CreateTempRoot();
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "readme.txt"), "no model");

            var library = new Live2DModelLibrary(Path.Combine(root, "library"));

            var ex = Assert.Throws<InvalidOperationException>(() => library.Import(source));
            Assert.Contains("model3.json", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImportZip_ExtractsNestedModelFolder()
    {
        var root = CreateTempRoot();
        try
        {
            var sourceParent = Path.Combine(root, "source");
            CreateModel(Path.Combine(sourceParent, "Shizuku"), "Shizuku");
            var zipPath = Path.Combine(root, "Shizuku.zip");
            ZipFile.CreateFromDirectory(sourceParent, zipPath);

            var library = new Live2DModelLibrary(Path.Combine(root, "library"));
            var imported = library.ImportZip(zipPath);

            Assert.Equal("Shizuku", imported.Name);
            Assert.True(File.Exists(Path.Combine(imported.Directory, "Shizuku.model3.json")));
            Assert.Single(library.Scan());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Delete_RejectsPathOutsideLibrary()
    {
        var root = CreateTempRoot();
        try
        {
            var outside = CreateModel(Path.Combine(root, "outside"), "Outside");
            var library = new Live2DModelLibrary(Path.Combine(root, "library"));

            var ex = Assert.Throws<InvalidOperationException>(() => library.Delete(outside));
            Assert.Contains("模型库内", ex.Message);
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Delete_RemovesImportedModel()
    {
        var root = CreateTempRoot();
        try
        {
            var source = CreateModel(Path.Combine(root, "source"), "DeleteMe");
            var library = new Live2DModelLibrary(Path.Combine(root, "library"));
            var imported = library.Import(source);

            library.Delete(imported.Directory);

            Assert.Empty(library.Scan());
            Assert.False(Directory.Exists(imported.Directory));
            Assert.True(Directory.Exists(source));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "voxime-model-library-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateModel(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".model3.json"), "{}");
        File.WriteAllText(Path.Combine(directory, name + ".moc3"), "moc3");
        var textures = Path.Combine(directory, "textures");
        Directory.CreateDirectory(textures);
        File.WriteAllBytes(Path.Combine(textures, "tex.png"), new byte[] { 1, 2, 3 });
        return directory;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 测试临时目录清理失败忽略。
        }
    }
}
