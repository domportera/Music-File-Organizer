using ATL;

namespace MusicRename;

public static class Program
{
    static readonly bool UseParallel = false;

    // if windows, use StringComparison.OrdinalIgnoreCase, otherwise use StringComparison.Ordinal
#if WINDOWS
    const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
#else
    const StringComparison PathComparison = StringComparison.Ordinal;
#endif

    static readonly HashSet<string> AudioFileTypes = new()
    {
        ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".mp1", ".mp2", ".aax", ".caf",
        ".m4b", ".mp4", ".mid", ".oga", ".tak", ".bwav", ".bwf", ".vgm", ".vgz", ".wv", ".wma", ".asf"
    };

    public static void Main(string[] args)
    {
        // get all files recursively from the path
        if (args.Length == 0 || !Directory.Exists(args[0]))
        {
            Console.WriteLine("Please provide a valid path to a music directory");
            return;
        }

        var musicDirectory = args[0];
        var files = Directory.GetFiles(musicDirectory, "*", SearchOption.AllDirectories);
        var invalidCharsInFileName = Path.GetInvalidFileNameChars();
        var invalidCharsInDirectoryName = invalidCharsInFileName
            .Concat(Path.GetInvalidPathChars())
            .Distinct()
            .ToArray();

        const string pattern = "{0}. {1}{2}";

        if (!UseParallel)
        {
            var trackJob = files
                .Where(file => AudioFileTypes.Contains(Path.GetExtension(file)))
                .Select(LoadTrack)
                .Where(track => track is not null)
                .Select(track => track!);

            foreach (var currentTrack in trackJob)
            {
                Organize(currentTrack);
            }
        }
        else
        {
            files
                .AsParallel()
                .Where(file => AudioFileTypes.Contains(Path.GetExtension(file)))
                .Select(LoadTrack)
                .Where(track => track is not null)
                .Select(track => track!)
                .ForAll(Organize);
        }

        HandleTrackConflicts();

        foreach (var movedTrackInfo in _movedTrackInfos)
        {
            MoveStrayFiles(movedTrackInfo);
        }

        DeleteEmptyDirectories(musicDirectory);
        return;

        void Organize(Track currentTrack)
        {
            try
            {
                var organized = OrganizeTrack(musicDirectory, currentTrack, pattern, invalidCharsInFileName,
                    invalidCharsInDirectoryName,
                    out Track track, out DirectoryInfo? newDirectory);

                if (!organized)
                    return;

                var originalDirectoryString = Path.GetDirectoryName(track.Path);

                if (originalDirectoryString is null)
                    return;

                var originalDirectory = new DirectoryInfo(originalDirectoryString);
                var newDirectoryString = newDirectory!.FullName;
                var movedTrackInfo = new MovedTrackInfo(originalDirectory, newDirectoryString, track.Path);
                _movedTrackInfos.Add(movedTrackInfo);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error organizing {currentTrack.Path}\n" +
                                  $"{e.Message}");
            }
        }
    }

    static void HandleTrackConflicts()
    {
        List<TrackConflict> uniqueConflicts = new(_trackConflicts.Count);
        // remove duplicate conflicts without using hashcode
        for (int i = _trackConflicts.Count - 1; i >= 0; i--)
        {
            var unique = true;
            for (int j = i - 1; j >= 0; j--)
            {
                if (_trackConflicts[i] == _trackConflicts[j])
                {
                    unique = false;
                    break;
                }
            }

            if (unique)
            {
                uniqueConflicts.Add(_trackConflicts[i]);
            }
        }

        foreach (var conflict in _trackConflicts)
        {
            ReplaceOrDeleteFile(conflict);
        }
    }

    static void MoveStrayFiles(MovedTrackInfo movedTrackInfo)
    {
        var originalDirectory = movedTrackInfo.OriginalDirectory;
        var newDirectoryString = movedTrackInfo.NewDirectoryString;
        var trackPath = movedTrackInfo.OriginalTrackPath;
        var strayDirectories = originalDirectory
            .GetDirectories()
            .Where(directory => !string.Equals(directory.FullName, newDirectoryString, PathComparison));

        foreach (var strayDirectory in strayDirectories)
        {
            var newDir = Path.Combine(newDirectoryString, strayDirectory.Name);
            var straysInDirectory = strayDirectory.GetFiles();
            foreach (var strayFile in straysInDirectory)
            {
                File.Move(strayFile.FullName, Path.Combine(newDir, strayFile.Name));
            }

            strayDirectory.Delete();
        }

        var strayFiles = originalDirectory.GetFiles()
            .Where(file => !AudioFileTypes.Contains(file.Extension))
            .Where(file => !string.Equals(file.FullName, trackPath, PathComparison));

        foreach (var strayFile in strayFiles)
        {
            File.Move(strayFile.FullName, Path.Combine(newDirectoryString, strayFile.Name));
        }
    }

    static void DeleteEmptyDirectories(string rootDir)
    {
        var directories = Directory.GetDirectories(rootDir, "*", SearchOption.AllDirectories)
            .OrderByDescending(directory => directory.Length);

        foreach (var directory in directories)
        {
            try
            {
                if (Directory.GetDirectories(directory).Length > 0)
                {
                    DeleteEmptyDirectories(directory);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error querying directory \"{directory}\"\n" +
                                  $"{e.Message}");
                continue;
            }

            if (Directory.GetFiles(directory).Length != 0 || Directory.GetDirectories(directory).Length != 0)
                continue;

            try
            {
                Directory.Delete(directory);
                Console.WriteLine($"Deleted {directory}");
            }
            catch (IOException e)
            {
                Console.WriteLine($"Error deleting {directory}\n" +
                                  $">> {e.Message}");
            }
        }
    }

    static bool OrganizeTrack(string musicDirectory, Track currentTrack, string pattern, char[] invalidCharsInFileName,
        char[] invalidCharsInDirectoryName, out Track track, out DirectoryInfo? newDirectory)
    {
        track = currentTrack;

        var trackNumber = track.TrackNumber;
        var title = track.Title;
        var titleSpan = track.Title.AsSpan();

        // remove erroneous track numbers prefixed to the title, even if repeating like "01 - 01 - 01 - Title"
        // this can occur when the track has no title metadata and this application is run multiple times
        if (Path.GetFileNameWithoutExtension(track.Path) == track.Title)
        {
            while (titleSpan.Length > 0 && !char.IsLetter(titleSpan[0]))
            {
                titleSpan = titleSpan[1..];
            }

            title = titleSpan.ToString();
        }

        RemoveDoubleSpaces(ref title);

        string extension = Path.GetExtension(track.Path);
        var newFileName = trackNumber is > 0
            ? string.Format(pattern, trackNumber.Value.ToString("00"), title, extension)
            : track.Title + extension;

        newFileName = ReplaceInvalidCharactersInFileName(newFileName, invalidCharsInFileName);
        string[] pathInConstruction = new string[2];

        TryCorrectSubdirectory(invalidCharsInDirectoryName, track.Artist, "Unknown Artist", out var artist);
        TryCorrectSubdirectory(invalidCharsInDirectoryName, track.Album, "Unknown Album", out var album);

        var year = track.Year;
        if (year is > 1000) // ugly check for valid year
            album = $"{year} - {album}";

        string newPath = musicDirectory;

        pathInConstruction[0] = artist;
        pathInConstruction[1] = album;

        newDirectory = null;
        foreach (var path in pathInConstruction)
        {
            newPath = Path.Combine(newPath, path);
            newDirectory = Directory.CreateDirectory(newPath);
        }

        newPath = Path.Combine(newPath, newFileName);

        if (string.Equals(track.Path, newPath, PathComparison))
        {
            newDirectory = null;
            return false;
        }

        if (!File.Exists(newPath))
        {
            return MoveFile(track.Path, newPath);
        }

        var conflict = new TrackConflict(track, newPath);
        _trackConflicts.Add(conflict);
        return true;
    }

    static bool MoveFile(string srcPath, string destinationPath)
    {
        try
        {
            File.Move(srcPath, destinationPath);
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

    static bool ReplaceOrDeleteFile(TrackConflict conflict)
    {
        var track = conflict.Track;
        var destinationPath = conflict.ExistingTrackPath;
        try
        {
            var existingTrack = LoadTrack(destinationPath);
            if (existingTrack == null)
                throw new Exception($"Failed to load existing track at {destinationPath}");

            return ChooseBestQuality(track, existingTrack);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error choosing best quality for {destinationPath}:\n{e}");
            return false;
        }

        static bool ChooseBestQuality(Track track, Track existingTrack)
        {
            var trackPath = existingTrack.Path;
            if (track.Duration != existingTrack.Duration)
            {
                throw new Exception($"Identical tracks have different durations:\n" +
                                    GetTrackString(track) + GetTrackString(existingTrack));
            }
            
            if (existingTrack.Bitrate < track.Bitrate)
            {
                File.Move(track.Path, trackPath, true);
                Console.WriteLine(
                    $"Replaced lower quality track ({existingTrack.Bitrate}kbps vs {track.Bitrate}kbps)\n" +
                    $"------> {trackPath}");
                return true;
            }

            // if the existing track is better quality, delete the new one
            File.Delete(track.Path);
            return false;
        }
    }

    static bool TryCorrectSubdirectory(char[] invalidCharsInDirectoryName, string album, string defaultName,
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

    static void RemoveDoubleSpaces(ref string title)
    {
        while (title.Contains("  "))
        {
            title = title.Replace("  ", " ");
        }
    }

    static string ReplaceInvalidCharactersInFileName(string fileName, char[] invalidChars)
    {
        var nameSpan = fileName.AsSpan();
        foreach (var invalidChar in invalidChars)
        {
            if (nameSpan.Contains(invalidChar))
                fileName = fileName.Replace(invalidChar, '-');
        }

        return fileName;
    }

    static string ReplaceInvalidCharactersInDirectoryName(string directoryName, char[] invalidChars)
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

    static Track? LoadTrack(string path)
    {
        Track? track = null;
        try
        {
            track = new Track(path, true);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to create track for {path}:\n{e}");
        }

        return track;
    }

    static string GetTrackString(Track track)
    {
        return $"\"{track.Artist} - {track.Album} - {track.TrackNumber}.{track.Title}\" ({track.Path})\n";
    }

    readonly struct MovedTrackInfo
    {
        public readonly DirectoryInfo OriginalDirectory;
        public readonly string NewDirectoryString;
        public readonly string OriginalTrackPath;

        public MovedTrackInfo(DirectoryInfo originalDirectory, string newDirectoryString, string originalTrackPath)
        {
            OriginalDirectory = originalDirectory;
            NewDirectoryString = newDirectoryString;
            OriginalTrackPath = originalTrackPath;
        }
    }

    readonly struct TrackConflict
    {
        public readonly Track Track;
        public readonly string ExistingTrackPath;

        public TrackConflict(Track track, string existingTrackPath)
        {
            Track = track;
            ExistingTrackPath = existingTrackPath;
        }

        public static bool operator ==(TrackConflict left, TrackConflict right)
        {
            return left.Track.Path == right.ExistingTrackPath || right.Track.Path == left.ExistingTrackPath;
        }

        public static bool operator !=(TrackConflict left, TrackConflict right) => !(left == right);

        public bool Equals(TrackConflict other) => this == other;
        public override bool Equals(object? obj) => obj is TrackConflict other && this == other;
    }

    static readonly List<TrackConflict> _trackConflicts = new();
    static readonly List<MovedTrackInfo> _movedTrackInfos = new();
}