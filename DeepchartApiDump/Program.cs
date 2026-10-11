// Liest die öffentliche Schnittstelle (Typen, Methoden, Eigenschaften, Enums) aus den Deepchart-DLLs
// und schreibt sie in eine Textdatei. Die DLLs werden nur als Metadaten gelesen (MetadataLoadContext),
// es wird kein Deepchart-Code ausgeführt.
//
// Aufruf:
//   dotnet run
//   dotnet run -- --dir "D:\Apps\Deepchart" --out "C:\Temp\deepchart_api.txt"
//   dotnet run -- --all          (alle Namespaces statt nur der Indikator-relevanten)

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

var dir = Arg("--dir") ?? @"C:\Program Files\Volumetrica Trading\Deepchart";
var outPath = Arg("--out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "deepchart_api.txt");
var files = (Arg("--files") ?? "VolSysAPI.dll,VolumetricaCore.dll").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var namespaces = (Arg("--namespaces") ?? "VolSysAPI,VolumetricaAPI,VolumetricaControls").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var all = args.Contains("--all");

if (!Directory.Exists(dir))
{
    Console.Error.WriteLine($"Ordner nicht gefunden: {dir}\nMit --dir \"<Deepchart-Ordner>\" angeben.");
    return 1;
}

// Auflösung: alle DLLs des Deepchart-Ordners + .NET-Laufzeit (+ Windows Desktop, falls vorhanden).
var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
var paths = new List<string>();
paths.AddRange(Directory.GetFiles(dir, "*.dll"));
paths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
var desktopRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "Microsoft.WindowsDesktop.App"));
if (Directory.Exists(desktopRoot))
{
    var latest = Directory.GetDirectories(desktopRoot).OrderBy(d => d).LastOrDefault();
    if (latest != null) paths.AddRange(Directory.GetFiles(latest, "*.dll"));
}
// Doppelte Dateinamen: die Deepchart-Version gewinnt (steht vorne).
var unique = paths.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Select(g => g.First());

using var mlc = new MetadataLoadContext(new PathAssemblyResolver(unique));
var sb = new StringBuilder();
sb.AppendLine($"# Deepchart API – erzeugt {DateTime.Now:yyyy-MM-dd HH:mm} aus {dir}");
sb.AppendLine($"# Filter: {(all ? "alle Namespaces" : string.Join(", ", namespaces))}");

