using ATL;

namespace MusicRename;

public static class Program
{
    // if windows, use StringComparison.OrdinalIgnoreCase, otherwise use StringComparison.Ordinal
#if WINDOWS
    const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
#else
    const StringComparison PathComparison = StringComparison.Ordinal;
#endif

    const bool MoveAudioFilesInParallel = true;
    const bool UseAlbumArtist = true;
    const bool UseDiscSubdirectory = false;
    const bool IgnoreHiddenFolders = true;

    static readonly HashSet<string> AudioFileTypes = new()
    {
        ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".mp1", ".mp2", ".aax", ".caf",
        ".m4b", ".mp4", ".mid", ".oga", ".tak", ".bwav", ".bwf", ".vgm", ".vgz", ".wv", ".wma", ".asf"
    };

    static readonly HashSet<string> LosslessAudioFileTypes = new()
    {
        ".flac", ".wav", ".tak", ".bwav", ".bwf", ".vgm", ".vgz", ".wv"
    };

    static readonly HashSet<string> PlaylistFileTypes = new()
    {
        ".m3u", ".m3u8", ".pls", ".wpl", ".zpl", ".xspf"
    };

    static readonly List<string> IgnoreDirectories = new()
    {
        ".stfolder", ".stversions"
    };

    const bool CompressionEnabled = true;

