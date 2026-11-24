// Public API diff by reflection: lists every public/protected type and member signature of two builds.
using System.Reflection;
using System.Runtime.Loader;
static IEnumerable<string> Api(string path)
{
    var alc = new AssemblyLoadContext(path, isCollectible: true);
    alc.Resolving += (c, n) => { var p = Path.Combine(Path.GetDirectoryName(path)!, n.Name + ".dll"); return File.Exists(p) ? c.LoadFromAssemblyPath(p) : null; };
    var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(path));
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    foreach (var t in asm.GetExportedTypes())
    {
        string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";
        yield return $"{kind} {t.FullName}" + (t.IsEnum ? " { " + string.Join(", ", Enum.GetNames(t)) + " }" : "");
        foreach (var m in t.GetMembers(F))
        {
            bool visible = m switch
            {
                MethodBase mb => mb.IsPublic || mb.IsFamily || mb.IsFamilyOrAssembly,
                FieldInfo fi => (fi.IsPublic || fi.IsFamily) && !t.IsEnum,
                PropertyInfo pi => (pi.GetMethod ?? pi.SetMethod) is { } a && (a.IsPublic || a.IsFamily),
                EventInfo ei => ei.AddMethod is { } a2 && (a2.IsPublic || a2.IsFamily),
                Type nt => nt.IsNestedPublic,
                _ => false,
            };
            if (!visible) continue;
            if (m is MethodInfo mi && mi.IsSpecialName) continue;
            string sig = m switch
            {
                MethodInfo mi2 => $"{(mi2.IsStatic ? "static " : "")}{mi2.ReturnType} {mi2.Name}({string.Join(", ", mi2.GetParameters().Select(p => $"{p.ParameterType} {p.Name}{(p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : "")}"))})",
                ConstructorInfo ci => $".ctor({string.Join(", ", ci.GetParameters().Select(p => $"{p.ParameterType} {p.Name}{(p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : "")}"))})",
                PropertyInfo pi2 => $"{pi2.PropertyType} {pi2.Name} {{ {(pi2.GetMethod?.IsPublic == true ? "get; " : "")}{(pi2.SetMethod?.IsPublic == true ? (pi2.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Any(x => x.Name == "IsExternalInit") ? "init; " : "set; ") : "")}}}",
                FieldInfo fi2 => $"{(fi2.IsStatic ? "static " : "")}{fi2.FieldType} {fi2.Name}",
                EventInfo ei2 => $"event {ei2.EventHandlerType} {ei2.Name}",
                _ => m.ToString()!,
            };
            yield return $"  {t.FullName}::{sig}";
        }
    }
}
var a = Api(args[0]).ToHashSet(); var b = Api(args[1]).ToHashSet();
Console.WriteLine($"# baseline {a.Count} entries, candidate {b.Count} entries");
Console.WriteLine("## Removed or changed (present only in baseline)");
foreach (var s in a.Except(b).Order()) Console.WriteLine("- " + s.Trim());
Console.WriteLine("## Added (present only in candidate)");
foreach (var s in b.Except(a).Order()) Console.WriteLine("+ " + s.Trim());
