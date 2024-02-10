using ATL;
using MusicOrganizer;

public static class Organization
{
    // todo - having these global variables is error prone 
    static readonly List<TrackConflict> TrackConflicts = [];
    static readonly List<MovedTrackInfo> MovedTrackInfos = [];
    
    public static void OrganizeTracks(
        FileInfo[] audioFiles, 
        string musicDirectory, 
        bool useAlbumArtist, 
        bool useDiscSubdirectory, 
        IReadOnlyCollection<string> ignoreDirectories, 
        IReadOnlyCollection<string> doNotDeleteDirectories)
    {
        TrackConflicts.Clear();
        MovedTrackInfos.Clear();
        
        Track[] tracks = TrackLoader.GetValidAudioFiles(audioFiles);
        
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
            .ForAll(albumKvp =>
            {
                OrganizeAlbum(albumKvp.Value, albumKvp.Key, musicDirectory, useAlbumArtist, useDiscSubdirectory);
            });
        
        Conflicts.HandleTrackConflicts(TrackConflicts);
        
        MoveAllStrayFiles(MovedTrackInfos, ignoreDirectories, doNotDeleteDirectories);
        TrackConflicts.Clear();
        FileIO.DeleteEmptyDirectories(musicDirectory, ignoreDirectories, doNotDeleteDirectories);
    }

    static void OrganizeAlbum(List<Track> trackList, string albumName, string musicRootDirectory, bool useAlbumArtist, bool useDiscSubdirectory)
    {
        const string unknownArtist = "Unknown Artist";
        const string variousArtists = "Various Artists";

        if (string.IsNullOrWhiteSpace(albumName))
        {
            foreach (var track in trackList)
            {
                var artist = TrackLoader.GetTrackArtist(track, useAlbumArtist);
                if (string.IsNullOrWhiteSpace(artist))
                {
                    artist = unknownArtist;
                }
                else
                {
                    var artistCounter = artist.Split(';')
                        .Where(x => !string.IsNullOrWhiteSpace(x));

                    if (artistCounter.Count() > 1)
                        artist = variousArtists;
                }

                NameAndMoveTrack(track, musicRootDirectory, albumName, true, artist, null, useDiscSubdirectory);
            }

            return;
        }

        int totalDiscCount = 1;
        string[] artists = trackList
            .SelectMany(track =>
            {
                var artist = TrackLoader.GetTrackArtist(track, useAlbumArtist);

                // while we're iterating, check if the album is multi-disc
                var discNo = track.DiscNumber ?? 1;
                if (discNo > totalDiscCount)
                    totalDiscCount = discNo;

                return artist.Split(';').Select(x => x.Trim());
            })
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .OrderBy(artist => char.IsUpper(artist[0])) // prioritize "correct" capitalization
            .DistinctBy(x => x.ToLowerInvariant()) // compare disregarding case
            .ToArray();

        string mostFrequentArtist;
        bool useArtistSubdirectory = true;

        switch (artists.Length)
        {
            case 0:
                mostFrequentArtist = unknownArtist;
                break;
            case 1:
                mostFrequentArtist = artists[0];
                break;
            case > 4:
                mostFrequentArtist = variousArtists;
                break;
            default:
            {
                // get top two most frequent artists
                var artistCount = new Dictionary<string, int>(artists.Length);
                foreach (var artist in artists)
                {
                    if (!artistCount.TryGetValue(artist, out var count))
                    {
                        artistCount[artist] = 1;
                    }
                    else
                    {
                        artistCount[artist] = count + 1;
                    }
                }

                // get the most frequent artist. If theres a tie, set mostFrequentArtist to unknown
                var artistsSorted = artistCount
                    .OrderByDescending(x => x.Value)
                    .Select(x => x.Key)
                    .ToArray();

                var mostFrequentArtistFound = artistsSorted[0];

                mostFrequentArtist = artistCount[mostFrequentArtistFound] == artistCount[artistsSorted[1]]
                    ? variousArtists
                    : mostFrequentArtistFound;

                break;
            }
        }

        foreach (var track in trackList)
        {
            NameAndMoveTrack(track, musicRootDirectory, albumName, useArtistSubdirectory, mostFrequentArtist, totalDiscCount, useDiscSubdirectory);
        }
    }

