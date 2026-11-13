using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.ClientIcons;

/// <summary>
/// Ubiquiti's public device database, as UniFi Network uses it to draw devices: each model code
/// (<c>shortnames</c>, e.g. U7PG2) and SKU maps to the icon id served from the catalog icon host.
/// </summary>
[VendorSpecific("UniFi", "static.ui.com/fingerprint/ui/public.json: devices[].shortnames/sku -> icon.id")]
public sealed class UbiquitiDeviceCatalog
{
    /// <summary>Where the database is published.</summary>
    public const string SourceUrl = "https://static.ui.com/fingerprint/ui/public.json";

    /// <summary>An empty catalog, before the first read or when it cannot be read.</summary>
    public static readonly UbiquitiDeviceCatalog Empty = new(new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> _iconIdByName;

    private UbiquitiDeviceCatalog(IReadOnlyDictionary<string, string> iconIdByName) => _iconIdByName = iconIdByName;

    /// <summary>Models with an icon.</summary>
    public int Count => _iconIdByName.Count;

    /// <summary>The icon id for a device's model code or short name, or null when the catalog has none.</summary>
    public string? IconIdFor(params string?[] names)
    {
        foreach (var name in names)
            if (!string.IsNullOrWhiteSpace(name) && _iconIdByName.TryGetValue(name.Trim(), out var id))
                return id;
        return null;
    }

    /// <summary>The icon id for a friendly model name ("U7 Pro", "USW-Flex-Mini"), as written or dashed like a SKU.</summary>
    public string? IconIdForModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : IconIdFor(model, model.Trim().Replace(' ', '-'));

    /// <summary>Parses the published JSON. Throws <see cref="JsonException"/> when it is not that shape.</summary>
    public static UbiquitiDeviceCatalog Parse(string json)
    {
        var root = JsonSerializer.Deserialize<Root>(json) ?? throw new JsonException("Empty device catalog");
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in root.Devices ?? [])
        {
            if (string.IsNullOrEmpty(device.Icon?.Id))
                continue;
            // A name listed by two entries keeps the first. Model codes and the SKU go first; the
            // display names ("U7 Pro") let the app's friendly model names find an entry too.
            foreach (var name in (device.Shortnames ?? []).Append(device.Sku).Append(device.Product?.Abbrev).Append(device.Product?.Name))
                if (!string.IsNullOrWhiteSpace(name))
                    byName.TryAdd(name.Trim(), device.Icon.Id);
        }
        return new UbiquitiDeviceCatalog(byName);
    }

    private sealed class Root
    {
        [JsonPropertyName("devices")]
        public List<Device>? Devices { get; set; }
    }

    private sealed class Device
    {
        [JsonPropertyName("sku")]
        public string? Sku { get; set; }

        [JsonPropertyName("shortnames")]
        public List<string>? Shortnames { get; set; }

        [JsonPropertyName("icon")]
        public Icon? Icon { get; set; }

        [JsonPropertyName("product")]
        public Product? Product { get; set; }
    }

    private sealed class Product
    {
        [JsonPropertyName("abbrev")]
        public string? Abbrev { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class Icon
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
