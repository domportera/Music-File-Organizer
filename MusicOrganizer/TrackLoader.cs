using System.Collections.Concurrent;
using System.Collections.Frozen;
using ATL;

public static class TrackLoader
{
    static readonly FrozenSet<string> AudioFileTypes = new HashSet<string>
    {
        ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".mp1", ".mp2", ".aax", ".caf",
        ".m4b", ".mp4", ".mid", ".oga", ".tak", ".bwav", ".bwf", ".vgm", ".vgz", ".wv", ".wma", ".asf"
    }.ToFrozenSet();

    static readonly FrozenSet<string> LosslessAudioFileTypes = new HashSet<string>
    {
        ".flac", ".wav", ".tak", ".bwav", ".bwf", ".vgm", ".vgz", ".wv"
    }.ToFrozenSet();

    static readonly FrozenSet<string> PlaylistFileTypes = new HashSet<string>
    {
        ".m3u", ".m3u8", ".pls", ".wpl", ".zpl", ".xspf"
    }.ToFrozenSet();

    public static bool IsAudioFile(FileInfo file) => AudioFileTypes.Contains(file.Extension);

    public static Track? LoadTrack(FileInfo fileInfo)
    {
        Track? track = null;
        var path = fileInfo.FullName;
        try
        {
            track = new Track(path);
            Console.WriteLine($"Loaded track \"{path}\"");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to create track for {path}:\n{e}");
        }

        return track;
    }

    public static void FindAudioAndPlaylistsIn(FileInfo[] files, out FileInfo[] audioFiles,
        out FileInfo[] playlistFiles)
    {
        ConcurrentBag<FileInfo> playlistFileBag = [];
        audioFiles = files
            .Where(file =>
            {
                var isAudioFile = AudioFileTypes.Contains(file.Extension);
                if (!isAudioFile && PlaylistFileTypes.Contains(file.Extension))
                    playlistFileBag.Add(file);

                return isAudioFile;
            })
            .ToArray();

        playlistFiles = playlistFileBag.ToArray();
    }

    public static void FindLosslessFilesIn(string directory, out FileInfo[] losslessFiles)
    {
        losslessFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .AsParallel()
            .Select(file => new FileInfo(file))
            .Where(file => LosslessAudioFileTypes.Contains(file.Extension))
            .ToArray();
    }

    public static void FindLosslessFilesIn(FileInfo[] files, out FileInfo[] losslessFiles)
    {
        losslessFiles = files
            .Where(file => LosslessAudioFileTypes.Contains(file.Extension))
            .ToArray();
    }

    public static Track[] GetValidAudioFiles(FileInfo[] potentialAudioFiles)
    {
        var tracks = potentialAudioFiles
            .AsParallel()
            .Select(LoadTrack)
            .Where(track => track is not null)
            .Select(track => track!)
            .ToArray();
        return tracks;
    }

    public static string? GetTrackArtist(Track track, bool useAlbumArtist)
    {
        string artist;
        if (useAlbumArtist)
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
            artist = track.Composer;

        if (string.IsNullOrWhiteSpace(artist))
            artist = track.Conductor;

        return artist;
    }
}