namespace MusicOrganizer;

public class FileIO
{
    // if windows, use StringComparison.OrdinalIgnoreCase, otherwise use StringComparison.Ordinal
#if WINDOWS
    const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
#else
    public const StringComparison PathComparison = StringComparison.Ordinal;
#endif
    
    public static bool MoveFile(string srcPath, string destinationPath, bool overwrite = false)
    {
        try
        {
            File.Move(srcPath, destinationPath, overwrite);
            Console.WriteLine($"Moved \"{srcPath}\"\n------> \"{destinationPath}\"");
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"\n\n!!!!!!!!!!Error moving {srcPath} to {destinationPath}!!!!!!!!!!!!\n" +
                              $"{e.Message}\n\n");

            return false;
        }
    }

    public static bool TryCorrectSubdirectory(string? preferredName, string defaultName,
        out string dir)
    {
        if (string.IsNullOrWhiteSpace(preferredName))
        {
            dir = defaultName;
            return false;
        }

        ValidateSubdirectory(preferredName, out dir);
        return true;
    }

    public static void ValidateSubdirectory(string name, out string validated)
    {
        RemoveDoubleSpaces(ref name);
        validated = ReplaceInvalidCharactersInDirectoryName(name).Trim();
    }

    public static void RemoveDoubleSpaces(ref string title)
    {
        while (title.Contains("  "))
        {
            title = title.Replace("  ", " ");
        }
    }

    public static string ReplaceInvalidCharactersInFileName(string fileName)
    {
        var nameSpan = fileName.AsSpan();
        foreach (var invalidChar in InvalidFileNameChars)
        {
            if (nameSpan.Contains(invalidChar))
                fileName = fileName.Replace(invalidChar, '-');
        }

        return fileName;
    }

    public static string ReplaceInvalidCharactersInDirectoryName(string directoryName)
    {
        var nameSpan = directoryName.AsSpan();
        foreach (var invalidChar in InvalidPathChars)
        {
            if (nameSpan.Contains(invalidChar))
                directoryName = directoryName.Replace(invalidChar, '_');
        }

        directoryName = directoryName
            .Replace(';', ',')
            .Replace("..", ".");

        while (directoryName.EndsWith('.'))
            directoryName = directoryName.Substring(0, directoryName.Length - 1);


        return directoryName;
    }

    public static void DeleteEmptyDirectories(string rootDir, IReadOnlyCollection<string> ignoreDirectories,
        IReadOnlyCollection<string> doNotDeleteDirectories)
    {
        var directories = Directory.GetDirectories(rootDir, "*", SearchOption.AllDirectories)
            .OrderByDescending(directory => directory.Length);

        foreach (var directory in directories)
        {
            var directoryInfo = new DirectoryInfo(directory);
            if (ignoreDirectories.Contains(directoryInfo.Name))
                continue;

            try
            {
                if (Directory.GetDirectories(directory).Length > 0)
                {
                    DeleteEmptyDirectories(directory, ignoreDirectories, doNotDeleteDirectories);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error querying directory \"{directory}\"\n" +
                                  $"{e.Message}");
                continue;
            }

            var skip = Directory.GetFiles(directory).Length != 0
                       || Directory.GetDirectories(directory).Length != 0
                       || doNotDeleteDirectories.Contains(directoryInfo.Name);

            if (skip)
                continue;

            try
            {
                directoryInfo.Delete();
                Console.WriteLine($"Deleted empty directory: {directory}");
            }
            catch (IOException e)
            {
                Console.WriteLine($"Error deleting {directory}\n" +
                                  $">> {e.Message}");
            }
        }
    }

    public static FileInfo[] FindAllFiles(string musicDirectory, bool ignoreHiddenFolders, IReadOnlyCollection<string> ignoreDirectories)
    {
        return Directory.EnumerateFiles(musicDirectory, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                string[] subdirectories = file.Split(Path.DirectorySeparatorChar);
                if (subdirectories.Length <= 0)
                {
                    return true;
                }

                if (ignoreHiddenFolders)
                {
                    foreach (var subdirectory in subdirectories)
                    {
                        if (subdirectory.StartsWith('.'))
                            return false;
                    }
                }

                foreach (var ignoredDir in ignoreDirectories)
                {
                    foreach (var subdirectory in subdirectories)
                    {
                        if (subdirectory.Equals(ignoredDir, PathComparison))
                            return false;
                    }
                }

                return true;
            })
            .Select(x => new FileInfo(x))
            .ToArray();
    }

    public static void MoveFilesInto(string directory, FileInfo[] files)
    {
        Directory.CreateDirectory(directory);
        files
            .AsParallel()
            .ForAll(file =>
            {
                var destinationPath = Path.Combine(directory, file.Name);
                File.Move(file.FullName, destinationPath, true);
                Console.WriteLine($"Moved playlist \"{file.Name}\" to \"{destinationPath}\"");
            });
    }

    static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    static readonly char[] InvalidPathChars = InvalidFileNameChars
        .Concat(Path.GetInvalidPathChars())
        .Distinct()
        .ToArray();
}