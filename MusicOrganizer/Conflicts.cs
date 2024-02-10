using ATL;

namespace MusicOrganizer;

public static class Conflicts
{
    public static void HandleTrackConflicts(IList<TrackConflict> trackConflicts)
    {
        // ensure there are no duplicate conflicts
        for (int i = trackConflicts.Count - 1; i >= 0; i--)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                if (trackConflicts[i] == trackConflicts[j])
                {
                    trackConflicts.RemoveAt(i);
                    break;
                }
            }
        }

        foreach (var conflict in trackConflicts)
        {
            TryResolveConflict(conflict);
        }
    }

    public static bool TryResolveConflict(TrackConflict conflict)
    {
        var track = conflict.Track;
        var destinationPath = conflict.ExistingTrackPath;
        try
        {
            var destinationFile = new FileInfo(destinationPath);
            var existingTrack = TrackLoader.LoadTrack(destinationFile);
            if (existingTrack == null)
                throw new Exception($"Failed to load existing track at {destinationPath}");

            return ChooseBestQuality(track, existingTrack);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error choosing best quality for {destinationPath}:\n{e}");
            return false;
        }

        static bool ChooseBestQuality(Track track1, Track track2)
        {
            var hasZeroBitrate = track1.Bitrate == 0 || track2.Bitrate == 0;

            var durationDeltaMs = (long)Math.Abs(track1.DurationMs - track2.DurationMs);
            if (!hasZeroBitrate && durationDeltaMs > TrackDifferenceThresholdMs)
            {
                var difference = Math.Abs(track1.DurationMs - track2.DurationMs);
                Console.WriteLine($"Identical tracks have different durations (difference: {difference:f0}ms).\n" +
                                  GetTrackString(track1) + GetTrackString(track2));
                return false;
            }

            if (track2.Bitrate < track1.Bitrate)
            {
                var track2Path = track2.Path;
                var track1Path = track1.Path;
                File.Move(track1Path, track2Path, true);
                Console.WriteLine(
                    $"Replaced lower quality track ({track2.Bitrate}kbps vs {track1.Bitrate}kbps)\n" +
                    $"------> {track2Path}");
                return true;
            }

            // if the existing track is better quality, delete the new one
            FileInfo trackFile = new(track1.Path);

            if (trackFile.IsReadOnly)
                trackFile.IsReadOnly = false;

            try
            {
                trackFile.Delete();
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to delete {trackFile.FullName}:\n{e}");
                return false;
            }
        }

        static string GetTrackString(Track track)
        {
            var str = $"({track.Duration}s {track.AudioFormat.ShortName} {track.Bitrate}";

            if (track.BitDepth != -1)
                str += $" {track.BitDepth}";

            str += $") || [\"{track.Artist} - {track.TrackNumber}. {track.Title}\"] || {track.Path}\n";

            return str;
        }
    }

    const int TrackDifferenceThresholdMs = 1000;
}

#pragma warning disable CS0660, CS0661
public readonly struct TrackConflict
#pragma warning restore CS0660, CS0661
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
}