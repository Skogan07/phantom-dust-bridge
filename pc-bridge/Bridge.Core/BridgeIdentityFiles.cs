using Microsoft.Data.Sqlite;

namespace PhantomDust.PcBridge.Core;

public static class BridgePaths {
    public static string CanonicalRoot(string? userProfile = null) => Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".phantom-dust-bridge");
    public static string LockPath(string canonicalRoot) => Path.GetFullPath(canonicalRoot) + ".instance.lock";
    public static void EnsureModeIsUsable(string canonicalRoot,string mode){var marker=Path.Combine(canonicalRoot,$"migration-conflict-{mode}.txt");if(File.Exists(marker))throw new InvalidOperationException($"Legacy {mode} pairing has conflicting certificate identity; explicit repair is required before this mode can start.");}
    public static void MigrateLegacy(string legacyRoot, string canonicalRoot, bool requireLegacySource = false, Func<bool>? beforeCommit = null, string activeMode = "live") {
        legacyRoot=Path.GetFullPath(legacyRoot);canonicalRoot=Path.GetFullPath(canonicalRoot);
        if(string.Equals(legacyRoot,canonicalRoot,StringComparison.OrdinalIgnoreCase)||Directory.Exists(canonicalRoot))return;
        if(requireLegacySource && (!Directory.Exists(legacyRoot) || !Directory.EnumerateFileSystemEntries(legacyRoot).Any()))throw new DirectoryNotFoundException("The explicit legacy Bridge source does not exist or is empty.");
        var parent=Directory.GetParent(canonicalRoot)?.FullName??throw new InvalidOperationException("Canonical Bridge parent is unavailable.");Directory.CreateDirectory(parent);
        var staging=Path.Combine(parent,"."+Path.GetFileName(canonicalRoot)+".migration-"+Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(staging);
            if(Directory.Exists(legacyRoot)) {
                var sources=Directory.EnumerateFiles(legacyRoot,"*",SearchOption.AllDirectories).Where(x=>!x.EndsWith("-wal",StringComparison.OrdinalIgnoreCase)&&!x.EndsWith("-shm",StringComparison.OrdinalIgnoreCase)).ToArray();
                var dbs=sources.Where(x=>string.Equals(Path.GetFileName(x),"bridge.db",StringComparison.OrdinalIgnoreCase)).ToArray();
                var rootCert=Path.Combine(legacyRoot,"certificate.dpapi");var modeCerts=new[]{("live",Path.Combine(legacyRoot,"live","certificate.dpapi")),("demo",Path.Combine(legacyRoot,"demo","certificate.dpapi"))}.Where(x=>File.Exists(x.Item2)).ToArray();
                var certs=new[]{rootCert}.Where(File.Exists).Concat(modeCerts.Select(x=>x.Item2)).ToArray();
                if(dbs.Length>0&&certs.Length==0)throw new InvalidDataException("Legacy Bridge state has no certificate identity; refusing partial migration.");
                foreach(var source in sources){var relative=Path.GetRelativePath(legacyRoot,source);var destination=Path.Combine(staging,relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);if(string.Equals(Path.GetFileName(source),"bridge.db",StringComparison.OrdinalIgnoreCase))BackupDatabase(source,destination);else File.Copy(source,destination);}
                var selected=SelectCertificate(legacyRoot,dbs,rootCert,modeCerts,activeMode,staging);if(selected is not null){Directory.CreateDirectory(staging);File.Copy(selected,Path.Combine(staging,"certificate.dpapi"),true);}
            }
            ValidateBundle(staging);if(beforeCommit is not null&&!beforeCommit())throw new InvalidOperationException("Bridge port ownership changed during migration; canonical storage was not installed.");Directory.Move(staging,canonicalRoot);
        }catch { if(Path.GetDirectoryName(staging)==parent&&Path.GetFileName(staging).StartsWith("."+Path.GetFileName(canonicalRoot)+".migration-",StringComparison.Ordinal))try{if(Directory.Exists(staging))Directory.Delete(staging,true);}catch{} throw; }
    }
    private static string? SelectCertificate(string root,string[] dbs,string rootCert,(string Mode,string Path)[] modeCerts,string activeMode,string staging){
        var paired= dbs.Where(ReadPaired).ToArray();
        var rootExists=File.Exists(rootCert);var candidates=modeCerts;
        var pairedModes=paired.Select(x=>Path.GetFileName(Path.GetDirectoryName(x)!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var activeLocal=modeCerts.FirstOrDefault(x=>x.Mode.Equals(activeMode,StringComparison.OrdinalIgnoreCase)).Path;
        if(rootExists){var bytes=File.ReadAllBytes(rootCert);if(pairedModes.Contains(activeMode,StringComparer.OrdinalIgnoreCase)&&(activeLocal is null||!bytes.SequenceEqual(File.ReadAllBytes(activeLocal))))throw new InvalidDataException("Active paired Bridge mode has no matching certificate identity.");foreach(var mode in pairedModes.Where(x=>!x.Equals(activeMode,StringComparison.OrdinalIgnoreCase))){var local=modeCerts.FirstOrDefault(x=>x.Mode.Equals(mode,StringComparison.OrdinalIgnoreCase)).Path;if(local is not null&&!bytes.SequenceEqual(File.ReadAllBytes(local)))File.WriteAllText(Path.Combine(staging,$"migration-conflict-{mode}.txt"),"Conflicting paired legacy certificate; explicit repair required before this mode can start.");}return rootCert;}
        if(pairedModes.Contains(activeMode,StringComparer.OrdinalIgnoreCase)){if(activeLocal is null)throw new InvalidDataException("Active paired Bridge mode is missing its certificate identity.");var bytes=File.ReadAllBytes(activeLocal);foreach(var mode in pairedModes.Where(x=>!x.Equals(activeMode,StringComparison.OrdinalIgnoreCase))){var local=modeCerts.FirstOrDefault(x=>x.Mode.Equals(mode,StringComparison.OrdinalIgnoreCase)).Path;if(local is null)throw new InvalidDataException("Paired Bridge mode is missing its certificate identity.");if(!bytes.SequenceEqual(File.ReadAllBytes(local)))File.WriteAllText(Path.Combine(staging,$"migration-conflict-{mode}.txt"),"Conflicting paired legacy certificate; explicit repair required before this mode can start.");}return activeLocal;}
        return candidates.FirstOrDefault(x=>x.Mode.Equals("live",StringComparison.OrdinalIgnoreCase)).Path??candidates.FirstOrDefault(x=>x.Mode.Equals("demo",StringComparison.OrdinalIgnoreCase)).Path;
    }
    private static bool ReadPaired(string dbPath){using var db=new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText="SELECT json FROM state WHERE id=1";var value=c.ExecuteScalar()??throw new InvalidDataException("Legacy Bridge database has no state record.");using var doc=System.Text.Json.JsonDocument.Parse((string)value);if(!doc.RootElement.TryGetProperty("devices",out var devices)||devices.ValueKind!=System.Text.Json.JsonValueKind.Array)throw new InvalidDataException("Legacy Bridge state has invalid device data.");return devices.GetArrayLength()>0;}
    private static void BackupDatabase(string sourcePath,string destinationPath){using var source=new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");source.Open();using var destination=new SqliteConnection($"Data Source={destinationPath};Pooling=False");destination.Open();source.BackupDatabase(destination);}
    private static void ValidateBundle(string root){var dbs=Directory.EnumerateFiles(root,"bridge.db",SearchOption.AllDirectories).ToArray();var cert=new[]{Path.Combine(root,"certificate.dpapi"),Path.Combine(root,"live","certificate.dpapi"),Path.Combine(root,"demo","certificate.dpapi")}.Any(File.Exists);if(dbs.Length>0&&!cert)throw new InvalidDataException("Staged Bridge state has no certificate identity.");foreach(var db in dbs){using var connection=new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT 1 FROM state WHERE id=1";if(command.ExecuteScalar() is null)throw new InvalidDataException("Staged Bridge database has no state record.");}}
}
public sealed class BridgeInstanceLock : IDisposable {
    private readonly FileStream stream;private BridgeInstanceLock(FileStream stream)=>this.stream=stream;
    public static BridgeInstanceLock Acquire(string canonicalRoot){var path=BridgePaths.LockPath(canonicalRoot);Directory.CreateDirectory(Path.GetDirectoryName(path)!);try{return new(new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None));}catch(IOException){throw new InvalidOperationException("Another Phantom Dust Bridge instance owns the canonical data directory.");}}
    public void Dispose()=>stream.Dispose();
}
public static class BridgeIdentityFiles {
    public static string CertificatePath(string stableDirectory)=>Path.Combine(Path.GetFullPath(stableDirectory),"certificate.dpapi");
    public static string[] LegacyCertificatePaths(string stableDirectory,string legacyDirectory)=>new[]{Path.Combine(Path.GetFullPath(stableDirectory),"live","certificate.dpapi"),Path.Combine(Path.GetFullPath(stableDirectory),"demo","certificate.dpapi"),Path.Combine(Path.GetFullPath(legacyDirectory),"certificate.dpapi")}.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
