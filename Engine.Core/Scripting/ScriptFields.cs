using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine.Core.Scene;

namespace Engine.Core.Scripting;

public enum ScriptFieldKind { Float, Double, Int, Long, Bool, String, Vector3, Enum, Object }

// One public field of a script that the Inspector can edit. DefaultJson is the value the script's own code gives it.
// For arrays and lists, Kind is the element type.
// The text the Inspector shows for an enum value in a dropdown, instead of its name:
//     public enum Size { [Label("Small (fast)")] Small, [Label("Big (slow)")] Big }
[AttributeUsage(AttributeTargets.Field)]
public sealed class LabelAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

// For an enum field, Options are its values' names (what is saved) and OptionLabels what the Inspector shows: a value's
// [Label("...")] (or [Description("...")]) when it has one, otherwise its name.
// For an Object field, RequiredUi is set when the field is a UI control (Button, TextField...): only objects with a UI
// element of that kind can be dropped on it.
public sealed record ScriptFieldInfo(string Name, ScriptFieldKind Kind, bool IsArray, bool IsList, string DefaultJson)
{
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OptionLabels { get; init; } = Array.Empty<string>();
    public UiKind? RequiredUi { get; init; }

    public bool IsCollection => IsArray || IsList;
}

// Public script fields shown in the Inspector: float, double, int, long, bool, string, Vector3, enums (a dropdown),
// SceneObject (an object from the scene, dragged in from the Hierarchy) and the UI controls Button, TextField, Checkbox,
// Dropdown and Bar (a UI object of that kind, dragged in the same way), and arrays and List<T> of all of them but enums.
// An object or control field is kept as the object's id and becomes the object (or its control) when play starts (null
// if it's gone). Values are kept as JSON text in ScriptComponent.Fields and copied into the script (a Behaviour, or a
// component written in a script) when play starts. A Vector3 field whose name ends in "Color" is shown as a color.
public static class ScriptFields
{
    public static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,   // Infinity and NaN are allowed
        Converters = { new Vector3Converter(), new JsonStringEnumConverter() },   // enums are saved by name
    };

    // A Vector3 as [x, y, z].
    private sealed class Vector3Converter : JsonConverter<System.Numerics.Vector3>
    {
        public override System.Numerics.Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var values = JsonSerializer.Deserialize<float[]>(ref reader, options) ?? [];
            return values.Length >= 3 ? new System.Numerics.Vector3(values[0], values[1], values[2])
                : throw new JsonException("A Vector3 needs three numbers.");
        }

        public override void Write(Utf8JsonWriter writer, System.Numerics.Vector3 value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteEndArray();
        }
    }

    private static readonly Dictionary<Type, ScriptFieldKind> Kinds = new()
    {
        [typeof(float)] = ScriptFieldKind.Float,
        [typeof(double)] = ScriptFieldKind.Double,
        [typeof(int)] = ScriptFieldKind.Int,
        [typeof(long)] = ScriptFieldKind.Long,
        [typeof(bool)] = ScriptFieldKind.Bool,
        [typeof(string)] = ScriptFieldKind.String,
        [typeof(System.Numerics.Vector3)] = ScriptFieldKind.Vector3,
    };

    // The editable fields of a script class, in the order they are declared (base class fields first).
    // The script is created once to read the defaults its field initializers set.
    public static List<ScriptFieldInfo> Describe(Type script)
    {
        object? instance = null;
        try { instance = Activator.CreateInstance(script); }
        catch { /* a script whose constructor throws still lists its fields, with empty defaults */ }

        var result = new List<ScriptFieldInfo>();
        foreach (var field in Editable(script))
        {
            var (kind, isArray, isList) = Classify(field.FieldType)!.Value;
            object? value = instance is null ? null : field.GetValue(instance);
            value ??= EmptyValue(field.FieldType);
            // Objects can't be given in code (they come from the scene), so an object field or list starts empty.
            string defaultJson = kind == ScriptFieldKind.Object ? (isArray || isList ? "[]" : "null")
                : JsonSerializer.Serialize(value, field.FieldType, Json);
            var info = new ScriptFieldInfo(field.Name, kind, isArray, isList, defaultJson);
            if (kind == ScriptFieldKind.Object && ElementType(field.FieldType) is { } target && UiControl.IsControlType(target))
                info = info with { RequiredUi = UiControl.KindOf(target) };
            if (kind == ScriptFieldKind.Enum)
            {
                var members = field.FieldType.GetFields(BindingFlags.Public | BindingFlags.Static);
                info = info with
                {
                    Options = members.Select(m => m.Name).ToList(),
                    OptionLabels = members.Select(m => m.GetCustomAttribute<LabelAttribute>()?.Text
                        ?? m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? m.Name).ToList(),
                };
            }
            result.Add(info);
        }
        return result;
    }

    // Copies stored values into a new script instance. Returns a note for each value that no longer fits
    // (for example a field whose type changed); those fields keep the script's own default.
    // findObject turns a saved object id into the object, for SceneObject fields; without it they stay null.
    public static string Apply(object script, IReadOnlyDictionary<string, string> values, Func<Guid, SceneObject?>? findObject = null)
    {
        if (values.Count == 0) return "";
        var problems = new StringBuilder();
        var fields = Editable(script.GetType()).ToDictionary(f => f.Name);
        foreach (var (name, json) in values)
        {
            if (!fields.TryGetValue(name, out var field)) continue;   // the field was removed from the script
            string where = $"{script.GetType().Name}.{name}";
            if (IsReferenceList(field.FieldType))
            {
                var element = ElementType(field.FieldType)!;
                var found = ObjectIds(json).Select(id => Resolve(element, id, findObject, where, problems)).ToList();
                var list = field.FieldType.IsArray
                    ? (System.Collections.IList)Array.CreateInstance(element, found.Count)
                    : (System.Collections.IList)Activator.CreateInstance(field.FieldType)!;
                for (int i = 0; i < found.Count; i++)
                    if (field.FieldType.IsArray) list[i] = found[i];
                    else list.Add(found[i]);
                field.SetValue(script, list);
                continue;
            }
            if (IsReference(field.FieldType))
            {
                field.SetValue(script, Resolve(field.FieldType, ObjectId(json), findObject, where, problems));
                continue;
            }
            try
            {
                field.SetValue(script, JsonSerializer.Deserialize(json, field.FieldType, Json) ?? EmptyValue(field.FieldType));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                problems.AppendLine($"{script.GetType().Name}.{name}: the saved value {json} does not fit a " +
                                    $"{field.FieldType.Name}, so the script's default is used.");
            }
        }
        return problems.ToString();
    }

    // The script's current public field values, as JSON text for ScriptComponent.Fields. Save games use it, so a
    // loaded game gets back what the script held when it was saved.
    public static Dictionary<string, string> Capture(object script)
    {
        var values = new Dictionary<string, string>();
        foreach (var field in Editable(script.GetType()))
        {
            if (IsReference(field.FieldType))
            {
                values[field.Name] = ObjectJson(ObjectOf(field.GetValue(script)));
                continue;
            }
            if (IsReferenceList(field.FieldType))
            {
                var items = field.GetValue(script) as System.Collections.IEnumerable ?? Array.Empty<object>();
                values[field.Name] = "[" + string.Join(",", items.Cast<object?>().Select(item => ObjectJson(ObjectOf(item)))) + "]";
                continue;
            }
            try { values[field.Name] = JsonSerializer.Serialize(field.GetValue(script), field.FieldType, Json); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { /* left out; the script keeps its default */ }
        }
        return values;
    }

    // How a SceneObject field is saved: the object's id as a JSON string, or null.
    public static string ObjectJson(SceneObject? obj) => obj is null ? "null" : JsonSerializer.Serialize(obj.Id.ToString());

    // Types set by dragging an object from the Hierarchy: SceneObject itself, and the UI controls.
    private static bool IsReference(Type type) => type == typeof(SceneObject) || UiControl.IsControlType(type);

    private static bool IsReferenceList(Type type) => ElementType(type) is { } element && element != type && IsReference(element);

    // A field's own type, or for an array or List<T> the type of its entries.
    private static Type? ElementType(Type type) =>
        type.IsArray && type.GetArrayRank() == 1 ? type.GetElementType() :
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] :
        type;

    // The object a reference field's value stands for.
    private static SceneObject? ObjectOf(object? value) => value switch
    {
        SceneObject obj => obj,
        UiControl control => control.Object,
        _ => null,
    };

    // A saved id turned into what the field holds: the object, or its control of the field's type. Null (with a note)
    // when the object is gone or has no UI element of the right kind.
    private static object? Resolve(Type type, Guid? id, Func<Guid, SceneObject?>? findObject, string where, StringBuilder problems)
    {
        if (id is not { } guid || findObject is null) return null;
        var obj = findObject(guid);
        if (obj is null)
        {
            problems.AppendLine($"{where}: an object it pointed to is no longer in the scene, so it is null.");
            return null;
        }
        if (type == typeof(SceneObject)) return obj;
        var control = UiControl.From(type, obj);
        if (control is null)
            problems.AppendLine($"{where}: '{obj.Name}' has no UI {UiControl.KindOf(type)}, so it is null.");
        return control;
    }

    // The ids in a saved object list (null for an empty entry).
    public static List<Guid?> ObjectIds(string? json)
    {
        var ids = new List<Guid?>();
        try
        {
            if (!string.IsNullOrWhiteSpace(json) && System.Text.Json.Nodes.JsonNode.Parse(json) is System.Text.Json.Nodes.JsonArray array)
                foreach (var node in array)
                    ids.Add(ObjectId(node?.ToJsonString()));
        }
        catch (JsonException) { }
        return ids;
    }

    // The object id saved in a SceneObject field's JSON, or null for none (or text that isn't an id).
    public static Guid? ObjectId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return null;
        try
        {
            return JsonSerializer.Deserialize<string>(json) is { } text && Guid.TryParse(text, out var id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<FieldInfo> Editable(Type script)
    {
        // Walk from the base class down so inherited fields come first, as they are declared.
        var chain = new Stack<Type>();
        for (var t = script; t is not null && t != typeof(Behaviour) && t != typeof(Component) && t != typeof(object); t = t.BaseType)
            chain.Push(t);
        foreach (var type in chain)
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(f => f.MetadataToken))
                if (!field.IsInitOnly && !field.IsLiteral && Classify(field.FieldType) is not null)
                    yield return field;
    }

    private static (ScriptFieldKind Kind, bool IsArray, bool IsList)? Classify(Type type)
    {
        if (IsReference(type)) return (ScriptFieldKind.Object, false, false);
        if (Kinds.TryGetValue(type, out var kind)) return (kind, false, false);
        if (type.IsArray && type.GetArrayRank() == 1 && IsReference(type.GetElementType()!))
            return (ScriptFieldKind.Object, true, false);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && IsReference(type.GetGenericArguments()[0]))
            return (ScriptFieldKind.Object, false, true);
        if (type.IsEnum) return (ScriptFieldKind.Enum, false, false);
        if (type.IsArray && type.GetArrayRank() == 1 && type.GetElementType() is { } element && Kinds.TryGetValue(element, out kind))
            return (kind, true, false);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) &&
            Kinds.TryGetValue(type.GetGenericArguments()[0], out kind))
            return (kind, false, true);
        return null;
    }

    private static object? EmptyValue(Type type) =>
        type == typeof(string) ? "" :
        type.IsArray ? Array.CreateInstance(type.GetElementType()!, 0) :
        type.IsGenericType ? Activator.CreateInstance(type) :
        null;
}
