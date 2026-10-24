namespace VisualCode.Blocks;

public sealed class BlockRegistry
{
    private readonly Dictionary<string, BlockSpec> _byType;

    public BlockRegistry(IEnumerable<BlockSpec> specs)
    {
        Specs = specs.ToList();
        _byType = Specs.ToDictionary(s => s.Type);
    }

    public IReadOnlyList<BlockSpec> Specs { get; }

    public BlockSpec? Get(string type) => _byType.TryGetValue(type, out var spec) ? spec : null;

    public static BlockRegistry Default { get; } = new(StandardBlocks.Create());
}
