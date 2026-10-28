using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FactorioMCP.Services;

internal sealed partial class FactorioService
{
    // ── Machine configuration: recipes, logistic requests, rocket launch ──────────
    // Player agents had to use execute_lua for all three (see the rocket run).

    /// <summary>
    /// Lua helper <c>pick(x, y, types)</c>: the entity of one of the given types whose box contains the
    /// point, else the nearest one within 0.7 tiles. Never returns the controlled character.
    /// </summary>
    private const string LuaPickEntity = """
        local function pick(surface, x, y, types)
            local list = surface.find_entities_filtered{ position = { x, y }, type = types }
            if #list == 0 then list = surface.find_entities_filtered{ position = { x, y }, radius = 0.7, type = types } end
            local best, best_d
            for _, e in pairs(list) do
                local d = (e.position.x - x) ^ 2 + (e.position.y - y) ^ 2
                if not best or d < best_d then best, best_d = e, d end
            end
            return best
        end
        local function status_name(e)
            for k, v in pairs(defines.entity_status) do if v == e.status then return k end end
            return "unknown"
        end
        """;

    /// <summary>
    /// Lua helper <c>pick_with_inventory(surface, x, y, inv_id)</c>: the entity that has inventory <c>inv_id</c>,
    /// preferring one whose box contains the point, else the nearest within 1 tile. Belts, ore and characters
    /// next to a chest were picked before ("entities[1]"), which made get_entity_inventory report a belt.
    /// <c>describe_at(surface, x, y)</c> lists what is there, for error messages.
    /// </summary>
    internal const string LuaPickWithInventory = """
        local function pick_with_inventory(surface, x, y, inv_id)
            local best, best_d
            local function consider(list)
                for _, e in pairs(list) do
                    if e.type ~= "resource" and e.type ~= "character" and e.get_inventory(inv_id) then
                        local d = (e.position.x - x) ^ 2 + (e.position.y - y) ^ 2
                        if not best or d < best_d then best, best_d = e, d end
                    end
                end
            end
            consider(surface.find_entities_filtered{ position = { x, y } })
            if not best then consider(surface.find_entities_filtered{ position = { x, y }, radius = 1 }) end
            return best
        end
        local function describe_at(surface, x, y)
            local names = {}
            for _, e in pairs(surface.find_entities_filtered{ position = { x, y }, radius = 1 }) do
                if e.type ~= "resource" then names[#names + 1] = e.name end
            end
            return table.concat(names, ",")
        end
        """;

    /// <summary>
    /// Lua snippet: stop with an out_of_range error when the entity's box is farther than the reach distance.
    /// (can_reach_entity answers true for scripted characters at any distance, so the distance is checked here.)
    /// </summary>
    private const string LuaRequireReach = """
            local bb, pp = e.bounding_box, player.position
            local gx = math.max(bb.left_top.x - pp.x, 0, pp.x - bb.right_bottom.x)
            local gy = math.max(bb.left_top.y - pp.y, 0, pp.y - bb.right_bottom.y)
            local d = math.sqrt(gx * gx + gy * gy)
            if d > player.reach_distance then
                rcon.print('{"success":false,"error":"out_of_range","entity":"' .. e.name .. '","distance":' .. string.format("%.1f", d) .. ',"reach":' .. player.reach_distance .. ',"hint":"walk closer to the entity first"}')
                return
            end
        """;

