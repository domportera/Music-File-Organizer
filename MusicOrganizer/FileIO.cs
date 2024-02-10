namespace MusicOrganizer;

public class FileIO
{
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

    public static void DeleteEmptyDirectories(string rootDir, ICollection<string> ignoreDirectories, ICollection<string> doNotDeleteDirectories)
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

            var skip =Directory.GetFiles(directory).Length != 0 
                      || Directory.GetDirectories(directory).Length != 0
                      || doNotDeleteDirectories.Contains(directoryInfo.Name);
            
            if (skip)
                continue;

            try
            {
                directoryInfo.Delete();
                Console.WriteLine($"Deleted {directory}");
            }
            catch (IOException e)
            {
                Console.WriteLine($"Error deleting {directory}\n" +
                                  $">> {e.Message}");
            }
        }
    }

    static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    static readonly char[] InvalidPathChars = InvalidFileNameChars
        .Concat(Path.GetInvalidPathChars())
        .Distinct()
        .ToArray();
}