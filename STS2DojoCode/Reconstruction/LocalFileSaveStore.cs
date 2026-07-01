using System;
using System.IO;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Dojo.STS2DojoCode.Reconstruction;

/// <summary>
/// Minimal read-only <see cref="ISaveStore"/> over the OS filesystem, used to hand arbitrary absolute
/// <c>.run</c> file paths (e.g. the analysis corpus in <c>runfiles/</c>, or a real profile's history dir)
/// to the game's own <c>MigrationManager</c>. All mutating members throw — this must never be able to
/// touch a player's actual save/history files, only read them.
/// </summary>
public class LocalFileSaveStore : ISaveStore
{
    public string? ReadFile(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public Task<string?> ReadFileAsync(string path)
    {
        return Task.FromResult(ReadFile(path));
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string[] GetFilesInDirectory(string directoryPath) => Directory.GetFiles(directoryPath);

    public string[] GetDirectoriesInDirectory(string directoryPath) => Directory.GetDirectories(directoryPath);

    public DateTimeOffset GetLastModifiedTime(string path) => File.GetLastWriteTimeUtc(path);

    public int GetFileSize(string path) => (int)new FileInfo(path).Length;

    public string GetFullPath(string filename) => Path.GetFullPath(filename);

    public void WriteFile(string path, string content) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");

    public void WriteFile(string path, byte[] content) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");

    public Task WriteFileAsync(string path, string content) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");

    public Task WriteFileAsync(string path, byte[] content) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");

    public void DeleteFile(string path) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");

    public void RenameFile(string sourcePath, string destinationPath) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");

    public void CreateDirectory(string directoryPath) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");

    public void DeleteDirectory(string directoryPath) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");

    public void DeleteTemporaryFiles(string directoryPath) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");

    public void SetLastModifiedTime(string path, DateTimeOffset time) =>
        throw new NotSupportedException("LocalFileSaveStore is read-only.");
}
