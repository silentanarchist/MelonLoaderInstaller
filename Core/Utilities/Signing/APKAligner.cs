using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MelonLoader.Installer.Core.Utilities.Signing
{
    public class APKAligner
    {
        // Warpforge Revival change: the aligned copy is written to a file next to the APK and then moved over it,
        // and each entry's data is copied through a small buffer. The original built the whole aligned APK in a
        // MemoryStream and read each entry into one array, which for the game's 740 MB data pack needed well over
        // a gigabyte of memory: fine on a PC, too much for the patcher app running on a phone. The bytes written
        // are the same as before (checked against the original code on the game's files).
        public static void AlignApk(string path)
        {
            string tempPath = path + ".aligning";
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (FileStream outFs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
            {
                FileMemory memory = new FileMemory(fs);
                FileMemory outMemory = new FileMemory(outFs);
                memory.Position = memory.Length() - 22;
                while (memory.ReadInt() != EndOfCentralDirectory.SIGNATURE)
                {
                    memory.Position -= 4 + 1;
                }
                memory.Position -= 4;
                List<CentralDirectoryFileHeader> cDs = new List<CentralDirectoryFileHeader>();
                EndOfCentralDirectory eocd = new EndOfCentralDirectory(memory);
                memory.Position = eocd.OffsetOfCD;
                byte[] buffer = new byte[1024 * 1024];
                for (int i = 0; i < eocd.NumberOfCDsOnDisk; i++)
                {
                    CentralDirectoryFileHeader cd = new CentralDirectoryFileHeader(memory);
                    var nextCD = memory.Position;
                    memory.Position = cd.Offset;
                    LocalFileHeader lfh = new LocalFileHeader(memory);
                    long dataStart = memory.Position;
                    memory.Position = dataStart + cd.CompressedSize;
                    DataDescriptor? dd = null;
                    if ((lfh.GeneralPurposeFlag & 0x08) != 0)
                        dd = new DataDescriptor(memory);
                    if (lfh.CompressionMethod == 0)
                    {
                        short padding = (short)((outMemory.Position + 30 + FileMemory.StringLength(lfh.FileName) + lfh.ExtraField.Length) % 4);
                        if (padding > 0)
                        {
                            padding = (short)(4 - padding);
                            lfh.ExtraField = lfh.ExtraField.Concat(new byte[padding]).ToArray();
                        }
                    }
                    cd.Offset = (int)outMemory.Position;
                    lfh.Write(outMemory);
                    CopyRange(fs, dataStart, cd.CompressedSize, outFs, buffer);
                    if (dd != null)
                        dd.Write(outMemory);
                    cDs.Add(cd);
                    memory.Position = nextCD;
                }
                eocd.OffsetOfCD = (int)outMemory.Position;
                foreach (CentralDirectoryFileHeader cd in cDs)
                {
                    cd.Write(outMemory);
                }
                eocd.NumberOfCDs = (short)cDs.Count;
                eocd.NumberOfCDsOnDisk = (short)cDs.Count;
                eocd.SizeOfCD = (int)(outMemory.Position - eocd.OffsetOfCD);
                eocd.Write(outMemory);
                outFs.SetLength(outFs.Position);
                outFs.Flush(true);
            }
            File.Move(tempPath, path, true);
        }

        // Copies count bytes starting at offset in src to the current position of dst (count may be 0).
        // Like the original's single Read of the whole entry, reading stops early at the end of src.
        internal static void CopyRange(Stream src, long offset, long count, Stream dst, byte[] buffer)
        {
            src.Position = offset;
            long left = count;
            long written = 0;
            while (left > 0)
            {
                int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0)
                    break;
                dst.Write(buffer, 0, n);
                left -= n;
                written += n;
            }
            // the original wrote a zero-filled array of the full size even when the read came up short
            if (written < count)
            {
                Array.Clear(buffer);
                while (written < count)
                {
                    int n = (int)Math.Min(buffer.Length, count - written);
                    dst.Write(buffer, 0, n);
                    written += n;
                }
            }
        }
    }
}
