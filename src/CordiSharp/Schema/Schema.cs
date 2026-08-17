using System.Collections;
using System.Globalization;

namespace CordiSharp.Schema;

/// <summary>A single validation issue reported by a <see cref="Schema"/>.</summary>
public sealed class SchemaIssue(string message, string? path = null)
{
    public string Message { get; } = message;
    public string? Path { get; } = path;

    public override string ToString() => Path is null ? Message : $"{Message} (at {Path})";
}

/// <summary>Thrown when a config object fails schema validation.</summary>
public sealed class SchemaValidationException(IReadOnlyList<SchemaIssue> issues)
    : CordisException("invalid config:\n" + string.Join("\n", issues.Select(i => "  - " + i)))
{
    public IReadOnlyList<SchemaIssue> Issues { get; } = issues;
}

/// <summary>Base class for config schemas (a pragmatic subset of schemastery).</summary>
public abstract class Schema
{
    /// <summary>Implicitly converts a CLR type to its schema (see <see cref="FromType"/>).</summary>
    public static implicit operator Schema(Type type) => FromType(type);
    
    /// <summary>Validate <paramref name="input"/>, appending issues and returning the coerced value.</summary>
    public abstract object? Validate(object? input, List<SchemaIssue> issues, string path);

    /// <summary>Validate; throws <see cref="SchemaValidationException"/> on failure.</summary>
    public object? Parse(object? input)
    {
        var issues = new List<SchemaIssue>();
        var value = Validate(input, issues, "");
        return issues.Count > 0 ? throw new SchemaValidationException(issues) : value;
    }

    public T Parse<T>(object? input) => (T)Parse(input)!;

    /// <summary>Merge multiple config values. Object schemas shallow-merge dictionaries;
    /// scalar schemas take the last non-null value.</summary>
    public virtual object? Merge(params object?[] configs) => configs.LastOrDefault(c => c is not null);

    /// <summary>Wraps this schema so that a default value is used when input is null.</summary>
    public Schema WithDefault(object? value) => new DefaultSchema(this, value);

    /// <summary>Wraps this schema so that null input is allowed.</summary>
    public Schema AsOptional() => new OptionalSchema(this);

    /// <summary>Wraps this schema with a value transform applied after validation.</summary>
    public Schema Transform(Func<object?, object?> transform) => new TransformSchema(this, transform);

    // ---- factories ----

    public static Schema String() => new StringSchema();
    public static Schema Number() => new NumberSchema();
    public static Schema Integer() => new IntegerSchema();
    public static Schema Boolean() => new BooleanSchema();
    public static Schema Any() => new AnySchema();
    public static Schema Object(IReadOnlyDictionary<string, Schema> fields, bool strict = false) => new ObjectSchema(fields, strict);
    public static Schema Array(Schema item) => new ArraySchema(item);
    public static Schema Tuple(params Schema[] items) => new TupleSchema(items);
    public static Schema Union(params Schema[] schemas) => new UnionSchema(schemas);
    public static Schema Optional(Schema inner) => new OptionalSchema(inner);
    public static Schema Default(Schema inner, object? value) => new DefaultSchema(inner, value);
    public static Schema Transform(Schema inner, Func<object?, object?> transform) => new TransformSchema(inner, transform);
    public static Schema Record(Schema value) => new RecordSchema(value);
    public static Schema Literal(object? value) => new LiteralSchema(value);

