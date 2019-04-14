using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace BrZip
{
    class Program
    {
        public const string Extension = ".brzip";

        static void Main(string[] args)
        {
            if (Debugger.IsAttached)
            {
                SafeMain(args);
                return;
            }

            try
            {
                SafeMain(args);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        static void SafeMain(string[] args)
        {
            Console.WriteLine("BrZip - Copyright (C) 2018-" + DateTime.Now.Year + " Simon Mourier. All rights reserved.");
            Console.WriteLine();
            if (CommandLine.HelpRequested || args.Length < 2)
            {
                Help();
                return;
            }

            var arg = CommandLine.GetNullifiedArgument(0);
            if (!Conversions.TryChangeType<CompressionMode>(arg, out var mode))
            {
                Help();
                return;
            }

            var inputPath = CommandLine.GetNullifiedArgument(1);
            if (inputPath == null)
            {
                Help();
                return;
            }

            inputPath = Path.GetFullPath(inputPath);
            Console.WriteLine("Input path: " + inputPath);

            var outputPath = CommandLine.GetNullifiedArgument(2);
            if (mode == CompressionMode.Decompress)
            {
                bool lo = CommandLine.GetArgument("listonly", false);
                outputPath = Path.GetFullPath(outputPath ?? Path.GetFileNameWithoutExtension(inputPath));
                Console.WriteLine("Output path: " + outputPath);
                using (var readArchive = new BrZipReadArchive())
                {
                    readArchive.Open(inputPath);
                    Console.WriteLine("Archive has " + readArchive.Entries.Count + " entrie(s).");
                    if (lo)
                    {
                        foreach (var entry in readArchive.Entries)
                        {
                            Console.WriteLine(entry.Value.Name);
                        }
                        return;
                    }

                    foreach (var entry in readArchive.Entries)
                    {
                        var path = Path.Combine(outputPath, entry.Value.Name);
                        entry.Value.WriteAsync(path).Wait();
                        Console.WriteLine("Written " + entry.Value.Name + " to " + path);
                    }
                }
                return;
            }

            outputPath = Path.GetFullPath(outputPath ?? Path.GetFileNameWithoutExtension(inputPath) + Extension);
            Console.WriteLine("Output path: " + outputPath);

            using (var writeArchive = new BrZipWriteArchive())
            {
                writeArchive.AddedEntry += OnAddedEntry;
                writeArchive.Open(outputPath);
                if (File.Exists(inputPath))
                {
                    writeArchive.AddEntry(inputPath);
                    return;
                }

                writeArchive.AddDirectory(inputPath);
            }
            return;
        }

        private static void OnAddedEntry(object sender, BrZipArchiveEventArgs e)
        {
            var length = new FileInfo(e.FilePath).Length;
            var ratio = 100 * length / e.CompressedLength; // min is 1
            Console.WriteLine("File " + e.FilePath + " was added. Length: " + length + " Compressed: " + e.CompressedLength + " Ratio: " + ratio + " %");
        }

        static void Help()
        {
            Console.WriteLine(Assembly.GetEntryAssembly().GetName().Name.ToUpperInvariant() + " <command> <input path> [output path] [options]");
            Console.WriteLine();
            Console.WriteLine("Description:");
            Console.WriteLine("    This tool is used to create .BRZIP files from a directory or a file.");
            Console.WriteLine("    BRZIP files are archive files that contains files compressed using the Brotli algorithm.");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("    compress             Creates a .brzip archive file from a file or a directory.");
            Console.WriteLine("    decompress           Extracts files from a .brzip archive file.");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("    /listonly            Decompress only list the files.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine();
            Console.WriteLine("    " + Assembly.GetEntryAssembly().GetName().Name.ToUpperInvariant() + " c:\\mypath\\myproject");
            Console.WriteLine();
            Console.WriteLine("    Compress the myproject directory into a myproject.brzip file in the current directory.");
            Console.WriteLine();
        }
    }
}
