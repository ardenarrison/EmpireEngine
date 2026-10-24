using VisualCode.Model;
using VisualCode.Transpiler;

namespace VisualCode.Blocks;

// The built-in block set. Each block states its editor label, its inputs, and its C# output.
internal static class StandardBlocks
{
    private static readonly string[] KeyNames = BuildKeyNames();
    private static readonly string[] ButtonNames = { "Left", "Right", "Middle" };
    private static readonly string[] AxisNames = { "X", "Y", "Z" };

    private static string[] BuildKeyNames()
    {
        var keys = new List<string>();
        for (char c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        for (char c = '0'; c <= '9'; c++) keys.Add(c.ToString());
        keys.AddRange(new[]
        {
            "Space", "Enter", "Escape", "Tab", "Backspace", "Shift", "Control", "Alt",
            "Up", "Down", "Left", "Right",
        });
        return keys.ToArray();
    }

    private static InputSpec Num(string name, string def = "0") => new(name, InputKind.Number, def);
    private static InputSpec Any(string name, string def = "0") => new(name, InputKind.Any, def);
    private static InputSpec Bool(string name, string def = "false") => new(name, InputKind.Bool, def);
    private static InputSpec Key(string name = "key") => new(name, InputKind.Key, "W", KeyNames);
    private static InputSpec Button(string name = "button") => new(name, InputKind.Choice, "Left", ButtonNames);
    private static InputSpec Axis(string name = "axis") => new(name, InputKind.Choice, "X", AxisNames);
    private static InputSpec Var(string name = "var") => new(name, InputKind.Variable, "");

    private static BlockSpec Hat(string type, string label, Func<VcNode, Emit, List<string>, List<string>> wrap, params InputSpec[] inputs) => new()
    {
        Type = type, Category = "Events", Label = label, Shape = BlockShape.Hat, Inputs = inputs, Wrap = wrap,
    };

    private static BlockSpec Statement(string type, string category, string label, BlockShape shape,
        Func<VcNode, Emit, List<string>> gen, params InputSpec[] inputs) => new()
    {
        Type = type, Category = category, Label = label, Shape = shape, Inputs = inputs, Stmt = gen,
    };

    private static BlockSpec Reporter(string type, string category, string label, string returns,
        Func<VcNode, Emit, string> gen, params InputSpec[] inputs) => new()
    {
        Type = type, Category = category, Label = label, Shape = BlockShape.Reporter,
        Inputs = inputs, Returns = returns, Expr = gen,
    };

    private static string Raw(VcNode n, string input, string fallback) =>
        n.Inputs.TryGetValue(input, out var v) && !string.IsNullOrEmpty(v.Literal) ? v.Literal : fallback;

    private static string Vec3(VcNode n, Emit e) =>
        $"new Vector3({e.Value(n, "x")}, {e.Value(n, "y")}, {e.Value(n, "z")})";

    private static string AxisVector(string axis, string value) => axis switch
    {
        "X" => $"new Vector3({value}, 0f, 0f)",
        "Z" => $"new Vector3(0f, 0f, {value})",
        _ => $"new Vector3(0f, {value}, 0f)",
    };

    private static string SetAxis(string axis, string value) => axis switch
    {
        "X" => $"Position = new Vector3({value}, Position.Y, Position.Z);",
        "Z" => $"Position = new Vector3(Position.X, Position.Y, {value});",
        _ => $"Position = new Vector3(Position.X, {value}, Position.Z);",
    };

    private static List<string> One(string line) => Code.One(line);

    public static List<BlockSpec> Create() => new()
    {
        // ---------- Events ----------
        Hat("when_start", "when scene starts",
            (n, e, body) => body.Append("yield break;").ToList()),

        Hat("when_every_frame", "every frame",
            (n, e, body) => Code.Loop(body)),

        Hat("when_key_pressed", "when {key} pressed",
            (n, e, body) => Code.Loop(Code.Block($"if (Input.GetKeyDown({e.Value(n, "key")}))", body)),
            Key()),

        Hat("when_key_held", "while {key} held",
            (n, e, body) => Code.Loop(Code.Block($"if (Input.GetKey({e.Value(n, "key")}))", body)),
            Key()),

        Hat("when_mouse_pressed", "when {button} mouse pressed",
            (n, e, body) => Code.Loop(Code.Block($"if (Input.GetMouseButtonDown({e.Value(n, "button")}))", body)),
            Button()),

        // ---------- Motion (3D) ----------
        Statement("move_forward", "Motion", "move {distance} units forward", BlockShape.Stack,
            (n, e) => One($"MoveForward({e.Value(n, "distance")});"),
            Num("distance", "1")),

        Statement("go_to", "Motion", "go to x: {x} y: {y} z: {z}", BlockShape.Stack,
            (n, e) => One($"Position = {Vec3(n, e)};"),
            Num("x"), Num("y"), Num("z")),

        Statement("glide_to", "Motion", "glide to x: {x} y: {y} z: {z} in {seconds} s", BlockShape.Stack,
            (n, e) => One($"yield return GlideTo({Vec3(n, e)}, {e.Value(n, "seconds")});"),
            Num("x"), Num("y"), Num("z"), Num("seconds", "1")),

        Statement("change_axis", "Motion", "change {axis} by {value}", BlockShape.Stack,
            (n, e) => One($"Position += {AxisVector(Raw(n, "axis", "X"), e.Value(n, "value"))};"),
            Axis(), Num("value", "1")),

        Statement("set_axis", "Motion", "set {axis} to {value}", BlockShape.Stack,
            (n, e) => One(SetAxis(Raw(n, "axis", "X"), e.Value(n, "value"))),
            Axis(), Num("value")),

        Statement("turn", "Motion", "turn {axis} by {degrees} degrees", BlockShape.Stack,
            (n, e) => One($"Turn({AxisVector(Raw(n, "axis", "Y"), "1f")}, {e.Value(n, "degrees")});"),
            Axis("axis"), Num("degrees", "15")),

        Reporter("x_position", "Motion", "x position", "number", (n, e) => "Position.X"),
        Reporter("y_position", "Motion", "y position", "number", (n, e) => "Position.Y"),
        Reporter("z_position", "Motion", "z position", "number", (n, e) => "Position.Z"),

        // ---------- Control ----------
        Statement("wait", "Control", "wait {seconds} seconds", BlockShape.Stack,
            (n, e) => One($"yield return WaitSeconds({e.Value(n, "seconds")});"),
            Num("seconds", "1")),

        Statement("wait_frame", "Control", "wait one frame", BlockShape.Stack,
            (n, e) => One("yield return null;")),

        Statement("wait_until", "Control", "wait until {condition}", BlockShape.Stack,
            (n, e) => One($"yield return WaitUntil(() => {e.Value(n, "condition")});"),
            Bool("condition")),

        Statement("repeat", "Control", "repeat {times} times", BlockShape.CBlock,
            (n, e) =>
            {
                var i = e.Unique("loop");
                return Code.Block($"for (int {i} = 0; {i} < (int)({e.Value(n, "times")}); {i}++)", e.Chain(n.Body));
            },
            Num("times", "10")),

        Statement("forever", "Control", "forever", BlockShape.CBlock,
            (n, e) => Code.Loop(e.Chain(n.Body))),

        Statement("repeat_until", "Control", "repeat until {condition}", BlockShape.CBlock,
            (n, e) => Code.Block($"while (!({e.Value(n, "condition")}))", e.Chain(n.Body).Append("yield return null;")),
            Bool("condition")),

        Statement("if", "Control", "if {condition} then", BlockShape.CBlock,
            (n, e) => Code.Block($"if ({e.Value(n, "condition")})", e.Chain(n.Body)),
            Bool("condition")),

        Statement("if_else", "Control", "if {condition} then", BlockShape.IfElse,
            (n, e) => Code.Block($"if ({e.Value(n, "condition")})", e.Chain(n.Body))
                .Concat(Code.Block("else", e.Chain(n.ElseBody)))
                .ToList(),
            Bool("condition")),

        Statement("stop_script", "Control", "stop this script", BlockShape.Stack,
            (n, e) => One("yield break;")),

        // ---------- Sensing ----------
        Reporter("key_pressed", "Sensing", "key {key} is pressed?", "bool",
            (n, e) => $"Input.GetKeyDown({e.Value(n, "key")})", Key()),

        Reporter("key_held", "Sensing", "key {key} held?", "bool",
            (n, e) => $"Input.GetKey({e.Value(n, "key")})", Key()),

        Reporter("mouse_pressed", "Sensing", "{button} mouse pressed?", "bool",
            (n, e) => $"MouseDown({e.Value(n, "button")})", Button()),

        Reporter("distance_to", "Sensing", "distance to x: {x} y: {y} z: {z}", "number",
            (n, e) => $"Vector3.Distance(Position, {Vec3(n, e)})",
            Num("x"), Num("y"), Num("z")),

        Reporter("timer", "Sensing", "timer", "number", (n, e) => "Timer"),
        Reporter("delta_time", "Sensing", "delta time", "number", (n, e) => "Time.deltaTime"),
        Reporter("fps", "Sensing", "fps", "number", (n, e) => "Time.fps"),

        Statement("reset_timer", "Sensing", "reset timer", BlockShape.Stack,
            (n, e) => One("ResetTimer();")),

        // ---------- Variables ----------
        Statement("set_var", "Variables", "set {var} to {value}", BlockShape.Stack,
            (n, e) => One($"{e.Value(n, "var")} = {e.Value(n, "value")};"),
            Var(), Any("value")),

        Statement("change_var", "Variables", "change {var} by {value}", BlockShape.Stack,
            (n, e) => One($"{e.Value(n, "var")} += {e.Value(n, "value")};"),
            Var(), Num("value", "1")),

        Reporter("variable", "Variables", "{var}", "any", (n, e) => e.Value(n, "var"), Var()),

        // ---------- Operators ----------
        Reporter("add", "Operators", "{a} + {b}", "number",
            (n, e) => $"({e.Value(n, "a")} + {e.Value(n, "b")})", Num("a"), Num("b")),

        Reporter("subtract", "Operators", "{a} - {b}", "number",
            (n, e) => $"({e.Value(n, "a")} - {e.Value(n, "b")})", Num("a"), Num("b")),

        Reporter("multiply", "Operators", "{a} * {b}", "number",
            (n, e) => $"({e.Value(n, "a")} * {e.Value(n, "b")})", Num("a", "1"), Num("b", "1")),

        Reporter("divide", "Operators", "{a} / {b}", "number",
            (n, e) => $"SafeDivide({e.Value(n, "a")}, {e.Value(n, "b")})", Num("a", "1"), Num("b", "1")),

        Reporter("mod", "Operators", "{a} mod {b}", "number",
            (n, e) => $"({e.Value(n, "a")} % {e.Value(n, "b")})", Num("a"), Num("b", "2")),

        Reporter("greater", "Operators", "{a} > {b}", "bool",
            (n, e) => $"({e.Value(n, "a")} > {e.Value(n, "b")})", Num("a"), Num("b")),

        Reporter("less", "Operators", "{a} < {b}", "bool",
            (n, e) => $"({e.Value(n, "a")} < {e.Value(n, "b")})", Num("a"), Num("b")),

        Reporter("equals", "Operators", "{a} = {b}", "bool",
            (n, e) => $"Approximately({e.Value(n, "a")}, {e.Value(n, "b")})", Num("a"), Num("b")),

        Reporter("and", "Operators", "{a} and {b}", "bool",
            (n, e) => $"({e.Value(n, "a")} && {e.Value(n, "b")})", Bool("a"), Bool("b")),

        Reporter("or", "Operators", "{a} or {b}", "bool",
            (n, e) => $"({e.Value(n, "a")} || {e.Value(n, "b")})", Bool("a"), Bool("b")),

        Reporter("not", "Operators", "not {a}", "bool",
            (n, e) => $"!({e.Value(n, "a")})", Bool("a")),

        Reporter("random", "Operators", "pick random {from} to {to}", "number",
            (n, e) => $"RandomRange({e.Value(n, "from")}, {e.Value(n, "to")})", Num("from", "0"), Num("to", "10")),
    };
}
