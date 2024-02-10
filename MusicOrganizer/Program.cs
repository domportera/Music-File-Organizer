using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.Serialization.Json;
using ATL;

namespace MusicOrganizer;

public static class Program
{
    // if windows, use StringComparison.OrdinalIgnoreCase, otherwise use StringComparison.Ordinal
#if WINDOWS
    const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
#else
    const StringComparison PathComparison = StringComparison.Ordinal;
#endif

    const bool UseAlbumArtist = true;
    const bool UseDiscSubdirectory = false;
    const bool IgnoreHiddenFolders = true;

    static readonly FrozenSet<string> IgnoreDirectories = new HashSet<string>
    {
        ".stfolder", ".stversions"
    }.ToFrozenSet();

    static readonly FrozenSet<string> DoNotDeleteDirectories = new HashSet<string>
    {
        "slskd"
    }.ToFrozenSet();

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

        // move playlists
        var playlistDirectoryPath = Path.Combine(musicDirectory, "Playlists");
        var playlistDirectory = new DirectoryInfo(playlistDirectoryPath);
        playlistDirectory.Create();

        var files = FindAllFiles(musicDirectory);

        TrackLoader.FindAudioAndPlaylistsIn(files, out var audioFiles, out var playlistFiles);

        playlistFiles
            .ToArray()
            .AsParallel()
            .ForAll(playlist =>
            {
                var playlistPath = Path.Combine(playlistDirectoryPath, playlist.Name);
                File.Move(playlist.FullName, playlistPath, true);
                Console.WriteLine($"Moved playlist \"{playlist.Name}\" to \"{playlistPath}\"");
            });

        var tracks = audioFiles
            .AsParallel()
            .Select(TrackLoader.LoadTrack)
            .Where(track => track is not null)
            .Select(track => track!)
            .ToArray();

        BeginOrganization(tracks, musicDirectory);

        Conflicts.HandleTrackConflicts(TrackConflicts);

        foreach (var movedTrackInfo in MovedTrackInfos)
        {
            MoveStrayFiles(movedTrackInfo);
        }

        FileIO.DeleteEmptyDirectories(musicDirectory, IgnoreDirectories, DoNotDeleteDirectories);

        if (CompressionEnabled)
        {
            TrackLoader.FindLosslessFilesIn(musicDirectory, out var losslessFiles);
            Console.WriteLine($"Found {losslessFiles.Length} lossless files to compress");

            if (losslessFiles.Length > 0)
            {
                Ffmpeg.CompressFiles(losslessFiles);
            }
        }