    /// <summary>Builds a schema from a CLR type (reflection fallback used when the
    /// source generator is not available).</summary>
    public static Schema FromType(Type type, int depth = 0)
    {
        if (depth > 8) return Any();
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
        {
            return Optional(FromType(nullable, depth + 1));
        }
        if (type == typeof(string)) return String();
        if (type == typeof(bool)) return Boolean();
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return Integer();
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return Number();
        if (type.IsEnum) return Union(type.GetEnumValues().Cast<object?>().Select(Literal).ToArray());
        if (type.IsArray)
        {
            var element = FromType(type.GetElementType()!, depth + 1);
            return Array(element);
        }
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>))
                return Array(FromType(type.GetGenericArguments()[0], depth + 1));
            if (def == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string)
                || def == typeof(IDictionary<,>) && type.GetGenericArguments()[0] == typeof(string))
                return Record(FromType(type.GetGenericArguments()[1], depth + 1));
        }
        // nested config class
        var fields = new Dictionary<string, Schema>();
        foreach (var prop in type.GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            var inner = FromType(prop.PropertyType, depth + 1);
            var attr = prop.GetCustomAttributes(typeof(DefaultValueAttribute), true).FirstOrDefault() as DefaultValueAttribute;
            if (attr is not null) inner = Default(inner, attr.Value);
            fields[prop.Name] = inner;
        }
        return Object(fields);
    }

    // ---- concrete schema types ----

    private sealed class AnySchema : Schema
    {
        public override object? Validate(object? input, List<SchemaIssue> issues, string path) => input;
    }

    private sealed class LiteralSchema(object? value) : Schema
    {
        public override object? Validate(object? input, List<SchemaIssue> issues, string path)
        {
            if (!Equals(input, value))
            {
                issues.Add(new SchemaIssue($"expected literal {value ?? "null"}", path));
                return value;
            }
            return input;
        }
    }

    private sealed class StringSchema : Schema
    {
        public override object? Validate(object? input, List<SchemaIssue> issues, string path)
        {
            if (input is null) { issues.Add(new SchemaIssue("expected string", path)); return null; }
            return Convert.ToString(input, CultureInfo.InvariantCulture) ?? "";
        }
    }

    private sealed class NumberSchema : Schema
    {
        public override object Validate(object? input, List<SchemaIssue> issues, string path)
        {
            if (input is null) { issues.Add(new SchemaIssue("expected number", path)); return 0d; }
            try { return Convert.ToDouble(input, CultureInfo.InvariantCulture); }
            catch { issues.Add(new SchemaIssue("expected number", path)); return 0d; }
        }
    }

    private sealed class IntegerSchema : Schema
    {
        public override object Validate(object? input, List<SchemaIssue> issues, string path)
        {
            if (input is null) { issues.Add(new SchemaIssue("expected integer", path)); return 0L; }
            try { return Convert.ToInt64(input, CultureInfo.InvariantCulture); }
            catch { issues.Add(new SchemaIssue("expected integer", path)); return 0L; }
        }
    }

    private sealed class BooleanSchema : Schema
    {
        public override object Validate(object? input, List<SchemaIssue> issues, string path)
        {
            if (input is null) { issues.Add(new SchemaIssue("expected boolean", path)); return false; }
            if (input is bool b) return b;
            if (bool.TryParse(Convert.ToString(input), out var parsed)) return parsed;
            issues.Add(new SchemaIssue("expected boolean", path));
            return false;
        }
    }

    private sealed class ObjectSchema(IReadOnlyDictionary<string, Schema> fields, bool strict) : Schema
    {
        public override object? Validate(object? input, List<SchemaIssue> issues, string path)
        {
            var isPoco = input is not null && input is not IDictionary<string, object?>;
            var dict = input as IDictionary<string, object?> ?? ToDictionary(input);
            var result = new Dictionary<string, object?>();
            foreach (var (key, schema) in fields)
            {
                dict!.TryGetValue(key, out var raw);
                var childPath = path.Length == 0 ? key : path + "." + key;
                result[key] = schema.Validate(raw, issues, childPath);
            }
            if (strict && dict is not null)
            {
                foreach (var key in dict.Keys)
                {
                    if (!fields.ContainsKey(key))
                        issues.Add(new SchemaIssue($"unexpected key \"{key}\"", path));
                }
            }
            // preserve the original POCO instance (typed configs stay typed)
            return isPoco ? input : result;
        }

        public override object Merge(params object?[] configs)
        {
            var merged = new Dictionary<string, object?>();
            foreach (var config in configs)
            {
                if (config is null) continue;
                var dict = config as IDictionary<string, object?> ?? ToDictionary(config);
                if (dict is null) continue;
                foreach (var (key, value) in dict) merged[key] = value;
            }
            return merged;
        }

        private static IDictionary<string, object?>? ToDictionary(object? value)
        {
            if (value is null) return null;
            if (value is IDictionary<string, object?> dict) return dict;
            var result = new Dictionary<string, object?>();
            foreach (var prop in value.GetType().GetProperties())
            {
                if (!prop.CanRead) continue;
                result[prop.Name] = prop.GetValue(value);
            }
            return result;
        }
    }

    private sealed class ArraySchema(Schema item) : Schema
    {
        public override object Validate(object? input, List<SchemaIssue> issues, string path)
        {
            if (input is null) { issues.Add(new SchemaIssue("expected array", path)); return System.Array.Empty<object?>(); }
            var items = (input as IEnumerable)?.Cast<object?>().ToList()
                ?? throw new InvalidOperationException("expected array");
            var result = new object?[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                var childPath = path.Length == 0 ? i.ToString() : path + "." + i;
                result[i] = item.Validate(items[i], issues, childPath);
            }
            return result;
        }
    }

    private sealed class TupleSchema(Schema[] items) : Schema
    {
        public override object Validate(object? input, List<SchemaIssue> issues, string path)
        {
            var items1 = (input as IEnumerable)?.Cast<object?>().ToList();
            var result = new object?[items.Length];
            for (var i = 0; i < items.Length; i++)
            {
                var childPath = path.Length == 0 ? i.ToString() : path + "." + i;
                result[i] = items[i].Validate(items1 is not null && i < items1.Count ? items1[i] : null, issues, childPath);
            }
            return result;
        }
    }

    private sealed class UnionSchema(Schema[] schemas) : Schema
    {
        public override object? Validate(object? input, List<SchemaIssue> issues, string path)
        {
            foreach (var schema in schemas)
            {
                var local = new List<SchemaIssue>();
                var value = schema.Validate(input, local, path);
                if (local.Count == 0) return value;
            }
            issues.Add(new SchemaIssue("expected union", path));
            return input;
        }
    }

    private sealed class OptionalSchema(Schema inner) : Schema
    {
        public override object? Validate(object? input, List<SchemaIssue> issues, string path)
        {
            if (input is null) return null;
            return inner.Validate(input, issues, path);
        }
    }

    private sealed class DefaultSchema(Schema inner, object? value) : Schema
    {
        public override object? Validate(object? input, List<SchemaIssue> issues, string path)
        {
            if (input is null) return inner.Validate(value, issues, path);
            return inner.Validate(input, issues, path);
        }
    }

    private sealed class TransformSchema(Schema inner, Func<object?, object?> transform) : Schema
    {
        public override object? Validate(object? input, List<SchemaIssue> issues, string path)
        {
            var value = inner.Validate(input, issues, path);
            if (issues.Count > 0) return value;
            try { return transform(value); }
            catch (Exception e) { issues.Add(new SchemaIssue(e.Message, path)); return value; }
        }
    }

    private sealed class RecordSchema(Schema value) : Schema
    {
        public override object Validate(object? input, List<SchemaIssue> issues, string path)
        {
            var result = new Dictionary<string, object?>();
            if (input is IDictionary<string, object?> dict)
            {
                foreach (var (key, value1) in dict)
                {
                    var childPath = path.Length == 0 ? key : path + "." + key;
                    result[key] = value.Validate(value1, issues, childPath);
                }
            }
            else if (input is not null)
            {
                issues.Add(new SchemaIssue("expected record", path));
            }
            return result;
        }
    }
}

/// <summary>Marks a class as a plugin config type (used by the source generator and
/// <see cref="Schema.FromType"/>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PluginConfigAttribute : Attribute;

/// <summary>Declares a default value for a config property.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DefaultValueAttribute(object? value) : Attribute
{
    public object? Value { get; } = value;
}

/// <summary>Marks a config property as required (cannot be null).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequiredAttribute : Attribute;