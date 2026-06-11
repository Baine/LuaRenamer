using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace LuaDefsGenerator;

[Generator]
public class DefsGenerator : IIncrementalGenerator
{
    private static readonly HashSet<string> s_group0 = new HashSet<string>(StringComparer.Ordinal)
    {
        "Japanese", "Romaji", "English", "Chinese", "Pinyin", "Korean", "KoreanTranscription"
    };

    private static readonly HashSet<string> s_group3 = new HashSet<string>(StringComparer.Ordinal)
    {
        "Unknown", "Main", "None"
    };

    private static readonly HashSet<string> s_anidbLangs = new HashSet<string>(StringComparer.Ordinal)
    {
        "Japanese", "Romaji", "English", "Chinese", "ChineseSimplified", "ChineseTraditional",
        "Pinyin", "Korean", "KoreanTranscription",
        "Afrikaans", "Albanian", "Arabic", "Bengali", "Bosnian", "Bulgarian", "MyanmarBurmese",
        "Croatian", "Czech", "Danish", "Dutch", "Esperanto", "Estonian", "Filipino", "Finnish",
        "French", "Georgian", "German", "Greek", "HaitianCreole", "Hebrew", "Hindi", "Hungarian",
        "Icelandic", "Indonesian", "Italian", "Javanese", "Latin", "Latvian", "Lithuanian",
        "Malaysian", "Mongolian", "Nepali", "Norwegian", "Persian", "Polish", "Portuguese",
        "BrazilianPortuguese", "Romanian", "Russian", "Serbian", "Sinhala", "Slovak", "Slovenian",
        "Spanish", "Basque", "Catalan", "Galician", "Swedish", "Tamil", "Tatar", "Telugu",
        "Thai", "ThaiTranscription", "Turkish", "Ukrainian", "Urdu", "Vietnamese"
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var projectDir = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) =>
            {
                provider.GlobalOptions.TryGetValue("build_property.projectdir", out var dir);
                return dir;
            });

        var payload = context.CompilationProvider
            .Select(static (compilation, _) => BuildAll(compilation));

        context.RegisterSourceOutput(projectDir.Combine(payload), static (_, combined) =>
        {
            var (dir, files) = combined;
            if (string.IsNullOrEmpty(dir) || files == null) return;
            var luaDir = Path.Combine(dir!, "lua");
#pragma warning disable RS1035
            WriteIfChanged(Path.Combine(luaDir, "defs.lua"), files.Defs);
            WriteIfChanged(Path.Combine(luaDir, "enums.lua"), files.Enums);
            WriteIfChanged(Path.Combine(luaDir, "env.lua"), files.Env);
#pragma warning restore RS1035
        });
    }

#pragma warning disable RS1035
    private static void WriteIfChanged(string path, string content)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != content)
            File.WriteAllText(path, content);
    }
