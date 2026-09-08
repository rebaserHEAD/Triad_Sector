using Content.Shared._NF.Shipyard.Events; // Triad: legacy import
using Content.Shared._Triad.Shipyard.Save;
using System.Threading.Tasks;
using System.Linq;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel; // Triad: legacy import

namespace Content.Client._Triad.Shipyard.Save;

public sealed partial class ShipFileManagementSystem : EntitySystem
{
    [Dependency] private IResourceManager _resourceManager = default!;
    [Dependency] private ILogManager _log = default!;

    // Static data shared across all instances to handle multiple system instances
    private static readonly Dictionary<string, string> CachedShipData = new();

    /// <summary>
    ///     Holds all file paths whitelisted for <see cref="DeleteLocalShipFileMessage"/>
    /// </summary>
    /// <remarks>
    ///     If the filepath isn't in this collection, it cannot be deleted by that message.
    ///     This prevents a rogue server from deleting non-ship-related files using path traversal trick shots
    /// </remarks>
    private static readonly List<string> DeletableShipPaths = new();

    private static readonly List<string> AvailableShips = new();
    private static event Action? ShipsUpdated;
    private static event Action<string>? ShipLoaded;
    private static bool _indexUpdateNeeded = false;
    private static DateTime _lastIndexUpdate = DateTime.MinValue;
    private static readonly TimeSpan IndexUpdateCooldown = TimeSpan.FromSeconds(1);

    private ISawmill _sawmill = default!;

    public event Action? OnShipsUpdated
    {
        add => ShipsUpdated += value;
        remove => ShipsUpdated -= value;
    }

    public event Action<string>? OnShipLoaded
    {
        add => ShipLoaded += value;
        remove => ShipLoaded -= value;
    }

    private static int _instanceCounter = 0;
    private readonly int _instanceId;

    public ShipFileManagementSystem()
    {
        _instanceId = ++_instanceCounter;
        // Reduced logging for performance
    }

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _log.GetSawmill("shipsave_file_management");

        SubscribeNetworkEvent<DeleteLocalShipFileMessage>(HandleDeleteLocalShipFile);

        // Ensure saved_ships directory exists on startup
        EnsureSavedShipsDirectoryExists();

