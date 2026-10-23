using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// What a plan still needs before it can apply (spec "Safety tiers": plan review is the only
/// confirmation surface): acknowledgements for tier 2 items, accepted regressions for tier 3, the
/// typed confirmation for lockouts, and no errors.
/// </summary>
public static class PlanReadiness
{
    /// <summary>The text the typed confirmation must match: the device names the lockouts name.</summary>
    public static string ConfirmationText(PlanDocument doc)
    {
        var devices = doc.Lockouts.SelectMany(l => l.Devices).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return devices.Count == 0 ? "" : string.Join(", ", devices);
    }

    /// <summary>The plan has a certain lockout, which only a Site Admin can override.</summary>
    public static bool HasCertainLockout(PlanDocument doc) => doc.Lockouts.Any(l => l.Kind == "certain");

    /// <summary>Items still open, empty when the plan can apply.</summary>
    public static List<string> Remaining(PlanDocument doc, PlanAcknowledgements acks, bool isSiteAdmin)
    {
        var open = new List<string>();
        if (doc.Errors.Count > 0)
            open.Add(doc.Errors.Count == 1 ? "1 error to fix" : $"{doc.Errors.Count} errors to fix");

        var toAck = doc.Operations.Count(o => o.Tier == PlanTiers.Acknowledge && !acks.Items.Contains(o.Key));
        if (toAck > 0) open.Add(toAck == 1 ? "1 item to acknowledge" : $"{toAck} items to acknowledge");

        var toAccept = doc.Regressions.Count(r => !acks.Regressions.Contains(r.Key));
        if (toAccept > 0) open.Add(toAccept == 1 ? "1 regression to accept" : $"{toAccept} regressions to accept");

        if (doc.Lockouts.Count > 0)
        {
            if (HasCertainLockout(doc) && !isSiteAdmin)
                open.Add("a certain lockout only a Site Admin can override");
            else if (!string.Equals(acks.TypedConfirmation?.Trim(), ConfirmationText(doc), StringComparison.OrdinalIgnoreCase))
                open.Add("1 confirmation to type");
        }
        return open;
    }

    /// <summary>Hash of the live objects a plan touches, to tell when they changed after approval.</summary>
    public static string TouchedHash(PlanDocument doc, ConfigState live)
    {
        var sb = new StringBuilder();
        foreach (var op in doc.Operations.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            sb.Append(op.Key).Append('=');
            if (op.ObjectId != null && !op.ObjectId.StartsWith(PlanBuilder.RefPrefix, StringComparison.Ordinal))
            {
                var obj = live.Get(op.Collection).FirstOrDefault(o => ConfigState.IdOf(o) == op.ObjectId);
                sb.Append(obj == null ? "missing" : CanonicalJson.Serialize(Strip(obj, op.Collection)));
            }
            else
            {
                var def = ConfigCollections.Get(op.Collection);
                sb.Append(live.Get(op.Collection).Any(o => ConfigState.DisplayName(def, o) == op.ObjectName) ? "exists" : "absent");
            }
            sb.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static JsonObject Strip(JsonObject o, string collection)
    {
        var def = ConfigCollections.Get(collection);
        var copy = (JsonObject)o.DeepClone();
        copy.Remove(ConfigCollections.DeviceMetaField);
        foreach (var key in copy.Select(p => p.Key).ToList())
            if (ConfigDiffEngine.IsChurnField(key) || (def?.ReadOnlyFields.Contains(key) ?? false))
                copy.Remove(key);
        return copy;
    }
}
