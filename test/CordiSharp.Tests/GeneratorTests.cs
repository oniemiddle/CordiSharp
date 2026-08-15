using CordiSharp.Registry;
using CordiSharp.Schema;
using Xunit;

namespace CordiSharp.Tests;

public class GeneratorTests
{
    [Fact]
    public async Task GeneratedMetadata_RegisteredAndUsed()
    {
        GeneratedDemoPlugin.LoadCount = 0;
        var root = Context.Create();

        // the source generator should have registered metadata for this assembly
        PluginMetadataRegistry.EnsureGeneratedRegistrations();
        var metadata = PluginMetadataRegistry.Get(typeof(GeneratedDemoPlugin));
        Assert.NotNull(metadata);
        Assert.Equal("generated-demo", metadata.Name);
        Assert.Equal(typeof(GeneratedDemoConfig), metadata.ConfigType);
        Assert.NotNull(metadata.ConfigSchema);

        var handle = root.Plugin(typeof(GeneratedDemoPlugin), new GeneratedDemoConfig { Name = "x", Tags = ["a"] });
        await handle.Await();
        Assert.Equal(1, GeneratedDemoPlugin.LoadCount);
        Assert.Equal("x", GeneratedDemoPlugin.LastName);
        Assert.Equal("generated-demo", handle.Ctx.Name);

        // invalid config is rejected by the generated schema
        var bad = root.Plugin(typeof(GeneratedDemoPlugin), new Dictionary<string, object?> { ["Name"] = "x", ["Count"] = "not-an-int" });
        await Assert.ThrowsAsync<SchemaValidationException>(bad.Await);
        await handle.DisposeAsync();
    }

    [Plugin("generated-demo")]
    public sealed class GeneratedDemoPlugin : IPlugin<GeneratedDemoConfig>
    {
        public static int LoadCount;
        public static string? LastName;

        public void Load(Context ctx, GeneratedDemoConfig config)
        {
            LoadCount++;
            LastName = config.Name;
        }
    }

    [PluginConfig]
    public sealed class GeneratedDemoConfig
    {
        [DefaultValue("default-name")]
        public string? Name { get; set; }

        public int Count { get; set; }

        public List<string>? Tags { get; set; }
    }
}