        // Only load existing ships if we haven't already loaded them
        if (AvailableShips.Count == 0)
        {
            // Load existing saved ships from user data
            LoadExistingShips();
        }
        // Skip reload if ships already loaded by previous instance
    }

    private void EnsureSavedShipsDirectoryExists()
    {
        // Exports folder already exists, no need to create directories
    }

    public async Task<string?> GetShipYamlData(string filePath)
    {
        string? yamlData;

        // Check cache first, load from disk if needed (lazy loading)
        if (CachedShipData.TryGetValue(filePath, out yamlData))
        {
            // Data already cached
        }
        else
        {
            // Load from disk
            try
            {
                using var reader = _resourceManager.UserData.OpenText(new(filePath));
                yamlData = reader.ReadToEnd();
                CachedShipData[filePath] = yamlData;
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to load ship data from {filePath}: {ex.Message}");
                return null;
            }
        }

        await Task.CompletedTask;
        return yamlData;
    }

    /// <summary>
    /// Triad: legacy import. Describes every local save to the server so it can say which it will
    /// take: the envelope's name, its unsigned appraisal, and the signature and public key it claims.
    ///
    /// <para>No hashing here, deliberately. A signature covers a SHA-256 of the ship data and
    /// <c>System.Security.Cryptography</c> is not on the engine's sandbox whitelist, so the client
    /// cannot compute one in a packaged build. It sends the key instead and the server hashes that
    /// itself; the payload, and the real verification, follow only for the ship actually imported.</para>
    /// </summary>
    public List<DrydockImportCandidate> BuildImportManifest()
    {
        var manifest = new List<DrydockImportCandidate>();

        foreach (var path in GetSavedShipFiles())
        {
            try
            {
                using var reader = _resourceManager.UserData.OpenText(new ResPath(path));
                var node = new YamlStream();
                node.Load(reader);

                if (node.Documents.Count == 0 || node.Documents[0].RootNode is not YamlMappingNode root)
                    continue;

                manifest.Add(new DrydockImportCandidate(
                    path,
                    ExtractFileNameWithoutExtension(path),
                    int.TryParse(Scalar(root, "appraisal"), out var appraisal) ? appraisal : null,
                    B64(root, "signature"),
                    B64(root, "signaturePublicKey")));
            }
            catch (Exception ex)
            {
                // A file that will not parse is one the server could not load either. Skipping it
                // keeps a corrupt save out of the list instead of out of the whole manifest.
                _sawmill.Warning($"Skipping '{path}' while building the import manifest: {ex.Message}");
            }
        }

        return manifest;
    }

    /// <summary>
    ///     Reads one scalar through the engine's helper rather than <c>YamlMappingNode.Children</c>.
    ///     A packaged client runs sandboxed, and that sandbox whitelists the engine wholesale and
    ///     YamlDotNet's dictionary surface not at all, so reaching for Children here fails the
    ///     assembly type check and the client aborts before it loads any content at all.
    /// </summary>
    private static string? Scalar(YamlMappingNode root, string key)
    {
        return root.TryGetNode<YamlScalarNode>(key, out var scalar) ? scalar.Value : null;
    }

    /// <summary>
    ///     Decodes without letting a malformed string throw, because <c>FormatException</c> is not a
    ///     type the sandbox lets content name: the catch that would be the obvious way to write this
    ///     is the same startup-killing violation as the one above.
    /// </summary>
    private static byte[] B64(YamlMappingNode root, string key)
    {
        var raw = Scalar(root, key);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<byte>();

        // Base64 carries three bytes in every four characters and never decodes to more than that.
        var decoded = new byte[raw.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(raw, decoded, out var written))
            return Array.Empty<byte>();

        Array.Resize(ref decoded, written);
        return decoded;
    }

    // Triad start
    /// <summary>
    ///     This method whitelists a path to be acted on by <see cref="DeleteLocalShipFileMessage"/>
    /// </summary>
    public static void MarkShipPathAsDeletable(string filePath)
    {
        if (!DeletableShipPaths.Contains(filePath))
            DeletableShipPaths.Add(filePath);
    }

    /// <summary>
    ///     Tests if the given filePath was previously marked as deletable and removes it from the list if so.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    public static bool WasShipMarkedAsDeletable(string filePath)
    {
        var index = DeletableShipPaths.IndexOf(filePath);
        if (index == -1)
        {
            return false;
        }
        else
        {
            DeletableShipPaths.RemoveAt(index);
            return true;
        }
    }

    // Triad end

    private void LoadExistingShips()
    {
        try
        {
            _sawmill.Info($"Instance #{_instanceId}: Attempting to find saved ship files...");

            // Try UserData.Find to enumerate all .yml files
            var (ymlFiles, directories) = _resourceManager.UserData.Find("*.yml", recursive: true);

            var ymlFilesList = ymlFiles.ToList();
            _sawmill.Info($"Instance #{_instanceId}: Found {ymlFilesList.Count.ToString()} .yml files total");

            foreach (var file in ymlFiles)
            {
                var filePath = file.ToString();

                // Accept any .yml file in Exports (not just ship_index), but exclude backups
                if (filePath.Contains("Exports")
                    && !filePath.Contains("Exports/backup")
                    && filePath.EndsWith(".yml")
                    && !filePath.Contains("ship_index"))
                {
                    if (!AvailableShips.Contains(filePath))
                        AvailableShips.Add(filePath);
                }
            }

            _sawmill.Debug($"Instance #{_instanceId}: Final result: Loaded {AvailableShips.Count} saved ships from Exports directory");

            // Trigger UI update
            ShipsUpdated?.Invoke();
        }
        catch (NotImplementedException)
        {
            // In test environments, the Find method may not be implemented
            // This is expected and should not cause test failures
            _sawmill.Debug($"Instance #{_instanceId}: Ship file enumeration not available in test environment");
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Instance #{_instanceId}: Failed to load existing ships: {ex.Message}");
        }
    }

    private void UpdateShipIndex()
    {
        try
        {
            // Rate limit index updates
            var now = DateTime.Now;
            if (!_indexUpdateNeeded || (now - _lastIndexUpdate) < IndexUpdateCooldown)
                return;

            var indexContent = string.Join('\n', AvailableShips);
            using var writer = _resourceManager.UserData.OpenWriteText(new("/Exports/ship_index.txt"));
            writer.Write(indexContent);

            _indexUpdateNeeded = false;
            _lastIndexUpdate = now;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to update ship index: {ex.Message}");
        }
    }

    // Useful for gathering fields inside of a ship YML file, like the stored appraisal value
    public string GetKeyValueFromPath(string filePath, string key)
    {
        using var reader = _resourceManager.UserData.OpenText(new(filePath));
        var content = reader.ReadToEnd();

        // lazy loading
        var lines = content.Split('\n');
        var val = lines.FirstOrDefault(l => l.Trim().StartsWith($"{key}:"))?.Split(':')[1].Trim() ?? "Unknown";

        return val;
    }

    // Update ship index periodically instead of on every change
    public void FlushPendingIndexUpdates()
    {
        if (_indexUpdateNeeded)
        {
            UpdateShipIndex();
        }
    }

    public List<string> GetSavedShipFiles()
    {
        return new List<string>(AvailableShips);
    }

    public static bool HasShipData(string shipName)
    {
        return CachedShipData.ContainsKey(shipName);
    }

    public static string? GetShipData(string shipName)
    {
        return CachedShipData.TryGetValue(shipName, out var data) ? data : null;
    }

    /// <summary>
    ///     Handles the deletion of the client's local ship file, called by the server.
    ///     The message's file path is checked against <see cref="DeletableShipPaths"/> to ensure the server can only delete valid ship files.
    /// </summary>
    private void HandleDeleteLocalShipFile(DeleteLocalShipFileMessage message)
    {
        try
        {
            // Triad start
            // We only allow the server to delete files that we have previously sent to the server
            if (!WasShipMarkedAsDeletable(message.FilePath))
            {
                _sawmill.Warning($"Server asked to move local file '{message.FilePath}' that was not previously loaded");
                return;
            }
            // Triad end

            // Move the loaded ship file into /Exports/backup instead of deleting.
            var originalPath = new ResPath(message.FilePath);
            if (_resourceManager.UserData.Exists(originalPath))
            {
                // Ensure backup directory exists
                var backupDir = new ResPath("/Exports/backup");
                _resourceManager.UserData.CreateDir(backupDir);

                // Compute destination file path under backup directory
                var fileName = ExtractFileNameWithoutExtension(message.FilePath);
                // Reconstruct original extension (assumed .yml)
                var destBase = new ResPath($"/Exports/backup/{fileName}");
                var destinationPath = new ResPath(destBase.ToString() + ".yml");

                // If a file with the same name already exists in backup, append a timestamp
                if (_resourceManager.UserData.Exists(destinationPath))
                {
                    var timestamped = new ResPath($"/Exports/backup/{fileName}_loaded_{DateTime.Now:yyyyMMdd_HHmmss}.yml");
                    destinationPath = timestamped;
                }

                // Triad start
                // Timestamp uniqueness is not something programmers can trust.
                // If we still don't have an unused path, we give up
                if (_resourceManager.UserData.Exists(destinationPath))
                {
                    _sawmill.Warning($"Failed to move local file '{message.FilePath}'. Could not generate safe backup path");
                }
                else
                {
                    // Originally opened the files as text
                    // Now we open them as bytes
                    using (var reader = _resourceManager.UserData.OpenRead(originalPath))
                    {
                        var writer = _resourceManager.UserData.OpenWrite(destinationPath);
                        reader.CopyTo(writer);
                    }

                    // Delete original file
                    _resourceManager.UserData.Delete(originalPath);
                    _sawmill.Info($"Moved local ship file to backup: {message.FilePath} -> {destinationPath}");
                }
                // Triad end
            }

            // Remove original entry from caches and list (do not add backup to menu)
            CachedShipData.Remove(message.FilePath);
            AvailableShips.Remove(message.FilePath);

            // Mark index update and notify UI
            _indexUpdateNeeded = true;
            ShipsUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to move local ship file '{message.FilePath}' to backup: {ex.Message}");
        }
    }

    private static string ExtractFileNameWithoutExtension(string filePath)
    {
        var fileName = filePath;
        var lastSlash = filePath.LastIndexOf('/');
        if (lastSlash >= 0)
            fileName = filePath.Substring(lastSlash + 1);
        var lastBackslash = fileName.LastIndexOf('\\');
        if (lastBackslash >= 0)
            fileName = fileName.Substring(lastBackslash + 1);
        var lastDot = fileName.LastIndexOf('.');
        if (lastDot >= 0)
            fileName = fileName.Substring(0, lastDot);
        return fileName;
    }
}
