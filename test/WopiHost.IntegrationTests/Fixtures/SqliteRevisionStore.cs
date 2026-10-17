using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WopiHost.Abstractions;

namespace WopiHost.IntegrationTests.Fixtures;

// Test infrastructure, not a production storage adapter. Every connection reopens the on-disk
// database. Bytes, revision and complete delivery receipts commit in the same SQLite transaction.
internal sealed class SqliteRevisionStore(string databasePath, string extension = "docx") : IWopiRevisionStore, IWopiStorageProvider, IWopiWritableStorageProvider
{
    public const string FileId = "document-1";
    private string Extension => extension;
    public bool LoseNextAcknowledgement { get; set; }
    public bool FailNextRead { get; set; }
    public bool MissingNextRead { get; set; }
    public bool BlockNextReadUntilCancelled { get; set; }
    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int DirectWrites { get; private set; }
    public IWopiContainer RootContainer { get; } = new Root();
    public int FileNameMaxLength => 250;
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    public void Initialize(byte[] initial)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS files(id TEXT PRIMARY KEY,bytes BLOB NOT NULL,revision INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS receipts(binding TEXT NOT NULL,operation TEXT NOT NULL,fingerprint TEXT NOT NULL,result TEXT NOT NULL,PRIMARY KEY(binding,operation)); INSERT OR IGNORE INTO files VALUES($id,$bytes,0);";
        command.Parameters.AddWithValue("$id", FileId);
        command.Parameters.AddWithValue("$bytes", initial);
        command.ExecuteNonQuery();
    }
    public async Task<WopiRevisionRead> ReadAsync(string fileId, CancellationToken cancellationToken = default)
    {
        if (FailNextRead) { FailNextRead = false; throw new IOException("Destination temporarily unavailable."); }
        if (MissingNextRead) { MissingNextRead = false; throw new FileNotFoundException("Destination disappeared after lookup."); }
        if (BlockNextReadUntilCancelled)
        {
            BlockNextReadUntilCancelled = false;
            ReadStarted.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { ReadCancelled.SetResult(); }
        }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bytes,revision FROM files WHERE id=$id";
        command.Parameters.AddWithValue("$id", fileId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new FileNotFoundException(fileId);
        var bytes = (byte[])reader[0];
        return new WopiRevisionRead(new MemoryStream(bytes, writable: false), "r" + reader.GetInt64(1), bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
    public async Task<WopiRevisionWriteResult> CompareExchangeAsync(WopiRevisionWrite request, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.LongLength != request.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != request.Sha256) throw new InvalidDataException("Content fingerprint mismatch.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT fingerprint,result FROM receipts WHERE binding=$binding AND operation=$operation";
        command.Parameters.AddWithValue("$binding", request.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$operation", request.OperationId.ToString("D"));
        var fingerprint = JsonSerializer.Serialize(request);
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != fingerprint) throw new InvalidDataException("Changed operation fingerprint.");
                return new(WopiRevisionWriteStatus.Applied, reader.GetString(1));
            }
        }
        command.CommandText = "UPDATE files SET bytes=$bytes,revision=revision+1 WHERE id=$id AND 'r'||revision=$expected RETURNING 'r'||revision";
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$id", request.FileId);
        command.Parameters.AddWithValue("$expected", request.ExpectedRevision);
        if (command.ExecuteScalar() is not string next) return new(WopiRevisionWriteStatus.Conflict);
        command.CommandText = "INSERT INTO receipts VALUES($binding,$operation,$fingerprint,$result)";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$result", next);
        command.ExecuteNonQuery();
        transaction.Commit();
        if (LoseNextAcknowledgement) { LoseNextAcknowledgement = false; throw new IOException("Committed destination; acknowledgement lost."); }
        return new(WopiRevisionWriteStatus.Applied, next);
    }
    public int ReceiptCount()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM receipts";
        return Convert.ToInt32(command.ExecuteScalar());
    }
    public void RemoteWrite(byte[] bytes)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE files SET bytes=$bytes,revision=revision+1 WHERE id=$id";
        command.Parameters.AddWithValue("$bytes", bytes); command.Parameters.AddWithValue("$id", FileId); command.ExecuteNonQuery();
    }
    public Task<IWopiFile?> GetWopiFile(string identifier, CancellationToken cancellationToken = default) => Task.FromResult<IWopiFile?>(identifier == FileId ? new File(this) : null);
    public Task<IWopiWritableFile?> GetWritableFile(string identifier, CancellationToken cancellationToken = default) => Task.FromResult<IWopiWritableFile?>(identifier == FileId ? new File(this) : null);
    public Task<IWopiContainer?> GetWopiContainer(string identifier, CancellationToken cancellationToken = default) => Task.FromResult<IWopiContainer?>(identifier == "root" ? RootContainer : null);
    public async IAsyncEnumerable<IWopiFile> GetWopiFiles(string identifier, IReadOnlyCollection<string>? fileExtensions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; if (identifier == "root") yield return new File(this); }
    public async IAsyncEnumerable<IWopiContainer> GetWopiContainers(string identifier, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<ReadOnlyCollection<IWopiContainer>> GetFileAncestors(string fileId, CancellationToken cancellationToken = default) => Task.FromResult(new ReadOnlyCollection<IWopiContainer>([RootContainer]));
    public Task<ReadOnlyCollection<IWopiContainer>> GetContainerAncestors(string containerId, CancellationToken cancellationToken = default) => Task.FromResult(new ReadOnlyCollection<IWopiContainer>([]));
    public Task<IWopiFile?> GetWopiFileByName(string containerId, string name, CancellationToken cancellationToken = default) => GetWopiFile(name == "test.docx" ? FileId : "", cancellationToken);
    public Task<IWopiContainer?> GetWopiContainerByName(string containerId, string name, CancellationToken cancellationToken = default) => Task.FromResult<IWopiContainer?>(null);
    public Task<IWopiWritableFile?> CreateWopiChildFile(string containerId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IWopiContainer?> CreateWopiChildContainer(string containerId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> DeleteWopiFile(string identifier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> DeleteWopiContainer(string identifier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> RenameWopiFile(string identifier, string requestedName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> RenameWopiContainer(string identifier, string requestedName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> CheckValidFileName(string name, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> CheckValidContainerName(string name, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<string> GetSuggestedFileName(string containerId, string name, CancellationToken cancellationToken = default) => Task.FromResult(name);
    public Task<string> GetSuggestedContainerName(string containerId, string name, CancellationToken cancellationToken = default) => Task.FromResult(name);
    private sealed class Root : IWopiContainer { public string Identifier => "root"; public string Name => "root"; public long Size => 0; public int ChildCount => 1; }
    private sealed class File(SqliteRevisionStore store) : IWopiWritableFile
    {
        public string Identifier => FileId; public string Name => "test." + store.Extension; public string Owner => "coordinated"; public bool Exists => true; public string Extension => store.Extension;
        public DateTime LastWriteTimeUtc => DateTime.UnixEpoch;
        public long Length { get { var value = store.ReadAsync(FileId).GetAwaiter().GetResult(); value.Content.Dispose(); return value.Length; } }
        public string? Version { get { var value = store.ReadAsync(FileId).GetAwaiter().GetResult(); value.Content.Dispose(); return value.Revision; } }
        public ReadOnlyMemory<byte>? Checksum => null;
        public async Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => (await store.ReadAsync(FileId, cancellationToken)).Content;
        public Task<Stream> OpenWriteAsync(CancellationToken cancellationToken = default) { store.DirectWrites++; throw new InvalidOperationException("Direct stream writes bypass coordination."); }
    }
}