    static void NameAndMoveTrack(Track currentTrack, string musicDirectory, string album, bool useArtistSubdirectory,
        string artistName, int? totalDiscCount, bool useDiscSubdirectory)
    {
        try
        {
            var organized = UpdateTrack(musicDirectory, currentTrack, album, useArtistSubdirectory,
                artistName, totalDiscCount, useDiscSubdirectory,
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

    static bool UpdateTrack(string musicDirectory, Track currentTrack,
        string album,
        bool useArtistSubdirectory,
        string artistName,
        int? totalDiscCount,
        bool useDiscSubdirectory,
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

        if (totalDiscCount is > 1)
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

            if (useDiscSubdirectory)
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

        if (needsMetadataSave)
        {
            track.Save();
        }

        if (string.Equals(track.Path, newPath, FileIO.PathComparison))
        {
            newDirectory = null;
            return false;
        }

        if (!File.Exists(newPath))
        {
            var moved = FileIO.MoveFile(track.Path, newPath);
            return moved;
        }

        var conflict = new TrackConflict(track, newPath);
        TrackConflicts.Add(conflict);
        return true;
    }


    static void MoveStrayFiles(MovedTrackInfo movedTrackInfo, IReadOnlyCollection<string> ignoreDirectories, IReadOnlyCollection<string> doNotDeleteDirectories)
    {
        var originalDirectory = movedTrackInfo.OriginalDirectory;
        if (!originalDirectory.Exists)
            return;

        var newDirectoryString = movedTrackInfo.NewDirectoryString;
        var trackPath = movedTrackInfo.OriginalTrackPath;

        var strayDirectories = originalDirectory
            .GetDirectories()
            .Where(directory => !string.Equals(directory.FullName, newDirectoryString, FileIO.PathComparison)
                                && !ignoreDirectories.Contains(directory.Name));

        foreach (var strayDirectory in strayDirectories)
        {
            var newDir = Path.Combine(newDirectoryString, strayDirectory.Name);

            var allRemainingFiles = strayDirectory.GetFiles("*", SearchOption.AllDirectories);
            var straysInDirectory = allRemainingFiles
                .Where(file => !TrackLoader.IsAudioFile(file))
                .ToArray();

            if (straysInDirectory.Length > 0)
                Directory.CreateDirectory(newDir);
            
            FileIO.MoveFilesInto(newDir, straysInDirectory);

            if (allRemainingFiles.Length == straysInDirectory.Length)
            {
                FileIO.DeleteEmptyDirectories(strayDirectory.FullName, ignoreDirectories, doNotDeleteDirectories);
            }
        }

        var strayFiles = originalDirectory.GetFiles()
            .Where(file => !TrackLoader.IsAudioFile(file) &&
                           !string.Equals(file.FullName, trackPath, FileIO.PathComparison));

        foreach (var strayFile in strayFiles)
        {
            File.Move(strayFile.FullName, Path.Combine(newDirectoryString, strayFile.Name), true);
        }

        FileIO.DeleteEmptyDirectories(originalDirectory.FullName, ignoreDirectories, doNotDeleteDirectories);
    }

    static void MoveAllStrayFiles(List<MovedTrackInfo> movedTrackInfos, IReadOnlyCollection<string> ignoreDirectories, IReadOnlyCollection<string> doNotDeleteDirectories)
    {
        foreach (var movedTrackInfo in movedTrackInfos)
        {
            MoveStrayFiles(movedTrackInfo, ignoreDirectories, doNotDeleteDirectories);
        }
    }
}

public readonly struct MovedTrackInfo
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