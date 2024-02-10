using System.Diagnostics;
using System.Text;
using ATL;

namespace MusicOrganizer;

public static class Ffmpeg
{
    const int TrackCompressionThresholdKbps = 900;
    const bool UseParallelCompression = true;
    const int CompressionLevel = 8;
    const bool ForceOverwriteReEncode = false;

    static readonly string FlacArgFormat = "-i \"{0}\" " +
                                 "-codec:a flac " +
                                 $"-compression_level {CompressionLevel} " +
                                 "\"{1}\"";

    public static void CompressFiles(FileInfo[] losslessFiles)
    {
        if (UseParallelCompression)
        {
            var threadCount = Math.Max(Environment.ProcessorCount - 1, 1);
            losslessFiles.AsParallel()
                // ReSharper disable once AsyncVoidLambda
                .WithDegreeOfParallelism(threadCount)
                .ForAll(async file =>
                {
                    try
                    {
                        Compress(file, "ffmpeg").Wait();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Error converting {file.FullName}\n" +
                                          $"{e.Message}");
                    }
                });
        }
        else
        {
            foreach (var file in losslessFiles)
            {
                try
                {
                    Compress(file, "ffmpeg").Wait();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error converting {file.FullName}\n" +
                                      $"{e.Message}");
                }
            }
        }
    }

    static async Task Compress(FileInfo inputFile, string ffmpegPath)
    {
        // check for if the file was already deleted previously
        inputFile.Refresh();
        if (!inputFile.Exists)
            return;

        var originalTrack = TrackLoader.LoadTrack(inputFile);

        if (originalTrack == null)
        {
            Console.WriteLine($"Failed to load track for conversion at {inputFile.FullName}");
            return;
        }

        if (originalTrack.Bitrate == 0 && inputFile.Extension == ".flac")
        {
            Console.WriteLine(
                $"Deleting {inputFile.FullName} because it's bitrate is 0kbps - likely corrupt or incomplete");
            try
            {
                inputFile.Delete();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to delete {inputFile.FullName}\n" +
                                  $"{e}");
            }

            return;
        }

        if (originalTrack.Bitrate < TrackCompressionThresholdKbps && !ForceOverwriteReEncode)
        {
            Console.WriteLine(
                $"Skipping {inputFile.FullName} because it's bitrate is low enough ({originalTrack.Bitrate}kbps)");
            return;
        }

        var compressedPath = inputFile.FullName.Replace(inputFile.Extension, ".flac");
        var outputFile = new FileInfo(compressedPath);
        var originalPath = inputFile.FullName;

        if (inputFile.FullName == compressedPath)
        {
            var tempPath = inputFile.FullName.Replace(inputFile.Extension, "-temp.flac");
            File.Move(inputFile.FullName, tempPath, true);
            inputFile = new FileInfo(tempPath);
        }

        using Process flacConversion = CreateFfmpegProcess(inputFile.FullName, ffmpegPath, compressedPath);

        StreamReader? reader;
        StringBuilder outputStringBuilder = new(4096);
        char[] buffer = new char[4096];

        try
        {
            flacConversion.Start();
            reader = flacConversion.StandardOutput;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to start conversion for {inputFile.FullName}");
            Console.WriteLine(e);
            MoveInputFileBack(inputFile, originalPath);
            return;
        }

        Console.WriteLine($"Started conversion for {inputFile.FullName}");

        try
        {
            Read(reader, outputStringBuilder, buffer);
            await flacConversion.WaitForExitAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to wait for conversion for {inputFile.FullName}\n" +
                              $"{e.Message}");
            flacConversion.Kill();
        }

        Console.WriteLine($"Finished conversion for {inputFile.FullName}");

        await Task.Delay(500); // wait for the file to be written to disk

        Track? convertedTrack = TrackLoader.LoadTrack(outputFile);
        outputFile.Refresh();
        if (convertedTrack == null || convertedTrack.Bitrate == 0 ||
            flacConversion.ExitCode != 0 || !outputFile.Exists || outputFile.Length == 0)
        {
            StringBuilder stringBuilder = new();

            stringBuilder.AppendLine($"Failed to convert {inputFile.FullName} to {outputFile.FullName}");
            stringBuilder.AppendLine($"exit code: {flacConversion.ExitCode}");
            stringBuilder.AppendLine($"output file exists: {outputFile.Exists}");

            if (outputFile.Exists)
                stringBuilder.AppendLine($"output file length: {outputFile.Length}");

            try
            {
                stringBuilder.AppendLine($"converted track is null: {convertedTrack == null}");
                if (convertedTrack != null)
                    stringBuilder.AppendLine($"converted track bitrate: {convertedTrack.Bitrate}");
            }
            catch (Exception e)
            {
                stringBuilder.AppendLine($"Failed to get converted track info:\n{e.Message}");
            }


            stringBuilder.AppendLine("OUTPUT: ");
            Read(reader, outputStringBuilder, buffer);
            stringBuilder.Append(outputStringBuilder);

            Console.WriteLine(stringBuilder.ToString());

            var deleted = TryDelete(outputFile);
            if (deleted)
                MoveInputFileBack(inputFile, originalPath);
            return;
        }

        // delete the compressed FLAC if it's larger than the original
        if (ForceOverwriteReEncode || outputFile.Length <= inputFile.Length)
        {
            var deleted = TryDelete(inputFile);
            if (deleted)
                Console.WriteLine($"Converted {inputFile.FullName} to {outputFile.FullName}");

            return;
        }

        try
        {
            outputFile.Delete();
            Console.WriteLine(
                $"Deleted compressed \"{outputFile.FullName}\" because it was larger than the original");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to delete {outputFile.FullName}\n" +
                              $"{e.Message}");
        }

        MoveInputFileBack(inputFile, originalPath);
    }

    static void Read(StreamReader reader, StringBuilder stringBuilder, char[] buffer)
    {
        try
        {
            while (reader.Peek() > 0)
            {
                var countRead = reader.Read(buffer, 0, buffer.Length);
                stringBuilder.Append(buffer, 0, countRead);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to read from stream\n" +
                              $"{e.Message}");
        }

        stringBuilder.AppendLine();
    }

    static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to delete {file.FullName}\n" +
                              $"{e.Message}");
            return false;
        }
    }

    static Process CreateFfmpegProcess(string inputPath, string ffmpegPath, string outputPath)
    {
        string arguments = string.Format(FlacArgFormat, inputPath, outputPath);

        var conversionProcess = new Process();

        conversionProcess.StartInfo = new ProcessStartInfo(ffmpegPath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true
        };

        return conversionProcess;
    }

    static void MoveInputFileBack(FileInfo input, string originalPath)
    {
        // move the original back to its original path if it was moved
        try
        {
            if (input.FullName != originalPath)
            {
                File.Move(input.FullName, originalPath, true);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to move {input.FullName} back to {originalPath}\n" +
                              $"{e.Message}");
        }
    }
}