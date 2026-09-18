using System.Security.Cryptography;
using System.Text;

namespace PhantomDust.PcBridge.Core;

/// Pure profile identity logic. The stable record prefix is compared as an
/// instantaneous load-time identity check. The mutable tail contains counters
/// that change during ordinary arsenal edits and is deliberately excluded.
/// Display names and all record bytes are excluded from the persistent key.
public static class ProfileIdentity {
    public const int RecordBytes = 0x44;
    public const int MatchStart = 4;
    // Most bytes 4..47 distinguish the two save slots. Live final-profile testing
    // showed offset 20 also changes during ordinary use, so it is excluded along
    // with the mutable tail at 48..67.
    public const int MatchLength = 44;
    private const int MutablePrefixOffset = 20;
    public const int NameOffset = 32;
    public const int NameLength = 16;

    public sealed record Match(bool Verified, int? Slot, string? DisplayName, string? ProfileKey, string Reason);

    public static Match Resolve(string bridgeId, string packageFamily, bool selectorLoaded, ReadOnlySpan<byte> current, IReadOnlyList<byte[]> records) {
        if (!selectorLoaded) return Fail("Profile selector is not loaded.");
        if (records.Count != 2 || string.IsNullOrWhiteSpace(bridgeId) || string.IsNullOrWhiteSpace(packageFamily) || current.Length < MatchStart + MatchLength)
            return Fail("Profile identity data is incomplete.");
        var matches = new List<(byte[] record,int index)>();
        for(var index=0;index<records.Count;index++) {
            var candidate=records[index];
            if(candidate.Length>=MatchStart+MatchLength&&Matches(current,candidate))matches.Add((candidate,index));
        }
        if (matches.Count != 1) return Fail(matches.Count == 0 ? "No stored profile matches the loaded profile." : "The loaded profile matches multiple save slots.");
        var record = matches[0].record;
        var slot = BitConverter.ToInt32(record, 0);
        if (slot is < 0 or > 1 || slot != matches[0].index) return Fail("Stored profile slot is invalid.");
        var nameBytes = record.AsSpan(NameOffset, NameLength); var terminator = nameBytes.IndexOf((byte)0);
        if (terminator >= 0) nameBytes = nameBytes[..terminator];
        var name = Encoding.ASCII.GetString(nameBytes).Trim();
        if (string.IsNullOrWhiteSpace(name) || name == "_ERROR_" || name.Any(c => c < 32 || c > 126)) return Fail("Stored profile display name is invalid.");
        var key = DeriveKey(bridgeId, packageFamily, slot);
        return new(true, slot, name, key, "Unique current-to-save-record match.");
    }

    public static string DeriveKey(string bridgeId, string packageFamily, int slot) {
        var canonical = $"phantom-dust-profile-v1\n{bridgeId.Trim().ToUpperInvariant()}\n{packageFamily.Trim().ToUpperInvariant()}\n{slot}";
        return "pdslot-v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static Match Fail(string reason) => new(false, null, null, null, reason);
    private static bool Matches(ReadOnlySpan<byte> current,ReadOnlySpan<byte> candidate) {
        for(var offset=MatchStart;offset<MatchStart+MatchLength;offset++)if(offset!=MutablePrefixOffset&&current[offset]!=candidate[offset])return false;
        return true;
    }
}
