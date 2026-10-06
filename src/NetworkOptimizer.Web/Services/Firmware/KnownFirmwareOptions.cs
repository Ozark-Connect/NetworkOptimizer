using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>A device a known build can be deployed to, as Deploy Known Firmware lists it.</summary>
/// <param name="Mac">Normalized MAC.</param>
/// <param name="Label">The list entry, e.g. <c>[Gateway] My Gateway</c>.</param>
/// <param name="Model">Model code, the key builds are filed under.</param>
/// <param name="DisplayModel">Model as people read it, e.g. <c>UCG-Fiber</c>.</param>
public sealed record KnownFirmwareDevice(string Mac, string Label, string Model, string DisplayModel);

/// <summary>A build Deploy Known Firmware offers, and the pin that plans it.</summary>
/// <param name="Key">Identity across rows: one image or package, whichever devices take it.</param>
/// <param name="Label">The list entry, e.g. <c>UniFi Network 11.0.81</c>.</param>
/// <param name="Kind">The surface it installs.</param>
/// <param name="Version">Version it carries.</param>
/// <param name="Pin">What the plan wizard is opened with; identical to a pasted link's.</param>
public sealed record KnownFirmwareBuild(string Key, string Label, FirmwareUrlKind Kind, string Version, RolloutBuildPin Pin);

/// <summary>One device that can take one build.</summary>
/// <param name="Device">The device.</param>
/// <param name="Build">The build.</param>
/// <param name="Older">True when the build is older than what the device runs (a device firmware downgrade).</param>
public sealed record KnownFirmwareOption(KnownFirmwareDevice Device, KnownFirmwareBuild Build, bool Older);

/// <summary>The console as Deploy Known Firmware needs it.</summary>
/// <param name="Platform">Hardware shortname UniFi OS builds are filed under.</param>
/// <param name="OsVersion">Installed UniFi OS.</param>
/// <param name="NetworkVersion">Installed UniFi Network.</param>
/// <param name="StandaloneConsole">True for UniFi OS Server, which takes the generic Network package and no UniFi OS image.</param>
/// <param name="OfferedOs">UniFi OS builds the console itself offers, with their links.</param>
/// <param name="OfferedNetworkVersion">The UniFi Network update the console offers, if any.</param>
public sealed record KnownFirmwareConsole(
    string? Platform,
    string? OsVersion,
    string? NetworkVersion,
    bool StandaloneConsole,
    IReadOnlyList<(string Version, string Url)> OfferedOs,
    string? OfferedNetworkVersion);

/// <summary>
/// Builds the device-by-build options behind Deploy Known Firmware. Each pick resolves to the same
/// <see cref="RolloutBuildPin"/> a pasted link would, so the plan wizard cannot tell them apart.
/// UniFi OS and UniFi Network never go backward, so their older builds are not offered at all;
/// device firmware older than what runs is offered and marked, as a pasted link would downgrade it.
/// </summary>
public static class KnownFirmwareOptions
{
    /// <summary>The host the Consoles' catalogs serve device images from, and which networks already allow.</summary>
    private const string CatalogImageHost = "fw-download.ubnt.com";

    /// <param name="devices">The site's adopted devices.</param>
    /// <param name="console">The console, or null when it did not answer.</param>
    /// <param name="deviceBuilds">The shared catalog's device builds.</param>
    /// <param name="osBuilds">The shared catalog's UniFi OS builds.</param>
    /// <param name="networkBuilds">The shared catalog's UniFi Network builds.</param>
    public static List<KnownFirmwareOption> Build(
        IReadOnlyList<PlannerDevice> devices,
        KnownFirmwareConsole? console,
        IReadOnlyList<SharedFirmwareBuild> deviceBuilds,
        IReadOnlyList<SharedUniFiOsBuild> osBuilds,
        IReadOnlyList<SharedNetworkAppBuild> networkBuilds)
    {
        var options = new List<KnownFirmwareOption>();

        // A Cloud Gateway never takes device firmware; it updates as UniFi OS and runs the Network app.
        var cloudGateway = devices.FirstOrDefault(d =>
            FirmwareTimingEstimator.Classify(d) == FirmwareDeviceClass.CloudGatewayUniFiOs);

        var deviceImages = DeviceImages(deviceBuilds);
        foreach (var device in devices)
        {
            // A cellular modem only installs the build its console cached, so a pin never applies to it.
            if (device == cloudGateway || string.IsNullOrWhiteSpace(device.Model)
                || FirmwareTimingEstimator.Classify(device) == FirmwareDeviceClass.CellularModem) continue;
            if (!deviceImages.TryGetValue(device.Model, out var images)) continue;

            var entry = ToDevice(device);
            foreach (var image in images)
            {
                if (FirmwareVersionFormat.SameBuild(image.Version, device.FromVersion)) continue;
                options.Add(new KnownFirmwareOption(entry, image,
                    FirmwareVersionFormat.IsNewer(device.FromVersion, image.Version)));
            }
        }

        if (cloudGateway != null && console != null)
        {
            var entry = ToDevice(cloudGateway);
            if (!console.StandaloneConsole)
            {
                foreach (var build in UniFiOsBuilds(console, osBuilds))
                    options.Add(new KnownFirmwareOption(entry, build, false));
            }
            foreach (var build in NetworkBuilds(console, networkBuilds))
                options.Add(new KnownFirmwareOption(entry, build, false));
        }

        return options;
    }