    /// <summary>
    /// Set the recipe of an assembling machine, chemical plant or oil refinery at a position.
    /// Errors name the cause: no machine there, unknown or not yet researched recipe, a recipe the machine
    /// cannot craft, or the machine out of reach. Items removed by the recipe change go to the character.
    /// </summary>
    public Task<string> SetRecipeAsync(double x, double y, string recipe, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe);
        var r = EscapeLuaString(recipe.Trim());
        var lua = string.Create(CultureInfo.InvariantCulture, $$"""
            {{LuaPickEntity}}
            local player = game.connected_players[1]
            local e = pick(player.surface, {{x}}, {{y}}, { "assembling-machine" })
            if not e then
                local other = player.surface.find_entities_filtered{ position = { {{x}}, {{y}} } }[1]
                rcon.print('{"success":false,"error":"no_machine","x":{{x}},"y":{{y}},"found":"' .. (other and other.name or "nothing") .. '","hint":"set_recipe works on assembling machines, chemical plants and oil refineries; furnaces choose their recipe themselves"}')
                return
            end
            {{LuaRequireReach}}
            local proto = prototypes.recipe["{{r}}"]
            if not proto then
                rcon.print('{"success":false,"error":"unknown_recipe","recipe":"{{r}}"}')
                return
            end
            local force_recipe = e.force.recipes["{{r}}"]
            if not (force_recipe and force_recipe.enabled) then
                rcon.print('{"success":false,"error":"recipe_not_researched","recipe":"{{r}}"}')
                return
            end
            -- Factorio 2.1: a recipe has a list of categories (recipe.categories); older versions one (recipe.category)
            local ok_cats, rcats = pcall(function() return proto.categories end)
            if not (ok_cats and rcats) then rcats = { proto.category } end
            local fits = false
            for _, cat in pairs(rcats) do if e.prototype.crafting_categories[cat] then fits = true end end
            if not fits then
                local cats = {}
                for c in pairs(e.prototype.crafting_categories) do cats[#cats + 1] = c end
                rcon.print('{"success":false,"error":"wrong_machine","recipe":"{{r}}","recipe_categories":"' .. table.concat(rcats, ",") .. '","entity":"' .. e.name .. '","entity_categories":"' .. table.concat(cats, ",") .. '"}')
                return
            end
            local previous = e.get_recipe()
            local removed = e.set_recipe("{{r}}") or {}
            local returned, spilled = 0, 0
            for _, stack in pairs(removed) do
                local inserted = player.insert(stack)
                returned = returned + inserted
                if inserted < stack.count then
                    player.surface.spill_item_stack{ position = e.position, stack = { name = stack.name, count = stack.count - inserted, quality = stack.quality } }
                    spilled = spilled + stack.count - inserted
                end
            end
            rcon.print('{"success":true,"entity":"' .. e.name .. '","x":' .. e.position.x .. ',"y":' .. e.position.y ..
                ',"recipe":"{{r}}","previous_recipe":"' .. (previous and previous.name or "none") ..
                '","items_returned":' .. returned .. ',"items_spilled":' .. spilled .. '}')
            """);
        return rcon.ExecuteLuaAsync(lua, cancellationToken);
    }

    /// <summary>One logistic request: item name and the minimum amount to keep in the chest.</summary>
    internal sealed record LogisticRequest(string Item, int Count);

    /// <summary>
    /// Parse the requests JSON of <see cref="SetLogisticRequestsAsync"/>: <c>[{"item":"iron-plate","count":200}, ...]</c>.
    /// Throws <see cref="ArgumentException"/> naming the bad entry.
    /// </summary>
    internal static List<LogisticRequest> ParseLogisticRequests(string requestsJson)
    {
        List<LogisticRequest>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<LogisticRequest>>(requestsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"requests is not valid JSON: {ex.Message}. Expected [{{\"item\":\"iron-plate\",\"count\":200}}].", nameof(requestsJson));
        }

        parsed ??= [];
        for (var i = 0; i < parsed.Count; i++)
        {
            var req = parsed[i];
            if (string.IsNullOrWhiteSpace(req.Item))
                throw new ArgumentException($"requests[{i}] has no item name.", nameof(requestsJson));
            if (req.Count < 0)
                throw new ArgumentException($"requests[{i}] ({req.Item}) has a negative count.", nameof(requestsJson));
        }
        return parsed;
    }

    /// <summary>
    /// Set the logistic requests of a requester or buffer chest (and optionally its slot limit).
    /// <paramref name="replace"/> = true clears the existing requests first. A count of 0 removes that request.
    /// </summary>
    public Task<string> SetLogisticRequestsAsync(double x, double y, string requestsJson, bool replace = true,
        int? slotLimit = null, CancellationToken cancellationToken = default)
    {
        List<LogisticRequest> requests;
        try { requests = ParseLogisticRequests(requestsJson); }
        catch (ArgumentException ex) { return Task.FromResult($$"""{"success":false,"error":"invalid_requests","detail":"{{EscapeLuaString(ex.Message)}}"}"""); }
        if (slotLimit is < 0)
            return Task.FromResult("""{"success":false,"error":"invalid_slot_limit","detail":"slotLimit must be 0 (no limit) or more"}""");

        var table = new StringBuilder();
        foreach (var req in requests)
            table.Append(CultureInfo.InvariantCulture, $"{{ \"{EscapeLuaString(req.Item.Trim())}\", {req.Count} }}, ");

        var lua = string.Create(CultureInfo.InvariantCulture, $$"""
            {{LuaPickEntity}}
            local player = game.connected_players[1]
            local e = pick(player.surface, {{x}}, {{y}}, { "logistic-container" })
            if not e then
                local other = player.surface.find_entities_filtered{ position = { {{x}}, {{y}} } }[1]
                rcon.print('{"success":false,"error":"no_logistic_chest","x":{{x}},"y":{{y}},"found":"' .. (other and other.name or "nothing") .. '"}')
                return
            end
            local mode = e.prototype.logistic_mode
            if mode ~= "requester" and mode ~= "buffer" then
                rcon.print('{"success":false,"error":"chest_cannot_request","entity":"' .. e.name .. '","logistic_mode":"' .. tostring(mode) .. '","hint":"only requester and buffer chests have requests"}')
                return
            end
            {{LuaRequireReach}}
            local requests = { {{table}} }
            for i, req in ipairs(requests) do
                if not prototypes.item[req[1]] then
                    rcon.print('{"success":false,"error":"unknown_item","item":"' .. req[1] .. '","index":' .. (i - 1) .. '}')
                    return
                end
            end
            local point = e.get_logistic_point(defines.logistic_member_index.logistic_container)
            local section = point.sections[1] or point.add_section()
            if {{(replace ? "true" : "false")}} then
                for i = section.filters_count, 1, -1 do section.clear_slot(i) end
            end
            for _, req in ipairs(requests) do
                local slot
                for i, f in pairs(section.filters) do
                    if f.value and f.value.name == req[1] then slot = i end
                end
                if req[2] == 0 then
                    if slot then section.clear_slot(slot) end
                else
                    slot = slot or (section.filters_count + 1)
                    section.set_slot(slot, { value = { name = req[1], quality = "normal" }, min = req[2] })
                end
            end
            {{(slotLimit is int limit
                ? $"local inv = e.get_inventory(defines.inventory.chest); inv.set_bar({(limit == 0 ? "nil" : (limit + 1).ToString(CultureInfo.InvariantCulture))})"
                : "")}}
            local now = {}
            for _, f in pairs(section.filters) do
                if f.value then now[#now + 1] = '{"item":"' .. f.value.name .. '","count":' .. (f.min or 0) .. '}' end
            end
            local bar = e.get_inventory(defines.inventory.chest).get_bar()
            rcon.print('{"success":true,"entity":"' .. e.name .. '","x":' .. e.position.x .. ',"y":' .. e.position.y ..
                ',"requests":[' .. table.concat(now, ",") .. '],"slot_limit":' .. (bar - 1) .. '}')
            """);
        return rcon.ExecuteLuaAsync(lua, cancellationToken);
    }

    /// <summary>
    /// Launch the rocket of the rocket silo at a position (or the nearest silo when no position is given).
    /// Errors say why nothing launched: no silo, out of reach, or no finished rocket (with parts built so far).
    /// </summary>
    public Task<string> LaunchRocketAsync(double? x = null, double? y = null, CancellationToken cancellationToken = default)
    {
        var find = x is double px && y is double py
            ? string.Create(CultureInfo.InvariantCulture, $"local e = pick(player.surface, {px}, {py}, {{ \"rocket-silo\" }})")
            : """
              local e, best
              for _, s in pairs(player.surface.find_entities_filtered{ type = "rocket-silo", force = player.force }) do
                  local d = (s.position.x - player.position.x) ^ 2 + (s.position.y - player.position.y) ^ 2
                  if not best or d < best then e, best = s, d end
              end
              """;
        var lua = $$"""
            {{LuaPickEntity}}
            local player = game.connected_players[1]
            {{find}}
            if not e then
                rcon.print('{"success":false,"error":"no_rocket_silo","hint":"give x and y of the silo, or build one"}')
                return
            end
            {{LuaRequireReach}}
            if e.status ~= defines.entity_status.waiting_to_launch_rocket then
                rcon.print('{"success":false,"error":"rocket_not_ready","status":"' .. status_name(e) .. '","rocket_parts":' .. e.rocket_parts .. ',"x":' .. e.position.x .. ',"y":' .. e.position.y .. '}')
                return
            end
            local launched = e.launch_rocket()
            rcon.print('{"success":' .. tostring(launched) .. ',"x":' .. e.position.x .. ',"y":' .. e.position.y .. ',"rockets_launched":' .. player.force.rockets_launched .. (launched and '' or ',"error":"launch_refused"') .. '}')
            """;
        return rcon.ExecuteLuaAsync(lua, cancellationToken);
    }
}