foreach (var file in files)
{
    var path = Path.Combine(dir, file);
    if (!File.Exists(path)) { sb.AppendLine($"\n## {file}: NICHT GEFUNDEN"); continue; }

    var asm = mlc.LoadFromAssemblyPath(path);
    sb.AppendLine($"\n## {asm.GetName().Name} {asm.GetName().Version}");

    Type[] types;
    try { types = asm.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

    foreach (var t in types
                 .Where(t => t.IsPublic || t.IsNestedPublic)
                 .Where(t => all || namespaces.Any(n => (t.Namespace ?? "").StartsWith(n, StringComparison.Ordinal)))
                 .OrderBy(t => t.Namespace).ThenBy(t => t.FullName))
        DumpType(t, sb);
}

File.WriteAllText(outPath, sb.ToString());
Console.WriteLine($"Geschrieben: {outPath} ({sb.Length / 1024} KB)");
return 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void DumpType(Type t, StringBuilder sb)
{
    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                               BindingFlags.Static | BindingFlags.DeclaredOnly;
    sb.AppendLine();
    var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" :
               t.IsAbstract && t.IsSealed ? "static class" : t.IsAbstract ? "abstract class" : "class";
    var header = $"{kind} {Safe(() => Name(t), t.Name)}";
    var bases = new List<string>();
    if (!t.IsEnum && !t.IsValueType && t.BaseType != null && t.BaseType.FullName != "System.Object")
        bases.Add(Safe(() => Name(t.BaseType), "?"));
    bases.AddRange(Safe(() => t.GetInterfaces().Select(Name).ToArray(), Array.Empty<string>()));
    if (bases.Count > 0) header += " : " + string.Join(", ", bases);
    sb.AppendLine(Attrs(() => t.GetCustomAttributesData()) + header);

    if (t.IsEnum)
    {
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            sb.AppendLine($"    {f.Name} = {Safe(() => f.GetRawConstantValue()?.ToString(), "?")}");
        return;
    }

    foreach (var c in Safe(() => t.GetConstructors(flags), Array.Empty<ConstructorInfo>()).Where(Visible))
        sb.AppendLine($"    {Access(c)}{t.Name}({Params(c)})");
    foreach (var f in Safe(() => t.GetFields(flags), Array.Empty<FieldInfo>()).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
        sb.AppendLine($"    {(f.IsPublic ? "public " : "protected ")}{(f.IsStatic ? "static " : "")}{Safe(() => Name(f.FieldType), "?")} {f.Name}");
    foreach (var p in Safe(() => t.GetProperties(flags), Array.Empty<PropertyInfo>()))
    {
        var get = p.GetMethod; var set = p.SetMethod;
        if (!(get != null && Visible(get)) && !(set != null && Visible(set))) continue;
        var acc = (get != null && Visible(get) ? "get; " : "") + (set != null && Visible(set) ? "set; " : "");
        var any = get ?? set!;
        sb.AppendLine($"    {Attrs(() => p.GetCustomAttributesData())}{Access(any)}{Modifiers(any)}{Safe(() => Name(p.PropertyType), "?")} {p.Name} {{ {acc}}}");
    }
    foreach (var e in Safe(() => t.GetEvents(flags), Array.Empty<EventInfo>()))
        if (e.AddMethod != null && Visible(e.AddMethod))
            sb.AppendLine($"    {Access(e.AddMethod)}event {Safe(() => Name(e.EventHandlerType!), "?")} {e.Name}");
    foreach (var m in Safe(() => t.GetMethods(flags), Array.Empty<MethodInfo>()).Where(m => !m.IsSpecialName && Visible(m)))
        sb.AppendLine($"    {Attrs(() => m.GetCustomAttributesData())}{Access(m)}{Modifiers(m)}{Safe(() => Name(m.ReturnType), "?")} {m.Name}{GenericArgs(m)}({Params(m)})");
    foreach (var n in Safe(() => t.GetNestedTypes(BindingFlags.Public), Array.Empty<Type>()))
        sb.AppendLine($"    nested {n.Name} (siehe eigener Eintrag)");
}

static bool Visible(MethodBase m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;
static string Access(MethodBase m) => m.IsPublic ? "public " : "protected ";
static string Modifiers(MethodBase m) =>
    m.IsStatic ? "static " : m.IsAbstract ? "abstract " :
    m.IsVirtual && !m.IsFinal ? ((m.Attributes & MethodAttributes.NewSlot) != 0 ? "virtual " : "override ") : "";
static string GenericArgs(MethodInfo m) =>
    m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";

static string Params(MethodBase m) => Safe(() => string.Join(", ", m.GetParameters().Select(p =>
    (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") +
    Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType) + " " + p.Name +
    (p.HasDefaultValue ? " = " + (Safe(() => p.RawDefaultValue?.ToString(), null) ?? "null") : ""))), "?");

static string Attrs(Func<IList<CustomAttributeData>> get)
{
    var names = Safe(() => get().Select(a => a.AttributeType.Name)
        .Where(n => n is not ("CompilerGeneratedAttribute" or "NullableAttribute" or "NullableContextAttribute"
                       or "IsReadOnlyAttribute" or "DebuggerHiddenAttribute" or "DebuggerStepThroughAttribute"
                       or "AsyncStateMachineAttribute" or "IteratorStateMachineAttribute"))
        .Select(n => n.EndsWith("Attribute") ? n[..^9] : n).ToArray(), Array.Empty<string>());
    return names.Length == 0 ? "" : "[" + string.Join(", ", names) + "] ";
}

static string Name(Type t)
{
    if (t.IsArray) return Name(t.GetElementType()!) + "[]";
    if (t.IsGenericParameter) return t.Name;
    var n = t.IsNested ? Name(t.DeclaringType!) + "." + t.Name : t.Name;
    if (t.IsGenericType)
    {
        n = n.Split('`')[0];
        n += "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
    }
    return n switch
    {
        "Int32" => "int", "Int64" => "long", "Double" => "double", "Single" => "float", "Boolean" => "bool",
        "String" => "string", "Void" => "void", "Object" => "object", "Decimal" => "decimal", "Byte" => "byte",
        _ => n
    };
}

static T Safe<T>(Func<T> f, T fallback)
{
    try { return f(); } catch { return fallback; }
}
