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

    public static bool TryCorrectSubdirectory(char[] invalidCharsInDirectoryName, string album, string defaultName,
        out string dir)
    {
        if (string.IsNullOrWhiteSpace(album))
        {
            dir = defaultName;
            return false;
        }

        dir = ReplaceInvalidCharactersInDirectoryName(album, invalidCharsInDirectoryName);
        dir = dir.Trim();
        RemoveDoubleSpaces(ref album);
        return true;
    }

    public static void RemoveDoubleSpaces(ref string title)
    {
        while (title.Contains("  "))
        {
            title = title.Replace("  ", " ");
        }
    }

    public static string ReplaceInvalidCharactersInFileName(string fileName, char[] invalidChars)
    {
        var nameSpan = fileName.AsSpan();
        foreach (var invalidChar in invalidChars)
        {
            if (nameSpan.Contains(invalidChar))
                fileName = fileName.Replace(invalidChar, '-');
        }

        return fileName;
    }

    public static string ReplaceInvalidCharactersInDirectoryName(string directoryName, char[] invalidChars)
    {
        var nameSpan = directoryName.AsSpan();
        foreach (var invalidChar in invalidChars)
        {
            if (nameSpan.Contains(invalidChar))
                directoryName = directoryName.Replace(invalidChar, '_');
        }

        directoryName = directoryName
            .Replace("; ", ", ")
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
}