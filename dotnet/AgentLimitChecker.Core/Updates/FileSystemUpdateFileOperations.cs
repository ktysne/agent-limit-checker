using System.Text;

namespace AgentLimitChecker.Core.Updates;
public sealed class FileSystemUpdateFileOperations : IUpdateFileOperations
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool PathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return true;
        }
    }

    public bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return true;
        }
    }

    public DirectoryListing? ListDirectory(string directory)
    {
        try
        {
            var files = new List<string>();
            var directories = new List<string>();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo)
                {
                    directories.Add(entry.Name);
                }
                else
                {
                    files.Add(entry.Name);
                }
            }

            return new DirectoryListing(files, directories);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return null;
        }
    }

    public bool MoveFile(string sourcePath, string destinationPath) =>
        Try(() => File.Move(sourcePath, destinationPath, overwrite: false));

    public bool CopyFile(string sourcePath, string destinationPath)
    {
        FileStream destination;
        try
        {
            destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return false;
        }

        try
        {
            using (destination)
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            return true;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            Try(() => File.Delete(destinationPath));
            return false;
        }
    }

    public bool DeleteFile(string path) => Try(() =>
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.Delete(path);
    });

    public bool CanOpenExclusively(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        return Try(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    public string? ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return null;
        }
    }

    public bool WriteNewFileAtomically(string path, string contents) =>
        !PathExists(path) && WriteViaTemporaryFile(path, contents, overwrite: false);

    public bool ReplaceFileAtomically(string path, string contents) =>
        WriteViaTemporaryFile(path, contents, overwrite: true);

    private static bool WriteViaTemporaryFile(string path, string contents, bool overwrite)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8WithoutBom.GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite);
            return true;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            Try(() => File.Delete(temporary));
            return false;
        }
    }

    public bool DeleteDirectoryTree(string path)
    {
        if (!Directory.Exists(path) || IsReparsePoint(path))
        {
            return false;
        }

        return Try(() => DeleteTreeWithoutFollowingLinks(new DirectoryInfo(path)));
    }
    private static void DeleteTreeWithoutFollowingLinks(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            if (entry is DirectoryInfo child)
            {
                if (isLink)
                {
                    child.Delete(recursive: false);
                }
                else
                {
                    DeleteTreeWithoutFollowingLinks(child);
                }

                continue;
            }

            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            entry.Delete();
        }

        directory.Delete(recursive: false);
    }

    private static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return false;
        }
    }

    private static bool IsIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;
}
