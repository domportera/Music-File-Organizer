using System.Collections.Frozen;

namespace MusicOrganizer;

public static class Program
{
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

    const bool CompressionEnabled = false;


    public static void Main(string[] args)
    {
        if (!TryParseArgs(args, out var musicDirectory)) 
            return;

        var files = FileIO.FindAllFiles(musicDirectory, IgnoreHiddenFolders, IgnoreDirectories);

        TrackLoader.FindAudioAndPlaylistsIn(files, out var audioFiles, out var playlistFiles);

        // move playlists
        var playlistDirectoryPath = Path.Combine(musicDirectory, "Playlists");
        FileIO.MoveFilesInto(playlistDirectoryPath, playlistFiles);

        Organization.OrganizeTracks(audioFiles, musicDirectory, UseAlbumArtist, UseDiscSubdirectory, IgnoreDirectories,
            DoNotDeleteDirectories);

        if (CompressionEnabled)
        {
            CompressLosslessFiles(musicDirectory);
        }

        Console.WriteLine("End of program");
    }

    static bool TryParseArgs(IReadOnlyList<string> args, out string musicDirectory)
    {
        bool failedStartup = false;
        // get all files recursively from the path
        try
        {
            if (args.Count == 0 || !Directory.Exists(args[0]))
            {
                failedStartup = true;
            }
            
            musicDirectory = args[0];
        }
        catch
        {
            musicDirectory = string.Empty;
            failedStartup = true;
        }
        
        if (failedStartup)
        {
            Console.WriteLine("Please provide a valid path to a music directory");
        }

        return !failedStartup;
    }

    static void CompressLosslessFiles(string musicDirectory)
    {
        TrackLoader.FindLosslessFilesIn(musicDirectory, out var losslessFiles);
        Console.WriteLine($"Found {losslessFiles.Length} lossless files to compress");

        if (losslessFiles.Length > 0)
        {
            Ffmpeg.CompressFiles(losslessFiles);
        }
    }
}