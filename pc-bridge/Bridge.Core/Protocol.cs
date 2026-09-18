using System.Text.Json;
using System.Text.Json.Serialization;
namespace PhantomDust.PcBridge.Core;
public static class Wire {
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
public enum JobState { Queued, Blocked, WaitingForGame, WaitingForFrames, WaitingForGameReady, Applying, AppliedAwaitingGameSave, Completed, Cancelled, Superseded, RecoveryRequired }
public enum CollectionStatus { Unavailable, Observed, Verified }
public sealed record CollectionEntry(int Total, int Free, int Assigned, IReadOnlyList<SkillAllocation> Allocations);
public sealed record SnapshotCollection(CollectionStatus Status, IReadOnlyDictionary<string,CollectionEntry> Skills);
// BootstrapSecret is the existing high-entropy QR/text credential. ShortCode is
// an intentionally separate, human-entered LAN recovery credential.
public sealed record PairRequest(string? BootstrapSecret, string DeviceName,string? DeviceIdentity=null,string? ShortCode=null);
public sealed record PairResponse(string PairingId, string Token, string BridgeId);
public sealed record PairPayload(int Version, string Endpoint, string BridgeId, string CertificateSha256, string BootstrapSecret, long ExpiresAt, string? ShortCode=null, long? ShortCodeExpiresAt=null, bool ShortCodeAvailable=true);
public sealed record BridgeDiscovery(string BridgeId, string DisplayName, string Endpoint, string CertificateSha256, bool ShortCodeAvailable, int ApiVersion=1, int ConnectionVersion=2);
public sealed record BridgeStatus(string BridgeId, string BridgeName, string Mode, string State, bool GameRunning, bool ProfileLoaded, bool WriteSupported, bool Paused, long CapturedAt, string Message,int WorkflowVersion=9, string? ErrorCode=null, string? RecoveryAction=null, bool? GameForeground=null, string? ReadinessCode=null, string? RequiredAction=null, bool StoreWatchSupported=false);
public sealed record Snapshot(string ProfileKey, string? Fingerprint, long CapturedAt, bool GameRunning, bool ProfileVerified, bool InventoryVerified, bool WriteSupported, bool SafeStateVerified, IReadOnlyList<SnapshotDeck> Arsenals, IReadOnlyDictionary<string,int> FreeInventory, IReadOnlyDictionary<string,string>? Diagnostics = null, bool ArsenalImportSupported = false, bool ControlledTestSession = false, string? SessionKey = null, SnapshotCollection? Collection = null, int? GameProfileSlot = null, string? GameProfileDisplayName = null, string? ProfileBindingKind = null, bool ProfileImportSupported = false, int? ArsenalNameMaxLength = null, bool? GameForeground = null, string? ReadinessCode = null, string? RequiredAction = null, bool SkillCountChecksSupported = false);
public sealed record SnapshotDeck(int Slot, string Name, int CaseCapacity, IReadOnlyList<string> SkillIds, string? Fingerprint = null, string? ContentFingerprint = null);
public sealed record DeckSpec(string DeckId, long Revision, string Name, IReadOnlyList<string> SkillIds, int CaseCapacity, string RulesetId);
public sealed record PlanRequest(string ProfileKey, int TargetSlot, string? ExpectedFingerprint, DeckSpec Deck, bool RequireEmptyTarget = false);
public sealed record MissingSkill(string SkillId, int Needed, int Available, int Missing);
public sealed record PlanBlocker(string Code, string Message);
public sealed record PlanResult(bool CanBuild, bool CanApply, IReadOnlyList<MissingSkill> MissingSkills, IReadOnlyList<PlanBlocker> Blockers, string? SnapshotFingerprint, string? TargetFingerprint, IReadOnlyDictionary<string,int> NewFreeInventory, long CheckedAt = 0);
public sealed record QueueRequest(string ProfileKey, int TargetSlot, string? ExpectedFingerprint, DeckSpec Deck, string ConfirmedFingerprint, string LinkId, long LinkGeneration, string? ClientJourneyId = null, string? SupersedesJourneyId = null);
public sealed record LinkRequest(string LinkId, string DeckId, string ProfileKey, int TargetSlot, long LinkGeneration, string ConfirmedFingerprint, bool OverwriteConfirmed = false, DeckSpec? IdenticalDeck = null, string? ReplacesLinkId = null, long? ReplacesLinkGeneration = null);
public sealed record RecoveryResolutionRequest(string ProfileKey,int TargetSlot,string ReviewedFingerprint);
public sealed record PcLink(string LinkId, string PairingId, string DeckId, string ProfileKey, int TargetSlot, long LinkGeneration, string ExpectedFingerprint, bool Active, string? BoundDisplayName = null, string? BaselineContentFingerprint = null);
public sealed record ProfileImportDeck(string LinkId, string DeckId, int TargetSlot, string ConfirmedFingerprint, string BaselineContentFingerprint);
public sealed record ProfileImportRequest(string ImportId, string ProfileKey, IReadOnlyList<ProfileImportDeck> Decks);
public sealed record ProfileImportResult(string ImportId, string ProfileKey, IReadOnlyList<PcLink> Links, bool AlreadyApplied = false);
public sealed record AppliedEvidence(string SessionKey, string ProfileKey, string TargetFingerprint, int CaseCapacity, IReadOnlyList<string> SkillIds, SnapshotCollection? Collection, bool ProfileVerified = false, bool ControlledTestSession = false, string? Name = null);
public sealed record ArsenalState(string Name, IReadOnlyList<string> SkillIds, int CaseCapacity);
public sealed record ArsenalChange(ArsenalState Before, ArsenalState After);
public sealed record JobView(string JobId, string PairingId, string DeckId, long Revision, int TargetSlot, JobState State, string? Reason, long CreatedAt, long UpdatedAt, QueueRequest Request, PlanResult? Plan, AppliedEvidence? Evidence = null, ArsenalChange? Changes = null, string? Phase = null, string? PhaseMessage = null, string? SupersededByJourneyId = null);
public sealed record WriteResult(JobState State, ArsenalChange? Changes = null, string? Reason = null);
public sealed record ControlRequest(bool Paused);
public sealed class BridgeException(int status, string message) : Exception(message) { public int Status { get; } = status; public string Code => Message.Contains("Link context changed",StringComparison.OrdinalIgnoreCase)?"LinkContextChanged":Status==401?"PairingRequired":Status==409?"PcChanged":"RequestFailed"; public string RecoveryAction => Code=="LinkContextChanged"?"RefreshLink":Status==401?"Pair":"Refresh"; }
public interface IPhantomDustProfileReader { Snapshot Read(); }
public interface IProcessMemory { byte[] Read(nuint address, int length); }
public interface IPhantomDustProfileWriter { bool WriteSupported { get; } }

public sealed record TransferReceipt(string ReceiptId,string ProfileKey,int TargetSlot,string Fingerprint);
public enum SkillStoreState { SkillStoreOpen, SkillStoreClosed, GameClosed, ProfileMismatch, Unsupported }
public sealed record StoreWatchRequest(string ProfileKey,IReadOnlyList<string> SkillIds);
public sealed record SkillStoreMatch(string SkillId,int? Price,string? Name=null);
public sealed record SkillStoreResult(string ProfileKey,SkillStoreState State,long? ScannedAt=null,string? Fingerprint=null,IReadOnlyList<SkillStoreMatch>? Matches=null,IReadOnlyList<SkillStoreMatch>? KnownPrices=null);
public interface IPhantomDustSkillStoreReader { bool Supported { get; } SkillStoreResult Read(); }