    private static KnownFirmwareDevice ToDevice(PlannerDevice device)
    {
        var display = string.IsNullOrWhiteSpace(device.DisplayModel)
            ? UniFiProductDatabase.GetBestProductName(device.Model, device.Model)
            : device.DisplayModel;
        var name = string.IsNullOrWhiteSpace(device.Name) ? display : device.Name;
        return new KnownFirmwareDevice(device.Mac, $"[{device.Type.ToDisplayName()}] {name}", device.Model, display);
    }

    /// <summary>
    /// Device images by model. One image (same md5 and version) can be filed for a whole family;
    /// it is one build whose pin covers every model it was filed for, each from its own catalog
    /// URL, as a pasted link's family is.
    /// </summary>
    private static Dictionary<string, List<KnownFirmwareBuild>> DeviceImages(IReadOnlyList<SharedFirmwareBuild> rows)
    {
        var byModel = new Dictionary<string, List<KnownFirmwareBuild>>(StringComparer.OrdinalIgnoreCase);
        var usable = rows.Where(r => !string.IsNullOrWhiteSpace(r.Model) && !string.IsNullOrWhiteSpace(r.Version)
                                     && !string.IsNullOrWhiteSpace(r.Url));

        foreach (var image in usable.GroupBy(r => ImageKey(r)))
        {
            var urls = image
                .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => PreferCatalogHost(g).Url, StringComparer.OrdinalIgnoreCase);
            var models = urls.Keys.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            var version = image.Select(r => r.Version).FirstOrDefault(v => v.Count(c => c == '.') >= 3)
                ?? image.First().Version;
            var lead = models[0];
            var pin = new RolloutBuildPin(FirmwareUrlKind.Device, lead, version, urls[lead], models, urls);

            foreach (var model in models)
            {
                var label = $"{UniFiProductDatabase.GetBestProductName(model, model)} firmware {FirmwareVersionFormat.ShortOrNull(version) ?? version}";
                var build = new KnownFirmwareBuild($"device:{image.Key}", label, FirmwareUrlKind.Device, version, pin);
                if (!byModel.TryGetValue(model, out var list)) byModel[model] = list = [];
                list.Add(build);
            }
        }

        return byModel;
    }

    /// <summary>An image is its md5 where the catalog has one, else its URL, at its short version.</summary>
    private static string ImageKey(SharedFirmwareBuild row) =>
        $"{(string.IsNullOrWhiteSpace(row.Md5Sum) ? row.Url : row.Md5Sum.ToLowerInvariant())}@{FirmwareVersionFormat.ShortOrNull(row.Version) ?? row.Version}";

    private static SharedFirmwareBuild PreferCatalogHost(IEnumerable<SharedFirmwareBuild> rows) =>
        rows.FirstOrDefault(r => Uri.TryCreate(r.Url, UriKind.Absolute, out var u)
                                 && u.Host.Equals(CatalogImageHost, StringComparison.OrdinalIgnoreCase))
        ?? rows.First();

    /// <summary>
    /// UniFi OS builds for the console's platform, newer than what it runs. The catalog can file one
    /// image under two version spellings (<c>6.0.11</c> and <c>v6.0.11+5efda8b</c>), so builds are
    /// one per URL, under the plainer spelling.
    /// </summary>
    private static IEnumerable<KnownFirmwareBuild> UniFiOsBuilds(KnownFirmwareConsole console, IReadOnlyList<SharedUniFiOsBuild> rows)
    {
        if (string.IsNullOrWhiteSpace(console.Platform)) yield break;

        var candidates = rows
            .Where(r => RolloutPlanComposer.SamePlatform(r.Platform, console.Platform) && !string.IsNullOrWhiteSpace(r.Url))
            .Select(r => (r.Version, Url: r.Url!))
            .Concat(console.OfferedOs);

        foreach (var image in candidates.GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase))
        {
            var version = image.Select(c => c.Version).OrderBy(v => v.Length).First();
            if (!FirmwareVersionFormat.IsNewer(version, console.OsVersion)) continue;

            var shortVersion = FirmwareVersionFormat.ShortOrNull(version) ?? version;
            yield return new KnownFirmwareBuild(
                $"os:{image.Key}", $"UniFi OS {shortVersion}", FirmwareUrlKind.UniFiOs, version,
                new RolloutBuildPin(FirmwareUrlKind.UniFiOs, console.Platform, version, image.Key));
        }
    }

    /// <summary>
    /// UniFi Network builds newer than what runs: the catalog's, and the console's own offer. The
    /// package URL follows from the version, as the planner derives it, so one entry per version.
    /// </summary>
    private static IEnumerable<KnownFirmwareBuild> NetworkBuilds(KnownFirmwareConsole console, IReadOnlyList<SharedNetworkAppBuild> rows)
    {
        var versions = rows.Select(r => r.Version)
            .Append(console.OfferedNetworkVersion)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions)
        {
            if (!FirmwareVersionFormat.IsNewer(version, console.NetworkVersion)) continue;

            var package = console.StandaloneConsole ? "unifi_sysvinit_all" : "unifi-native_sysvinit";
            var url = $"https://dl.ui.com/unifi/{version}/{package}.deb";
            yield return new KnownFirmwareBuild(
                $"network:{version}", $"UniFi Network {version}", FirmwareUrlKind.NetworkApp, version,
                new RolloutBuildPin(FirmwareUrlKind.NetworkApp, null, version, url));
        }
    }
}
