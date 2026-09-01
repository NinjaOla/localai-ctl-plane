namespace llamactl.Contracts;

public sealed record ModelFile(
    string RelativePath,
    long SizeBytes,
    bool IsShard,
    bool IsMmproj,
    bool IsDraftHead,
    bool FromHfCache = false);

public sealed record OrphanedBlob(string RelativePath, long SizeBytes);

public sealed record ModelInventory(
    DateTimeOffset ScannedAt,
    long FreeBytes,
    IReadOnlyList<ModelFile> Files,
    IReadOnlyList<OrphanedBlob> OrphanedBlobs);

public enum LibraryOperationKind { CreateLink, CreateDirectory, Remove }

public sealed record LibraryOperation(LibraryOperationKind Kind, string Path, string? Target);

public sealed record ReconcileLibraryResult(bool Applied, IReadOnlyList<LibraryOperation> Operations);

public sealed record InspectGgufRequest(string RelativePath, bool FromHfCache = false);

public sealed record GgufInspection(
    string RelativePath,
    string? Architecture,
    long? TrainingContext,
    int TensorCount,
    bool HasDraftHead,
    IReadOnlyList<string> DraftTensors);

public sealed record DeleteModelRequest(IReadOnlyList<string> RelativePaths, bool FromHfCache = false);
public sealed record DeleteModelResult(long FreedBytes);

public sealed record HuggingFaceRepository(string Id, long Downloads, long Likes, DateTimeOffset? LastModified);
public sealed record HuggingFaceFile(string Path, long SizeBytes);

public enum FitVerdict { Fits, ExceedsVram, ExceedsDisk }
public sealed record ModelFitEstimate(string Variant, long SizeBytes, FitVerdict Verdict, long? EstimatedKvBytes, string Caveat);

public enum DownloadState { Queued, Running, Completed, Cancelled, Failed }
public sealed record StartModelDownload(string Repository, string Revision, IReadOnlyList<HuggingFaceFile> Files);
public sealed record DownloadProgress(Guid Id, DownloadState State, long BytesReceived, long TotalBytes, string? CurrentFile, string? Error);