#pragma warning restore RS1035

    private sealed class GeneratedFiles
    {
        public string Defs { get; }
        public string Enums { get; }
        public string Env { get; }
        public GeneratedFiles(string defs, string enums, string env) { Defs = defs; Enums = enums; Env = env; }
    }

    private static GeneratedFiles? BuildAll(Compilation compilation)
    {
        var ns = GetNamespace(compilation.GlobalNamespace, new[] { "LuaRenamer", "LuaEnv" });
        if (ns == null) return null;
        var envType = ns.GetTypeMembers("EnvTable").FirstOrDefault();
        if (envType == null) return null;
        var enumToLuaName = BuildEnumMap(envType);
        return new GeneratedFiles(
            BuildDefs(ns, enumToLuaName),
            BuildEnums(envType),
            BuildEnv(envType, enumToLuaName));
    }

    // Maps fully-qualified enum type name -> Lua property name (e.g. "...TitleLanguage" -> "Language").
    private static Dictionary<string, string> BuildEnumMap(INamedTypeSymbol envType)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in envType.GetMembers())
        {
            if (member is IPropertySymbol prop &&
                GetAttr(prop, "LuaFieldAttribute") != null &&
                prop.Type is INamedTypeSymbol { Name: "LuaEnumRef", TypeArguments: { Length: 1 } } enumRef)
            {
                result[enumRef.TypeArguments[0].ToDisplayString()] = prop.Name;
            }
        }
        return result;
    }

    private static string BuildDefs(INamespaceSymbol ns, Dictionary<string, string> enumToLuaName)
    {
        var types = ns.GetTypeMembers()
            .Where(t => t.TypeKind == TypeKind.Class && t.Name != "EnvTable" && InheritsFromLuaTableWriter(t))
            .OrderBy(t => StripTable(t.Name), StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("---@meta\n\n");

        foreach (var type in types)
        {
            var className = StripTable(type.Name);
            var functions = new List<IPropertySymbol>();
            sb.Append($"---@class (exact) {className}\n");

            foreach (var member in type.GetMembers())
            {
                if (member is not IPropertySymbol prop || prop.IsStatic) continue;
                if (GetAttr(prop, "LuaFieldAttribute") is not { } fieldAttr) continue;

                if (prop.Type is INamedTypeSymbol { Name: "LuaFunctionRef", TypeArguments: { Length: 1 } })
                {
                    functions.Add(prop);
                }
                else
                {
                    var luaType = InferLuaType(prop, enumToLuaName);
                    var desc = GetAttrDescription(fieldAttr);
                    sb.Append($"---@field {prop.Name} {luaType}{(desc != null ? $" # {desc}" : "")}\n");
                }
            }

            sb.Append($"local {className} = {{}}\n\n");

            foreach (var funcProp in functions)
                GenerateFunctionAnnotations(sb, funcProp, $"{className}:{funcProp.Name}", enumToLuaName);
        }

        sb.Length--;
        return sb.ToString();
    }

    private static string BuildEnums(INamedTypeSymbol envType)
    {
        var sb = new StringBuilder();
        sb.Append("---@meta\n\n");

        foreach (var member in envType.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (GetAttr(prop, "LuaFieldAttribute") == null) continue;
            if (prop.Type is not INamedTypeSymbol { Name: "LuaEnumRef", TypeArguments: { Length: 1 } } enumRef) continue;

            var enumType = (INamedTypeSymbol)enumRef.TypeArguments[0];
            var propName = prop.Name;

            sb.Append($"---@enum {propName}\n");
            sb.Append($"{propName} = {{\n");

            if (enumType.Name == "TitleLanguage")
                AppendTitleLanguageMembers(sb, enumType);
            else
                AppendEnumMembersSorted(sb, enumType);

            sb.Append("}\n\n");
        }

        sb.Length--;
        return sb.ToString();
    }

    private static void AppendEnumMembersSorted(StringBuilder sb, INamedTypeSymbol enumType)
    {
        foreach (var name in GetEnumNamesSortedByValue(enumType))
            sb.Append($"    {name} = \"{name}\",\n");
    }

    private static void AppendTitleLanguageMembers(StringBuilder sb, INamedTypeSymbol enumType)
    {
        var allNames = GetEnumNamesSortedByValue(enumType).ToList();

        var group0 = allNames.Where(n => s_group0.Contains(n)).ToList();
        var group1 = allNames.Where(n => s_anidbLangs.Contains(n) && !s_group0.Contains(n))
                             .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var group2 = allNames.Where(n => !s_anidbLangs.Contains(n) && !s_group3.Contains(n))
                             .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var group3 = allNames.Where(n => s_group3.Contains(n)).ToList();

        sb.Append("\n--#region AniDB Languages\n");
        foreach (var n in group0) sb.Append($"    {n} = \"{n}\",\n");
        sb.Append('\n');
        foreach (var n in group1) sb.Append($"    {n} = \"{n}\",\n");
        sb.Append("--#endregion\n");
        sb.Append("\n--#region Other Languages\n");
        foreach (var n in group2) sb.Append($"    {n} = \"{n}\",\n");
        sb.Append("--#endregion\n\n");
        foreach (var n in group3) sb.Append($"    {n} = \"{n}\",\n");
    }

    // Returns enum member names sorted by unsigned binary value (matching Enum.GetValues() order),
    // deduped per value keeping first occurrence.
    private static IEnumerable<string> GetEnumNamesSortedByValue(INamedTypeSymbol enumType)
    {
        var seen = new HashSet<ulong>();
        return enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue)
            .Select(f => (f.Name, UBits: GetUnsignedBits(f.ConstantValue)))
            .OrderBy(f => f.UBits)
            .Where(f => seen.Add(f.UBits))
            .Select(f => f.Name);
    }

    // Interprets the enum constant as unsigned bits, matching Enum.GetValues() binary ordering.
    private static ulong GetUnsignedBits(object? value) => value switch
    {
        int i => (ulong)(uint)i,
        uint u => (ulong)u,
        long l => (ulong)l,
        ulong ul => ul,
        short s => (ulong)(ushort)s,
        ushort us => (ulong)us,
        byte b => (ulong)b,
        sbyte sb => (ulong)(byte)sb,
        _ => Convert.ToUInt64(value)
    };

    private static string BuildEnv(INamedTypeSymbol envType, Dictionary<string, string> enumToLuaName)
    {
        var sb = new StringBuilder();
        sb.Append("---@meta\n\n");

        foreach (var member in envType.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.Type is INamedTypeSymbol { Name: "LuaEnumRef" }) continue;
            if (GetAttr(prop, "LuaFieldAttribute") is not { } fieldAttr) continue;

            if (prop.Type is INamedTypeSymbol { Name: "LuaFunctionRef", TypeArguments: { Length: 1 } })
            {
                GenerateFunctionAnnotations(sb, prop, prop.Name, enumToLuaName);
            }
            else
            {
                var luaType = InferLuaType(prop, enumToLuaName);
                var desc = GetAttrDescription(fieldAttr);
                if (desc != null) sb.Append($"---{desc}\n");
                sb.Append($"---@type {luaType}\n");
                var defaultValue = GetAttrDefaultValue(fieldAttr) ?? "nil";
                sb.Append($"{prop.Name} = {defaultValue}\n\n");
            }
        }

        sb.Length--;
        return sb.ToString();
    }

    private static void GenerateFunctionAnnotations(StringBuilder sb, IPropertySymbol prop, string functionName,
        Dictionary<string, string> enumToLuaName)
    {
        var fieldAttr = GetAttr(prop, "LuaFieldAttribute")!;
        var desc = GetAttrDescription(fieldAttr);
        if (desc != null) sb.Append($"---{desc}\n");

        var delegateType = (INamedTypeSymbol)((INamedTypeSymbol)prop.Type).TypeArguments[0];
        var invoke = delegateType.DelegateInvokeMethod!;
        var parameters = invoke.Parameters;

        foreach (var param in parameters)
        {
            var luaType = InferLuaTypeForArg(param, enumToLuaName);
            var paramDesc = GetAttr(param, "DescriptionAttribute") is { } descAttr
                ? descAttr.ConstructorArguments.Length > 0 ? descAttr.ConstructorArguments[0].Value as string : null
                : null;
            sb.Append($"---@param {param.Name} {luaType}{(paramDesc != null ? $" # {paramDesc}" : "")}\n");
        }

        var retType = invoke.ReturnType;
        if (retType.SpecialType == SpecialType.System_Void)
        {
            sb.Append("---@return nil\n");
        }
        else
        {
            var retLuaType = InferLuaTypeForReturn(invoke, enumToLuaName);
            sb.Append($"---@return {retLuaType}\n");
        }

        var paramNames = string.Join(", ", parameters.Select(p => p.Name));
        sb.Append($"function {functionName}({paramNames}) end\n\n");
    }

    private static string InferLuaType(IPropertySymbol prop, Dictionary<string, string> enumToLuaName)
    {
        var t = prop.Type;

        // Nullable<T> struct wrapper
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullSym)
            return InferLuaTypeForInner(nullSym.TypeArguments[0], enumToLuaName) + "|nil";

        // Nullable reference type
        var isNullable = t.IsReferenceType && prop.NullableAnnotation == NullableAnnotation.Annotated;
        return InferLuaTypeForInner(t, enumToLuaName) + (isNullable ? "|nil" : "");
    }

    private static string InferLuaTypeForArg(IParameterSymbol param, Dictionary<string, string> enumToLuaName)
    {
        var t = param.Type;

        // Nullable<T> struct wrapper (bool?, long?, enum?)
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullSym)
            return InferLuaTypeForInner(nullSym.TypeArguments[0], enumToLuaName) + "|nil";

        // Nullable reference type (string?)
        var isNullable = param.NullableAnnotation == NullableAnnotation.Annotated;
        return InferLuaTypeForInner(t, enumToLuaName) + (isNullable ? "|nil" : "");
    }

    private static string InferLuaTypeForReturn(IMethodSymbol invoke, Dictionary<string, string> enumToLuaName)
    {
        var t = invoke.ReturnType;

        // Nullable<T> struct wrapper
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullSym)
            return InferLuaTypeForInner(nullSym.TypeArguments[0], enumToLuaName) + "|nil";

        // Nullable reference type (string?)
        var isNullable = invoke.ReturnNullableAnnotation == NullableAnnotation.Annotated;
        return InferLuaTypeForInner(t, enumToLuaName) + (isNullable ? "|nil" : "");
    }

    private static string InferLuaTypeForInner(ITypeSymbol t, Dictionary<string, string> enumToLuaName)
    {
        switch (t.SpecialType)
        {
            case SpecialType.System_Int64: return "integer";
            case SpecialType.System_Double: return "number";
            case SpecialType.System_Boolean: return "boolean";
            case SpecialType.System_String: return "string";
        }

        if (t.TypeKind == TypeKind.Enum)
            return enumToLuaName.TryGetValue(t.ToDisplayString(), out var luaName) ? luaName : t.Name;

        if (t is not INamedTypeSymbol named || named.TypeArguments.IsEmpty)
            return "table";

        var args = named.TypeArguments;
        switch (named.Name)
        {
            case "LuaRef" when args.Length == 1:
                return StripTable(args[0].Name);
            case "LuaArray" when args.Length == 1:
                return InferLuaTypeForInner(args[0], enumToLuaName) + "[]";
            case "LuaMap" when args.Length == 2:
                return $"table<{InferLuaTypeForInner(args[0], enumToLuaName)}, {InferLuaTypeForInner(args[1], enumToLuaName)}>";
            case "LuaUnion" when args.Length == 2:
                return InferLuaTypeForInner(args[0], enumToLuaName) + "|" + InferLuaTypeForInner(args[1], enumToLuaName);
            default:
                return "table";
        }
    }

    private static string? GetAttrDescription(AttributeData attr) =>
        attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string : null;

    private static string? GetAttrDefaultValue(AttributeData attr) =>
        attr.NamedArguments.FirstOrDefault(a => a.Key == "DefaultValue").Value.Value as string;

    private static AttributeData? GetAttr(ISymbol s, string attrName) =>
        s.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == attrName);

    private static string StripTable(string name) =>
        name.EndsWith("Table") ? name.Substring(0, name.Length - 5) : name;

    private static bool InheritsFromLuaTableWriter(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current != null)
        {
            if (current.Name == "LuaTableWriter") return true;
            current = current.BaseType;
        }
        return false;
    }

    private static INamespaceSymbol? GetNamespace(INamespaceSymbol root, string[] parts)
    {
        var current = root;
        foreach (var part in parts)
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part)!;
            if (current == null) return null;
        }
        return current;
    }
}
