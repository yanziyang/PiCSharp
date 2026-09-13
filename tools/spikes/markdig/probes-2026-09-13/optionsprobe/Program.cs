using System.Reflection;
using Markdig.Extensions.AutoLinks;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;

Dump(typeof(AutoLinkOptions), new AutoLinkOptions());
Dump(typeof(PipeTableOptions), new PipeTableOptions());
Console.WriteLine("EmphasisExtraOptions: " + string.Join(", ", Enum.GetNames<EmphasisExtraOptions>()) + " | Default = " + EmphasisExtraOptions.Default);

Console.WriteLine("AutoLinkParser fields:");
foreach (var f in typeof(AutoLinkParser).GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
{
    var value = f.IsStatic ? Format(f.GetValue(null)) : "(instance)";
    Console.WriteLine("  " + (f.IsStatic ? "static " : string.Empty) + f.FieldType.Name + " " + f.Name + " = " + value);
}

Console.WriteLine("AutoLinkParser constructors:");
foreach (var c in typeof(AutoLinkParser).GetConstructors())
{
    Console.WriteLine("  (" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
}

static void Dump(Type t, object instance)
{
    Console.WriteLine(t.Name + ":");
    foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
    {
        Console.WriteLine("  " + p.PropertyType.Name + " " + p.Name + " = " + Format(p.GetValue(instance)));
    }
}

static string Format(object? v)
{
    var q = ((char)34).ToString();
    if (v is null)
    {
        return "null";
    }

    if (v is string s)
    {
        return q + s + q;
    }

    if (v is System.Collections.IEnumerable e)
    {
        var items = new List<string>();
        foreach (var x in e)
        {
            items.Add(x?.ToString() ?? "null");
        }

        return "[" + string.Join(", ", items) + "]";
    }

    return v.ToString() ?? string.Empty;
}
