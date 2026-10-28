namespace FactorioMCP.Services;

/// <summary>
/// Inventory names for the tools' inventoryType parameters. Factorio 2.1 merged the furnace_* and
/// assembling_machine_* inventories into crafter_*; the old names, which agents know, still work as aliases.
/// A name is a key of <c>defines.inventory</c>.
/// </summary>
internal static class InventoryNames
{
    internal static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["furnace_source"] = "crafter_input",
        ["furnace_result"] = "crafter_output",
        ["furnace_modules"] = "crafter_modules",
        ["assembling_machine_input"] = "crafter_input",
        ["assembling_machine_output"] = "crafter_output",
        ["assembling_machine_modules"] = "crafter_modules",
    };

    /// <summary>The common names, for tool descriptions and error hints.</summary>
    internal const string Common =
        "fuel, chest, crafter_input (furnace/assembler input), crafter_output, crafter_modules, lab_input, burnt_result";

    /// <summary>Message of an <c>invalid_inventoryType</c> error.</summary>
    internal const string ValidTypes = "valid types: " + Common;

    /// <summary>
    /// The <c>defines.inventory</c> key for a tool's inventory name: an old name becomes its 2.1 name.
    /// Throws <see cref="ArgumentException"/> for names that cannot be a key (only a-z and _ are allowed).
    /// </summary>
    internal static string Resolve(string name, string parameterName = "inventoryType")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        var key = name.Trim();
        if (!key.All(c => c is (>= 'a' and <= 'z') or '_'))
            throw new ArgumentException(ValidTypes, parameterName);
        return Aliases.GetValueOrDefault(key, key);
    }
}