        Console.WriteLine("End of program");
    }

    static void BeginOrganization(Track[] tracks, string musicDirectory)
    {
        Dictionary<string, List<Track>> tracksByAlbum = new();
        foreach (var file in tracks)
        {
            if (!tracksByAlbum.TryGetValue(file.Album, out var albumTracks))
            {
                albumTracks = new List<Track>(16);
                var album = file.Album ?? string.Empty;
                tracksByAlbum.Add(album, albumTracks);
            }

            albumTracks.Add(file);
        }

        tracksByAlbum
            .AsParallel()
            .ForAll(albumKvp => OrganizeAlbum(albumKvp.Value, albumKvp.Key, musicDirectory));
    }

    static FileInfo[] FindAllFiles(string musicDirectory)
    {
        return Directory.EnumerateFiles(musicDirectory, "*", SearchOption.AllDirectories)
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
    }

    static void Organize(Track currentTrack, string musicDirectory, string album, bool useArtistSubdirectory,
        string artistName, int totalDiscCount)
    {
        try
        {
            var organized = OrganizeTrack(musicDirectory, currentTrack, album, useArtistSubdirectory,
                artistName, totalDiscCount,
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

    static void OrganizeAlbum(List<Track> album, string albumName, string musicRootDirectory)
    {
        string[] artists;
        int totalDiscCount = 1;
        artists = album
            .Select(track =>
            {
                string artist;
                if (UseAlbumArtist)
                {
                    artist = track.AlbumArtist;
                    if (string.IsNullOrWhiteSpace(artist))
                        artist = track.Artist;
                }
                else
                {
                    artist = track.Artist;
                    if (string.IsNullOrWhiteSpace(artist))
                        artist = track.AlbumArtist;
                }

                if (string.IsNullOrWhiteSpace(artist))
                    artist = track.OriginalArtist;

                if (string.IsNullOrWhiteSpace(artist))
                    artist = "Unknown Artist";

                // while we're iterating, check if the album is multi-disc
                var discNo = track.DiscNumber ?? 1;
                if (discNo > totalDiscCount)
                    totalDiscCount = discNo;
                
                return artist;
            })
            .ToArray();

        string mostFrequentArtist;
        bool useArtistSubdirectory = true;
        if (artists.Length == 1)
        {
            mostFrequentArtist = artists[0];
        }
        else
        {
            var artistGroups = artists.GroupBy(x => x).ToArray();

            // get the primary artist of the album if there is one.
            mostFrequentArtist = artistGroups
                .OrderByDescending(x => x.Count())
                .Select(x => x.Key)
                .First();

            // if it appears to be more than one artist consistently,
            // set useArtistSubdirectory to false
            useArtistSubdirectory = artistGroups
                .Count(x => x.Count() > 1) > 1;
        }

        foreach (var track in album)
        {
            Organize(track, musicRootDirectory, albumName, useArtistSubdirectory, mostFrequentArtist, totalDiscCount);
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
                .Where(file => !TrackLoader.IsAudioFile(file))
                .ToArray();

            if (straysInDirectory.Length > 0)
                Directory.CreateDirectory(newDir);

            foreach (var strayFile in straysInDirectory)
            {
                File.Move(strayFile.FullName, Path.Combine(newDir, strayFile.Name));
            }

            if (allRemainingFiles.Length == straysInDirectory.Length)
            {
                FileIO.DeleteEmptyDirectories(strayDirectory.FullName, IgnoreDirectories, DoNotDeleteDirectories);
            }
        }

        var strayFiles = originalDirectory.GetFiles()
            .Where(file => !TrackLoader.IsAudioFile(file) &&
                           !string.Equals(file.FullName, trackPath, PathComparison));

        foreach (var strayFile in strayFiles)
        {
            File.Move(strayFile.FullName, Path.Combine(newDirectoryString, strayFile.Name), true);
        }

        FileIO.DeleteEmptyDirectories(originalDirectory.FullName, IgnoreDirectories, DoNotDeleteDirectories);
    }

    static bool OrganizeTrack(string musicDirectory, Track currentTrack,
        string album,
        bool useArtistSubdirectory,
        string artistName,
        int totalDiscCount,
        out Track track,
        out DirectoryInfo? newDirectory)
    {
        const string pattern = "{0}. {1}{2}";
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
        List<string> pathInConstruction = new(4);

        FileIO.TryCorrectSubdirectory(album, "Unknown Album", out var albumDir);

        var year = track.Year;
        if (year is > 1000) // ugly check for valid year
            album = $"{year} - {album}";

        string newPath = musicDirectory;

        if (useArtistSubdirectory)
        {
            FileIO.ValidateSubdirectory(artistName, out var artistSubdirectory);
            pathInConstruction.Add(artistSubdirectory);
        }

        pathInConstruction.Add(albumDir);

        bool needsMetadataSave = false;

        if (totalDiscCount > 1)
        {
            if (track.DiscNumber is null or < 1)
            {
                track.DiscNumber = 1;
                needsMetadataSave = true;
            }

            if (track.DiscTotal is null or < 1)
            {
                track.DiscTotal = totalDiscCount;
                needsMetadataSave = true;
            }

            var discNumber = track.DiscNumber;
            newFileName = $"{discNumber:0}_{newFileName}";

            if (UseDiscSubdirectory)
                pathInConstruction.Add($"Disc {discNumber:0}");
        }

        newFileName = FileIO.ReplaceInvalidCharactersInFileName(newFileName);

        newDirectory = null;
        foreach (var path in pathInConstruction)
        {
            newPath = Path.Combine(newPath, path);
            newDirectory = Directory.CreateDirectory(newPath);
        }

        newPath = Path.Combine(newPath, newFileName);
        
        if(needsMetadataSave)
        {
            track.Save();
        }

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

    static readonly List<TrackConflict> TrackConflicts = [];
    static readonly List<string> FilePaths = [];
    static readonly List<MovedTrackInfo> MovedTrackInfos = [];
}