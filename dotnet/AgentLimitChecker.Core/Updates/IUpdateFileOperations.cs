namespace AgentLimitChecker.Core.Updates;
public interface IUpdateFileOperations
{
    bool FileExists(string path);

    bool DirectoryExists(string path);
    bool PathExists(string path);

    bool IsReparsePoint(string path);
    DirectoryListing? ListDirectory(string directory);
    bool MoveFile(string sourcePath, string destinationPath);
    bool CopyFile(string sourcePath, string destinationPath);
    bool DeleteFile(string path);
    bool CanOpenExclusively(string path);

    string? ReadAllText(string path);
    bool WriteNewFileAtomically(string path, string contents);
    bool ReplaceFileAtomically(string path, string contents);
    bool DeleteDirectoryTree(string path);
}
public sealed record DirectoryListing(IReadOnlyList<string> Files, IReadOnlyList<string> Directories);
