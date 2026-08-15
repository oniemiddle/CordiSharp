using CordiSharp.Schema;
using Xunit;
using S = CordiSharp.Schema.Schema;

namespace CordiSharp.Tests;

public class SchemaTests
{
    [Fact]
    public void ObjectSchema_Parses()
    {
        var schema = S.Object(new Dictionary<string, S>
        {
            ["name"] = S.String(),
            ["count"] = S.Integer().WithDefault(3),
            ["tags"] = S.Array(S.String()),
        });
        var result = (Dictionary<string, object?>)schema.Parse(new Dictionary<string, object?>
        {
            ["name"] = "x",
            ["tags"] = new[] { "a", "b" },
        })!;
        Assert.Equal("x", result["name"]);
        Assert.Equal(3L, result["count"]);
        Assert.Equal(2, ((object?[])result["tags"]!).Length);
    }

    [Fact]
    public void ObjectSchema_RejectsMissingRequired()
    {
        var schema = S.Object(new Dictionary<string, S> { ["name"] = S.String() });
        Assert.Throws<SchemaValidationException>(() => schema.Parse(new Dictionary<string, object?>()));
    }

    [Fact]
    public void DefaultSchema_UsesDefault()
    {
        var schema = S.Default(S.Integer(), 7);
        Assert.Equal(7L, schema.Parse(null));
    }

    [Fact]
    public void UnionSchema_AcceptsAnyBranch()
    {
        var schema = S.Union(S.Literal("a"), S.Literal("b"));
        Assert.Equal("a", schema.Parse("a"));
        Assert.Equal("b", schema.Parse("b"));
        Assert.Throws<SchemaValidationException>(() => schema.Parse("c"));
    }

    [Fact]
    public void TransformSchema_AppliesTransform()
    {
        var schema = S.Transform(S.String(), v => ((string)v!).ToUpperInvariant());
        Assert.Equal("ABC", schema.Parse("abc"));
    }

    [Fact]
    public void FromType_ReflectionFallback()
    {
        S schema = typeof(TestConfig);
        var result = schema.Parse(new TestConfig { Name = "n", Count = 5 })!;
        // POCO inputs are preserved; validate fields
        Assert.Equal("n", ((TestConfig)result).Name);
        Assert.Equal(5, ((TestConfig)result).Count);
    }

    [Fact]
    public void Type_ImplicitlyConvertsToSchema()
    {
        S stringSchema = typeof(string);
        Assert.Equal("abc", stringSchema.Parse("abc"));

        S intSchema = typeof(int);
        Assert.Equal(42L, intSchema.Parse("42"));

        S configSchema = typeof(TestConfig);
        var result = configSchema.Parse(new TestConfig { Name = "n", Count = 5 })!;
        Assert.Equal("n", ((TestConfig)result).Name);
    }

    [Fact]
    public void Merge_Objects_ShallowMerge()
    {
        var schema = S.Object(new Dictionary<string, S> { ["a"] = S.String() });
        var merged = (Dictionary<string, object?>)schema.Merge(
            new Dictionary<string, object?> { ["a"] = "1", ["b"] = "2" },
            new Dictionary<string, object?> { ["a"] = "3" })!;
        Assert.Equal("3", merged["a"]);
        Assert.Equal("2", merged["b"]);
    }

    public sealed class TestConfig
    {
        public string? Name { get; set; }
        public int Count { get; set; }
        public bool Enabled { get; set; }
    }
}