    public static void Main(string[] args)
    {
        // get all files recursively from the path
        if (args.Length == 0 || !Directory.Exists(args[0]))
        {
            Console.WriteLine("Please provide a valid path to a music directory");
            return;
        }

        var musicDirectory = args[0];

        var invalidCharsInFileName = Path.GetInvalidFileNameChars();
        var invalidCharsInDirectoryName = invalidCharsInFileName
            .Concat(Path.GetInvalidPathChars())
            .Distinct()
            .ToArray();


        // move playlists
        var playlistDirectoryPath = Path.Combine(musicDirectory, "Playlists");
        var playlistDirectory = new DirectoryInfo(playlistDirectoryPath);
        playlistDirectory.Create();

        var files = Directory.EnumerateFiles(musicDirectory, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                string[] subdirectories = file.Split(Path.DirectorySeparatorChar);
                if (subdirectories.Length <= 0)
                {
                    return true;
                }

                if (IgnoreHiddenFolders)
                {
                    foreach (var subdirectory in subdirectories)
                    {
                        if (subdirectory.StartsWith('.'))
                            return false;
                    }
                }

                foreach (var ignoredDir in IgnoreDirectories)
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

        var audioFiles = files
            .Where(file => AudioFileTypes.Contains(file.Extension))
            .ToArray();
        var playlistFiles = files
            .Where(file => PlaylistFileTypes.Contains(file.Extension))
            .Where(file => file.Directory?.FullName != playlistDirectory.FullName)
            .ToArray();

        playlistFiles
            .AsParallel()
            .ForAll(playlist =>
            {
                var playlistPath = Path.Combine(playlistDirectoryPath, playlist.Name);
                File.Move(playlist.FullName, playlistPath, true);
                Console.WriteLine($"Moved playlist \"{playlist.Name}\" to \"{playlistPath}\"");
            });

        if (!MoveAudioFilesInParallel)
        {
            var trackJob = audioFiles
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
            audioFiles
                .AsParallel()
                .Select(LoadTrack)
                .Where(track => track is not null)
                .Select(track => track!)
                .ForAll(Organize);
        }

        Conflicts.HandleTrackConflicts(TrackConflicts);

        foreach (var movedTrackInfo in MovedTrackInfos)
        {
            MoveStrayFiles(movedTrackInfo);
        }

        FileIO.DeleteEmptyDirectories(musicDirectory, IgnoreDirectories);

        if (CompressionEnabled)
        {
            var losslessFiles = Directory.GetFiles(musicDirectory, "*", SearchOption.AllDirectories)
                .AsParallel()
                .Select(file => new FileInfo(file))
                .Where(file => LosslessAudioFileTypes.Contains(file.Extension))
                .ToArray();

            Console.WriteLine($"Found {losslessFiles.Length} lossless files to compress");

            if (losslessFiles.Length > 0)
            {
                Ffmpeg.CompressFiles(losslessFiles);
            }
        }

        Console.WriteLine($"End of program");
        return;

        void Organize(Track currentTrack)
        {
            const string pattern = "{0}. {1}{2}";
            
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
                MovedTrackInfos.Add(movedTrackInfo);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error organizing {currentTrack.Path}\n" +
                                  $"{e.Message}");
            }
        }
    }

    static void MoveStrayFiles(MovedTrackInfo movedTrackInfo)
    {
        var originalDirectory = movedTrackInfo.OriginalDirectory;
        if (!originalDirectory.Exists)
            return;

        var newDirectoryString = movedTrackInfo.NewDirectoryString;
        var trackPath = movedTrackInfo.OriginalTrackPath;

        var strayDirectories = originalDirectory
            .GetDirectories()
            .Where(directory => !string.Equals(directory.FullName, newDirectoryString, PathComparison)
                                && !IgnoreDirectories.Contains(directory.Name));

        foreach (var strayDirectory in strayDirectories)
        {
            var newDir = Path.Combine(newDirectoryString, strayDirectory.Name);

            var allRemainingFiles = strayDirectory.GetFiles("*", SearchOption.AllDirectories);
            var straysInDirectory = allRemainingFiles
                .Where(file => !AudioFileTypes.Contains(file.Extension))
                .ToArray();

            if (straysInDirectory.Length > 0)
                Directory.CreateDirectory(newDir);

            foreach (var strayFile in straysInDirectory)
            {
                File.Move(strayFile.FullName, Path.Combine(newDir, strayFile.Name));
            }

            if (allRemainingFiles.Length == straysInDirectory.Length)
            {
                FileIO.DeleteEmptyDirectories(strayDirectory.FullName, IgnoreDirectories);
            }
        }

        var strayFiles = originalDirectory.GetFiles()
            .Where(file => !AudioFileTypes.Contains(file.Extension))
            .Where(file => !string.Equals(file.FullName, trackPath, PathComparison));

        foreach (var strayFile in strayFiles)
        {
            File.Move(strayFile.FullName, Path.Combine(newDirectoryString, strayFile.Name), true);
        }

        FileIO.DeleteEmptyDirectories(originalDirectory.FullName, IgnoreDirectories);
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

        FileIO.RemoveDoubleSpaces(ref title);

        string extension = Path.GetExtension(track.Path);
        var newFileName = trackNumber is > 0
            ? string.Format(pattern, trackNumber.Value.ToString("00"), title, extension)
            : track.Title + extension;

        var cd = track.DiscNumber;
        bool hasCd = false;
        if (cd is > 0)
        {
            newFileName = $"{cd.Value:0}_{newFileName}";
            hasCd = true;
        }

        newFileName = FileIO.ReplaceInvalidCharactersInFileName(newFileName, invalidCharsInFileName);
        List<string> pathInConstruction = new(4);

        string rawArtist;
        if (UseAlbumArtist)
        {
            rawArtist = track.AlbumArtist;
            if (string.IsNullOrWhiteSpace(rawArtist))
                rawArtist = track.Artist;
        }
        else
        {
            rawArtist = track.Artist;
            if (string.IsNullOrWhiteSpace(rawArtist))
                rawArtist = track.AlbumArtist;
        }

        if (string.IsNullOrWhiteSpace(rawArtist))
            rawArtist = track.OriginalArtist;

        FileIO.TryCorrectSubdirectory(invalidCharsInDirectoryName, rawArtist, "Unknown Artist", out var artist);
        FileIO.TryCorrectSubdirectory(invalidCharsInDirectoryName, track.Album, "Unknown Album", out var album);

        var year = track.Year;
        if (year is > 1000) // ugly check for valid year
            album = $"{year} - {album}";

        string newPath = musicDirectory;

        pathInConstruction.Add(artist);
        pathInConstruction.Add(album);

        if (UseDiscSubdirectory && hasCd)
            pathInConstruction.Add($"Disc {cd!.Value:0}");

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
            var moved = FileIO.MoveFile(track.Path, newPath);
            FilePaths.Add(moved ? newPath : track.Path);
            return moved;
        }

        var conflict = new TrackConflict(track, newPath);
        TrackConflicts.Add(conflict);
        return true;
    }

    public static Track? LoadTrack(FileInfo fileInfo)
    {
        Track? track = null;
        var path = fileInfo.FullName;
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

    static readonly List<TrackConflict> TrackConflicts = new();
    static readonly List<string> FilePaths = new();
    static readonly List<MovedTrackInfo> MovedTrackInfos = new();
}