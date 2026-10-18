using System.Runtime.CompilerServices;
namespace Vemo.Core;

public sealed record GoogleConnectionStatus(bool Configured, bool Connected, bool Busy, string? FolderId, string? FolderName, string? LastError, DateTimeOffset? LastSync);
public sealed record GoogleDriveFolder(string Id, string Name, string ProcessorId);
public sealed record GoogleSyncResult(int Imported, int Published, int Skipped);

public sealed partial class GoogleConnectionService : IDisposable
{
    static readonly ConditionalWeakTable<LibraryStore, GoogleConnectionService> instances = new();
    public static GoogleConnectionService Shared(LibraryStore store) => instances.GetValue(store, s => new(s));
    public event EventHandler? StatusChanged;
    public GoogleConnectionStatus Status { get; private set; } = new(false, false, false, null, null, null, null);
    readonly LibraryStore store;
    readonly IGoogleCredentialVault vault;
    readonly IGoogleHttpTransport transport;
    public GoogleConnectionService(LibraryStore store) : this(store, new GoogleWindowsCredentialVault(Path.Combine(store.Root, "google", "credentials")), new GoogleHttpTransport()) { }
    public GoogleConnectionService(LibraryStore store, IGoogleCredentialVault vault, IGoogleHttpTransport transport)
    { this.store = store; this.vault = vault; this.transport = transport; Hydrate(); }
    void Changed() => StatusChanged?.Invoke(this, EventArgs.Empty);
    public Task ImportClientConfigurationAsync(string installedJson, CancellationToken ct = default) => ImportCoreAsync(installedJson, ct);
    public Task ConnectAsync(CancellationToken ct = default) => ConnectCoreAsync(ct);
    public Task DisconnectAsync(CancellationToken ct = default) => DisconnectCoreAsync(ct);
    public Task<IReadOnlyList<GoogleDriveFolder>> ListFoldersAsync(CancellationToken ct = default) => ListCoreAsync(ct);
    public Task<GoogleDriveFolder> CreateFolderAsync(string name, CancellationToken ct = default) => CreateCoreAsync(name, ct);
    public Task SelectFolderAsync(string folderId, CancellationToken ct = default) => SelectCoreAsync(folderId, ct);
    public Task<GoogleSyncResult> SyncAsync(CancellationToken ct = default) => SyncCoreAsync(ct);
    // Implemented in the backend partial file; integration surface stays stable.
    private partial Task ImportCoreAsync(string json, CancellationToken ct);
    private partial Task ConnectCoreAsync(CancellationToken ct);
    private partial Task DisconnectCoreAsync(CancellationToken ct);
    private partial Task<IReadOnlyList<GoogleDriveFolder>> ListCoreAsync(CancellationToken ct);
    private partial Task<GoogleDriveFolder> CreateCoreAsync(string name, CancellationToken ct);
    private partial Task SelectCoreAsync(string id, CancellationToken ct);
    private partial Task<GoogleSyncResult> SyncCoreAsync(CancellationToken ct);
    public void Dispose() => DisposeCore();
    private partial void DisposeCore();
}
