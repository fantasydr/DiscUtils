using System;
using System.Collections.Generic;
using System.IO;

using DiscUtils;
using DiscUtils.Fat;

namespace FatQuery
{
    class Program
    {
        static Dictionary<ulong, string> _mapping = new Dictionary<ulong, string>();
        static Dictionary<string, ulong> _first = new Dictionary<string, ulong>();

        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Query file name from given sector or sector list file.");
                Console.WriteLine("The drive or image must be FAT partition (tested only in FAT16/FAT32).");
                Console.WriteLine("Use nfi.exe from Windows SDK to query the NTFS sector");
                Console.WriteLine("");
                Console.WriteLine("Usage:");
                Console.WriteLine("fatquery <drive_letter or image_file> <sector or sector_list_file>");
                Console.WriteLine("");
                Console.WriteLine("Samples:");
                Console.WriteLine("fatquery h 40353");
                Console.WriteLine("fatquery h sectors.txt");
                Console.WriteLine("fatquery h.bin sectors.txt");
                return;
            }

            var source = args[0];
            var sectorParam = args[1];

            try
            {
                if (File.Exists(source))
                {
                    using (var input = new FileStream(source, FileMode.Open))
                    {
                        ParseFAT(sectorParam, input);
                    }
                }
                else
                {
                    using (var input = new DiskStream(source))
                    {
                        ParseFAT(sectorParam, input);
                    }
                }
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine("Cannot open the drive: " + source);
            }
            catch(Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }

        private static void ParseFAT(string sectorParam, Stream input)
        {
            using (FatFileSystem fs = new FatFileSystem(input))
            {
                var startSector = (ulong)fs.HiddenSectors + (ulong)(fs.FirstDataSector - 2 * (int)fs.SectorsPerCluster); // magic number 2
                Console.WriteLine("Start sector: {0}", fs.HiddenSectors);
                Console.WriteLine("Data sector: {0}", fs.FirstDataSector);
                Console.WriteLine("First file sector: {0}", startSector);

                Dump(string.Empty, string.Empty, fs);
                Console.WriteLine("FAT Entry count: {0}", _first.Count);
                Console.WriteLine("");

                if (ulong.TryParse(sectorParam, out ulong onesec))
                {
                    TryGetResult(startSector, fs, onesec);
                }
                else if (File.Exists(sectorParam))
                {
                    ulong[] sectors = ReadSectors(sectorParam);
                    foreach (var sector in sectors)
                        TryGetResult(startSector, fs, sector);
                }
                else
                {
                    Console.WriteLine("Cannot find sector information");
                }
            }
        }

        private static ulong[] ReadSectors(string filename)
        {
            List<ulong> sectors = new List<ulong>();
            using (StreamReader sr = new StreamReader(filename))
            {
                while(!sr.EndOfStream)
                {
                    if(ulong.TryParse(sr.ReadLine(), out ulong result))
                        sectors.Add(result);
                }
            }
            return sectors.ToArray();
        }

        private static void TryGetResult(ulong startSector, FatFileSystem fs, ulong sector)
        {
            ulong cluster = (sector - startSector) / fs.SectorsPerCluster;
            Console.WriteLine(string.Format("Target Sector: {0}", sector));
            if (_mapping.TryGetValue(cluster, out string result))
            {
                var firstCluster = _first[result];
                Console.WriteLine(string.Format("File Path: {0}", result));
                var firstSector = firstCluster * fs.SectorsPerCluster + startSector;
                Console.WriteLine(string.Format("First Sector: {0}", firstSector));
                Console.WriteLine(string.Format("First Byte: {0}", firstSector * (ulong)fs.BytesPerSector));
                Console.WriteLine(string.Format("First Cluster (in FAT): {0}", firstCluster));
            }
            else
            {
                Console.WriteLine(string.Format("Missing..."));
            }

            Console.WriteLine("");
            Console.WriteLine("");
        }

        static void Dump(string path, string rootfull, FatFileSystem fs)
        {
            var fat = fs.Fat;
            var folders = fs.GetDirectories(path);
            foreach(var folder in folders)
            {
                var files = fs.GetFiles(folder);
                var di = fs.GetFileInfo(folder);
                var fullpath = Path.Combine(rootfull, fs.GetLongFileName(di.FullName));
                foreach (var file in files)
                {
                    var entry = fs.GetDirectoryEntry(file);

                    var fi = fs.GetFileInfo(file);
                    var fullname = Path.Combine(fullpath, fs.GetLongFileName(fi.FullName));

                    var cluster = entry.FirstCluster;
                    _first.Add(fullname, cluster);

                    while (!fat.IsEndOfChain(cluster))
                    {
                        _mapping.Add(cluster, fullname);
                        cluster = fat.GetNext(cluster);
                    }

                }

                Dump(folder, fullpath, fs);
            }

            return;
        }
    }
}