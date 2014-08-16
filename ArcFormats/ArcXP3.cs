//! \file       ArcXP3.cs
//! \date       Wed Jul 16 13:58:17 2014
//! \brief      KiriKiri engine archive implementation.
//
// Copyright (C) 2014 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using GameRes.Utility;
using ZLibNet;
using GameRes.Formats.Strings;
using GameRes.Formats.Properties;

namespace GameRes.Formats.KiriKiri
{
    public struct Xp3Segment
    {
        public bool IsCompressed;
        public long Offset;
        public uint Size;
        public uint PackedSize;
    }

    public class Xp3Entry : PackedEntry
    {
        List<Xp3Segment> m_segments = new List<Xp3Segment>();

        public bool IsEncrypted { get; set; }
        public ICrypt Cipher { get; set; }
        public ICollection<Xp3Segment> Segments { get { return m_segments; } }
        public uint Hash { get; set; }
    }

    public class Xp3Options : ResourceOptions
    {
        public int              Version { get; set; }
        public ICrypt            Scheme { get; set; }
        public bool       CompressIndex { get; set; }
        public bool    CompressContents { get; set; }
        public bool          RetainDirs { get; set; }
    }

    // Archive version 1: encrypt file first, then calculate checksum
    //         version 2: calculate checksum, then encrypt

    [Export(typeof(ArchiveFormat))]
    public class Xp3Opener : ArchiveFormat
    {
        public override string Tag { get { return "XP3"; } }
        public override string Description { get { return arcStrings.XP3Description; } }
        public override uint Signature { get { return 0x0d335058; } }
        public override bool IsHierarchic { get { return true; } }
        public override bool CanCreate { get { return true; } }

        private static readonly ICrypt NoCryptAlgorithm = new NoCrypt();

        public static readonly Dictionary<string, ICrypt> KnownSchemes = new Dictionary<string, ICrypt> {
            { arcStrings.ArcNoEncryption, NoCryptAlgorithm },
            { "Fate/Stay Night",    new FateCrypt() },
            { "Swan Song",          new SwanSongCrypt() },
            { "Cafe Sourire",       new XorCrypt (0xcd) },
            { "Seirei Tenshou",     new SeitenCrypt() },
            { "Okiba ga Nai!",      new OkibaCrypt() },
        };

        public override ArcFile TryOpen (ArcView file)
        {
            if (!file.View.AsciiEqual (0, "XP3\x0d\x0a\x20\x0a\x1a\x8b\x67\x01"))
                return null;
            long dir_offset = file.View.ReadInt64 (0x0b);
            if (0x17 == dir_offset)
            {
                if (1 != file.View.ReadUInt32 (0x13))
                    return null;
                if (0x80 != file.View.ReadUInt32 (0x17))
                    return null;
                dir_offset = file.View.ReadInt64 (0x20);
            }
            if (dir_offset >= file.MaxOffset)
                return null;

            int header_type = file.View.ReadByte (dir_offset);
            if (0 != header_type && 1 != header_type)
                return null;

            Stream header_stream;
            if (0 == header_type) // read unpacked header
            {
                long header_size = file.View.ReadInt64 (dir_offset+1);
                if (header_size > uint.MaxValue)
                    return null;
                header_stream = file.CreateStream (dir_offset+9, (uint)header_size);
            }
            else // read packed header
            {
                long packed_size = file.View.ReadInt64 (dir_offset+1);
                if (packed_size > uint.MaxValue)
                    return null;
                long header_size = file.View.ReadInt64 (dir_offset+9);
                using (var input = file.CreateStream (dir_offset+17, (uint)packed_size))
                    header_stream = ZLibCompressor.DeCompress (input);
            }

            var crypt_algorithm = new Lazy<ICrypt> (QueryCryptAlgorithm);

            var dir = new List<Entry>();
            dir_offset = 0;
            using (var header = new BinaryReader (header_stream, Encoding.Unicode))
            {
                while (-1 != header.PeekChar())
                {
                    uint entry_signature = header.ReadUInt32();
                    if (0x656c6946 != entry_signature) // "File"
                    {
                        break;
                    }
                    long entry_size = header.ReadInt64();
                    dir_offset += 12 + entry_size;
                    var entry = new Xp3Entry();
                    while (entry_size > 0)
                    {
                        uint section = header.ReadUInt32();
                        long section_size = header.ReadInt64();
                        entry_size -= 12;
                        if (section_size > entry_size)
                            break;
                        entry_size -= section_size;
                        long next_section_pos = header.BaseStream.Position + section_size;
                        switch (section)
                        {
                        case 0x6f666e69: // "info"
                            {
                                if (entry.Size != 0 || !string.IsNullOrEmpty (entry.Name))
                                {
                                    goto NextEntry; // ambiguous entry, ignore
                                }
                                entry.IsEncrypted = 0 != header.ReadUInt32();
                                long file_size = header.ReadInt64();
                                long packed_size = header.ReadInt64();
                                if (file_size > uint.MaxValue || packed_size > uint.MaxValue)
                                {
                                    goto NextEntry;
                                }
                                entry.IsPacked     = file_size != packed_size;
                                entry.Size         = (uint)packed_size;
                                entry.UnpackedSize = (uint)file_size;

                                int name_size = header.ReadInt16();
                                if (name_size > 0x100 || name_size <= 0)
                                {
                                    goto NextEntry;
                                }
                                if (entry.IsEncrypted)
                                    entry.Cipher = crypt_algorithm.Value;
                                else
                                    entry.Cipher = NoCryptAlgorithm;

                                char[] name = header.ReadChars (name_size);
                                entry.Name = new string (name);
                                entry.Type = FormatCatalog.Instance.GetTypeFromName (entry.Name);
                                break;
                            }
                        case 0x6d676573: // "segm"
                            {
                                int segment_count = (int)(section_size / 0x1c);
                                if (segment_count > 0)
                                {
                                    for (int i = 0; i < segment_count; ++i)
                                    {
                                        bool compressed  = 0 != header.ReadInt32();
                                        long segment_offset = header.ReadInt64();
                                        long segment_size   = header.ReadInt64();
                                        long segment_packed_size = header.ReadInt64();
                                        if (segment_offset > file.MaxOffset || segment_size > file.MaxOffset
                                            || segment_packed_size > file.MaxOffset)
                                        {
                                            goto NextEntry;
                                        }
                                        var segment = new Xp3Segment {
                                            IsCompressed = compressed,
                                            Offset       = segment_offset,
                                            Size         = (uint)segment_size,
                                            PackedSize   = (uint)segment_packed_size
                                        };
                                        entry.Segments.Add (segment);
                                    }
                                    entry.Offset = entry.Segments.First().Offset;
                                }
                                break;
                            }
                        case 0x726c6461: // "adlr"
                            if (4 == section_size)
                                entry.Hash = header.ReadUInt32();
                            break;

                        default: // unknown section
                            break;
                        }
                        header.BaseStream.Position = next_section_pos;
                    }
                    if (!string.IsNullOrEmpty (entry.Name) && entry.Segments.Any())
                    {
                        dir.Add (entry);
//                        Trace.WriteLine (string.Format ("{0,-16} {3:X8} {1,11} {2,12}", entry.Name,
//                                                        entry.IsEncrypted ? "[encrypted]" : "",
//                                                        entry.Segments.First().IsCompressed ? "[compressed]" : "",
//                                                        entry.Hash));
                    }
NextEntry:
                    header.BaseStream.Position = dir_offset;
                }
            }
            return new ArcFile (file, this, dir);
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var xp3_entry = entry as Xp3Entry;
            if (null == xp3_entry)
                return arc.File.CreateStream (entry.Offset, entry.Size);
            if (1 == xp3_entry.Segments.Count && !xp3_entry.IsEncrypted)
            {
                var segment = xp3_entry.Segments.First();
                if (segment.IsCompressed)
                    return new ZLibStream (arc.File.CreateStream (segment.Offset, segment.PackedSize),
                                           CompressionMode.Decompress);
                else
                    return arc.File.CreateStream (segment.Offset, segment.Size);
            }
            return new Xp3Stream (arc.File, xp3_entry);
        }

        public override ResourceOptions GetDefaultOptions ()
        {
            return new Xp3Options {
                Version             = Settings.Default.XP3Version,
                Scheme              = GetScheme (Settings.Default.XP3Scheme),
                CompressIndex       = Settings.Default.XP3CompressHeader,
                CompressContents    = Settings.Default.XP3CompressContents,
                RetainDirs          = Settings.Default.XP3RetainStructure,
            };
        }

        public override object GetCreationWidget ()
        {
            return new GUI.CreateXP3Widget();
        }

        public override object GetAccessWidget ()
        {
            return new GUI.WidgetXP3();
        }

        ICrypt QueryCryptAlgorithm ()
        {
            var options = Query<Xp3Options> (arcStrings.ArcEncryptedNotice);
            return options.Scheme;
        }

        public static ICrypt GetScheme (string scheme)
        {
            ICrypt algorithm = NoCryptAlgorithm;
            if (!string.IsNullOrEmpty (scheme))
            {
                KnownSchemes.TryGetValue (scheme, out algorithm);
            }
            return algorithm;
        }

        static uint GetFileCheckSum (Stream src)
        {
            // compute file checksum via adler32.
            // src's file pointer should be reset to zero.
            var sum = new Adler32();
            byte[] buf = new byte[64*1024];
            for (;;)
            {
                int read = src.Read (buf, 0, buf.Length);
                if (0 == read) break;
                sum.Update (buf, 0, read);
            }
            return sum.Value;
        }

        static readonly byte[] s_xp3_header = {
            (byte)'X', (byte)'P', (byte)'3', 0x0d, 0x0a, 0x20, 0x0a, 0x1a, 0x8b, 0x67, 0x01
        };

        public override void Create (Stream output, IEnumerable<Entry> list, ResourceOptions options,
                                     EntryCallback callback)
        {
            var xp3_options = GetOptions<Xp3Options> (options);

            ICrypt scheme = xp3_options.Scheme;
            bool compress_index = xp3_options.CompressIndex;
            bool compress_contents = xp3_options.CompressContents;
            bool retain_dirs = xp3_options.RetainDirs;

            bool use_encryption = scheme != NoCryptAlgorithm;

            using (var writer = new BinaryWriter (output, Encoding.ASCII, true))
            {
                writer.Write (s_xp3_header);
                if (2 == xp3_options.Version)
                {
                    writer.Write ((long)0x17);
                    writer.Write ((int)1);
                    writer.Write ((byte)0x80);
                    writer.Write ((long)0);
                }
                long index_pos_offset = writer.BaseStream.Position;
                writer.BaseStream.Seek (8, SeekOrigin.Current);

                int callback_count = 0;
                var used_names = new HashSet<string>();
                var dir = new List<Xp3Entry>();
                long current_offset = writer.BaseStream.Position;
                foreach (var entry in list)
                {
                    if (null != callback)
                        callback (callback_count++, entry, arcStrings.MsgAddingFile);

                    string name = entry.Name;
                    if (!retain_dirs)
                        name = Path.GetFileName (name);
                    else
                        name = name.Replace (@"\", "/");
                    if (!used_names.Add (name))
                    {
                        Trace.WriteLine ("duplicate name", entry.Name);
                        continue;
                    }

                    var xp3entry = new Xp3Entry {
                        Name            = name,
                        IsEncrypted     = use_encryption,
                        Cipher          = scheme,
                    };
                    bool compress = compress_contents && ShouldCompressFile (entry);
                    using (var file = File.Open (name, FileMode.Open, FileAccess.Read))
                    {
                        if (!use_encryption || 0 == file.Length)
                            RawFileCopy (file, xp3entry, output, compress);
                        else
                            EncryptedFileCopy (file, xp3entry, output, compress);
                    }

                    dir.Add (xp3entry);
                }

                long index_pos = writer.BaseStream.Position;
                writer.BaseStream.Position = index_pos_offset;
                writer.Write (index_pos);
                writer.BaseStream.Position = index_pos;

                using (var header = new BinaryWriter (new MemoryStream (dir.Count*0x58), Encoding.Unicode))
                {
                    if (null != callback)
                        callback (callback_count++, null, arcStrings.MsgWritingIndex);

                    long dir_pos = 0;
                    foreach (var entry in dir)
                    {
                        header.BaseStream.Position = dir_pos;
                        header.Write ((uint)0x656c6946); // "File"
                        long header_size_pos = header.BaseStream.Position;
                        header.Write ((long)0);
                        header.Write ((uint)0x6f666e69); // "info"
                        header.Write ((long)(4+8+8+2 + entry.Name.Length*2));
                        header.Write ((uint)(use_encryption ? 0x80000000 : 0));
                        header.Write ((long)entry.UnpackedSize);
                        header.Write ((long)entry.Size);

                        header.Write ((short)entry.Name.Length);
                        foreach (char c in entry.Name)
                            header.Write (c);

                        header.Write ((uint)0x6d676573); // "segm"
                        header.Write ((long)0x1c);
                        var segment = entry.Segments.First();
                        header.Write ((int)(segment.IsCompressed ? 1 : 0));
                        header.Write ((long)segment.Offset);
                        header.Write ((long)segment.Size);
                        header.Write ((long)segment.PackedSize);

                        header.Write ((uint)0x726c6461); // "adlr"
                        header.Write ((long)4);
                        header.Write ((uint)entry.Hash);

                        dir_pos = header.BaseStream.Position;
                        long header_size = dir_pos - header_size_pos - 8;
                        header.BaseStream.Position = header_size_pos;
                        header.Write (header_size);
                    }

                    header.BaseStream.Position = 0;
                    writer.Write ((byte)(compress_index ? 1 : 0));
                    long unpacked_dir_size = header.BaseStream.Length;
                    if (compress_index)
                    {
                        if (null != callback)
                            callback (callback_count++, null, arcStrings.MsgCompressingIndex);

                        long packed_dir_size_pos = writer.BaseStream.Position;
                        writer.Write ((long)0);
                        writer.Write (unpacked_dir_size);

                        long dir_start = writer.BaseStream.Position;
                        using (var zstream = new ZLibStream (writer.BaseStream, CompressionMode.Compress,
                                                             CompressionLevel.Level9, true))
                            header.BaseStream.CopyTo (zstream);

                        long packed_dir_size = writer.BaseStream.Position - dir_start;
                        writer.BaseStream.Position = packed_dir_size_pos;
                        writer.Write (packed_dir_size);
                    }
                    else
                    {
                        writer.Write (unpacked_dir_size);
                        header.BaseStream.CopyTo (writer.BaseStream);
                    }
                }
            }
            output.Seek (0, SeekOrigin.End);
        }

        void RawFileCopy (FileStream file, Xp3Entry xp3entry, Stream output, bool compress)
        {
            if (file.Length > uint.MaxValue)
                throw new FileSizeException();

            uint unpacked_size    = (uint)file.Length;
            xp3entry.UnpackedSize = (uint)unpacked_size;
            xp3entry.Size         = (uint)unpacked_size;
            compress = compress && unpacked_size > 0;
            var segment = new Xp3Segment {
                IsCompressed = compress,
                Offset       = output.Position,
                Size         = unpacked_size,
                PackedSize   = unpacked_size
            };
            if (compress)
            {
                using (var zstream = new ZLibStream (output, CompressionMode.Compress, CompressionLevel.Level9, true))
                {
                    xp3entry.Hash = CheckedCopy (file, zstream);
                    zstream.Flush();
                    segment.PackedSize = (uint)zstream.TotalOut;
                }
            }
            else
            {
                xp3entry.Hash = CheckedCopy (file, output);
            }
            xp3entry.Segments.Add (segment);
        }

        void EncryptedFileCopy (FileStream file, Xp3Entry xp3entry, Stream output, bool compress)
        {
            if (file.Length > int.MaxValue)
                throw new FileSizeException();

            using (var map = MemoryMappedFile.CreateFromFile (file, null, 0,
                    MemoryMappedFileAccess.Read, null, HandleInheritability.None, true))
            {
                uint unpacked_size    = (uint)file.Length;
                xp3entry.UnpackedSize = (uint)unpacked_size;
                xp3entry.Size         = (uint)unpacked_size;
                using (var view = map.CreateViewAccessor (0, unpacked_size, MemoryMappedFileAccess.Read))
                {
                    var segment = new Xp3Segment {
                        IsCompressed = compress,
                        Offset       = output.Position,
                        Size         = unpacked_size,
                        PackedSize   = unpacked_size,
                    };
                    xp3entry.Segments.Add (segment);
                    bool need_output_dispose = false;
                    if (compress)
                    {
                        output = new ZLibStream (output, CompressionMode.Compress, CompressionLevel.Level9, true);
                        need_output_dispose = true;
                    }
                    unsafe
                    {
                        byte[] read_buffer = new byte[81920];
                        byte* ptr = view.GetPointer (0);
                        try
                        {
                            var checksum = new Adler32();
                            bool hash_after_crypt = xp3entry.Cipher.HashAfterCrypt;
                            if (!hash_after_crypt)
                                xp3entry.Hash = checksum.Update (ptr, (int)unpacked_size);
                            int offset = 0;
                            int remaining = (int)unpacked_size;
                            while (remaining > 0)
                            {
                                int amount = Math.Min (remaining, read_buffer.Length);
                                remaining -= amount;
                                Marshal.Copy ((IntPtr)(ptr+offset), read_buffer, 0, amount);
                                xp3entry.Cipher.Encrypt (xp3entry, offset, read_buffer, 0, amount);
                                if (hash_after_crypt)
                                    checksum.Update (read_buffer, 0, amount);
                                output.Write (read_buffer, 0, amount);
                                offset += amount;
                            }
                            if (hash_after_crypt)
                                xp3entry.Hash = checksum.Value;
                            if (compress)
                            {
                                output.Flush();
                                segment.PackedSize = (uint)(output as ZLibStream).TotalOut;
                            }
                        }
                        finally
                        {
                            view.SafeMemoryMappedViewHandle.ReleasePointer();
                            if (need_output_dispose)
                                output.Dispose();
                        }
                    }
                }
            }
        }

        uint CheckedCopy (Stream src, Stream dst)
        {
            var checksum = new Adler32();
            var read_buffer = new byte[81920];
            for (;;)
            {
                int read = src.Read (read_buffer, 0, read_buffer.Length);
                if (0 == read)
                    break;
                checksum.Update (read_buffer, 0, read);
                dst.Write (read_buffer, 0, read);
            }
            return checksum.Value;
        }

        bool ShouldCompressFile (Entry entry)
        {
            if ("image" == entry.Type || "archive" == entry.Type)
                return false;
            var ext = Path.GetExtension (entry.Name);
            if (!string.IsNullOrEmpty (ext) && ext.Equals (".ogg", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
    }

    internal class Xp3Stream : Stream
    {
        ArcView     m_file;
        Xp3Entry    m_entry;
        IEnumerator<Xp3Segment> m_segment;
        Stream      m_stream;
        long        m_offset = 0;
        bool        m_eof = false;

        public override bool CanRead  { get { return true; } }
        public override bool CanSeek  { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length   { get { return m_entry.UnpackedSize; } }
        public override long Position
        {
            get { return m_offset; }
            set { throw new NotSupportedException ("Xp3Stream.Position not supported."); }
        }

        public Xp3Stream (ArcView file, Xp3Entry entry)
        {
            m_file = file;
            m_entry = entry;
            m_segment = entry.Segments.GetEnumerator();
            NextSegment();
        }

        private void NextSegment ()
        {
            if (!m_segment.MoveNext())
            {
                m_eof = true;
                return;
            }
            if (null != m_stream)
                m_stream.Dispose();
            var segment = m_segment.Current;
            if (segment.IsCompressed)
                m_stream = new ZLibStream (m_file.CreateStream (segment.Offset, segment.PackedSize),
                                           CompressionMode.Decompress);
            else
                m_stream = m_file.CreateStream (segment.Offset, segment.Size);
        }

        public override int Read (byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (!m_eof && count > 0)
            {
                int read = m_stream.Read (buffer, offset, count);
                m_entry.Cipher.Decrypt (m_entry, m_offset, buffer, offset, read);
                m_offset += read;
                total += read;
                offset += read;
                count -= read;
                if (0 != count)
                    NextSegment();
            }
            return total;
        }

        public override int ReadByte ()
        {
            int b = -1;
            while (!m_eof)
            {
                b = m_stream.ReadByte();
                if (-1 != b)
                {
                    b = m_entry.Cipher.Decrypt (m_entry, m_offset++, (byte)b);
                    break;
                }
                NextSegment();
            }
            return b;
        }

        public override void Flush ()
        {
        }

        public override long Seek (long offset, SeekOrigin origin)
        {
            throw new NotSupportedException ("Xp3Stream.Seek method is not supported");
        }

        public override void SetLength (long length)
        {
            throw new NotSupportedException ("Xp3Stream.SetLength method is not supported");
        }

        public override void Write (byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException ("Xp3Stream.Write method is not supported");
        }

        public override void WriteByte (byte value)
        {
            throw new NotSupportedException("Xp3Stream.WriteByte method is not supported");
        }

        #region IDisposable Members
        bool disposed = false;
        protected override void Dispose (bool disposing)
        {
            if (!disposed)
            {
                m_file = null;
                if (null != m_stream)
                {
                    m_stream.Dispose();
                    m_stream = null;
                }
                disposed = true;
                base.Dispose (disposing);
            }
        }
        #endregion
    }

    public abstract class ICrypt
    {
        /// <summary>
        /// whether Adler32 checksum should be calculated after contents have been encrypted.
        /// </summary>
        public virtual bool HashAfterCrypt { get { return false; } }

        public abstract byte Decrypt (Xp3Entry entry, long offset, byte value);

        public virtual void Decrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            for (int i = 0; i < count; ++i)
                values[pos+i] = this.Decrypt (entry, offset+i, values[pos+i]);
        }

        public virtual void Encrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            throw new NotImplementedException (arcStrings.MsgEncNotImplemented);
        }
    }

    public class NoCrypt : ICrypt
    {
        public override byte Decrypt (Xp3Entry entry, long offset, byte value)
        {
            return value;
        }
        public override void Decrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            return;
        }
        public override void Encrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            return;
        }
    }

    internal class FateCrypt : ICrypt
    {
        public override bool HashAfterCrypt { get { return true; } }

        public override byte Decrypt (Xp3Entry entry, long offset, byte value)
        {
            byte result = (byte)(value ^ 0x36);
            if (0x13 == offset)
                result ^= 1;
            else if (0x2ea29 == offset)
                result ^= 3;
            return result;
        }

        public override void Decrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            for (int i = 0; i < count; ++i)
            {
                values[pos+i] ^= 0x36;
            }
            if (offset > 0x2ea29)
                return;
            if (offset + count > 0x2ea29)
                values[pos+0x2ea29-offset] ^= 3;
            if (offset > 0x13)
                return;
            if (offset + count > 0x13)
                values[pos+0x13-offset] ^= 1;
        }

        public override void Encrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            Decrypt (entry, offset, values, pos, count);
        }
    }

    internal class XorCrypt : ICrypt
    {
        private byte m_key;

        public byte Key
        {
            get { return m_key; }
            set { m_key = value; }
        }

        public XorCrypt (uint key)
        {
            m_key = (byte)key;
        }

        public override byte Decrypt (Xp3Entry entry, long offset, byte value)
        {
            return (byte)(value ^ m_key);
        }

        public override void Decrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            for (int i = 0; i < count; ++i)
            {
                values[pos+i] ^= m_key;
            }
        }

        public override void Encrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            Decrypt (entry, offset, values, pos, count);
        }
    }

    internal class SwanSongCrypt : ICrypt
    {
        static private byte Adjust (uint hash, out int shift)
        {
            int cl = (int)(hash & 0xff);
            if (0 == cl) cl = 0x0f;
            shift = cl & 7;
            int ch = (int)((hash >> 8) & 0xff);
            if (0 == ch) ch = 0xf0;
            return (byte)ch;
        }

        public override byte Decrypt (Xp3Entry entry, long offset, byte value)
        {
            int shift;
            byte xor = Adjust (entry.Hash, out shift);
            uint data = (uint)(value ^ xor);
            return (byte)((data >> shift) | (data << (8 - shift)));
        }

        public override void Decrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            int shift;
            byte xor = Adjust (entry.Hash, out shift);
            for (int i = 0; i < count; ++i)
            {
                uint data = (uint)(values[pos+i] ^ xor);
                values[pos+i] = (byte)((data >> shift) | (data << (8 - shift)));
            }
        }

        public override void Encrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            int shift;
            byte xor = Adjust (entry.Hash, out shift);
            for (int i = 0; i < count; ++i)
            {
                uint data = values[pos+i];
                data = (byte)((data << shift) | (data >> (8 - shift)));
                values[pos+i] = (byte)(data ^ xor);
            }
        }
    }

    internal class SeitenCrypt : ICrypt
    {
        public override byte Decrypt (Xp3Entry entry, long offset, byte value)
        {
            uint key = entry.Hash ^ (uint)offset;
            if (0 != (key & 2))
            {
                int ecx = (int)key & 0x18;
                value ^= (byte)((key >> ecx) | (key >> (ecx & 8)));
            }
            if (0 != (key & 4))
            {
                value += (byte)key;
            }
            if (0 != (key & 8))
            {
                value -= (byte)(key >> (int)(key & 0x10));
            }
            return value;
        }

        public override void Encrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            for (int i = 0; i < count; ++i)
            {
                uint key = entry.Hash ^ (uint)offset;
                if (0 != (key & 8))
                {
                    values[pos+i] += (byte)(key >> (int)(key & 0x10));
                }
                if (0 != (key & 4))
                {
                    values[pos+i] -= (byte)key;
                }
                if (0 != (key & 2))
                {
                    int ecx = (int)key & 0x18;
                    values[pos+i] ^= (byte)((key >> ecx) | (key >> (ecx & 8)));
                }
            }
        }
    }

    internal class OkibaCrypt : ICrypt
    {
        public override byte Decrypt (Xp3Entry entry, long offset, byte value)
        {
            if (offset < 0x65)
                return (byte)(value ^ (byte)(entry.Hash >> 4));
            uint key = entry.Hash;
            // 0,1,2,3 -> 1,0,3,2
            key = ((key & 0xff0000) << 8) | ((key & 0xff000000) >> 8)
                | ((key & 0xff00) >> 8)   | ((key & 0xff) << 8);
            key >>= 8 * ((int)(offset - 0x65) & 3);
            return (byte)(value ^ (byte)key);
        }

        public override void Decrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            int i = 0;
            if (offset < 0x65)
            {
                uint key = entry.Hash >> 4;
                int limit = Math.Min (count, (int)(0x65 - offset));
                for (; i < limit; ++i)
                {
                    values[pos+i] ^= (byte)key;
                    ++offset;
                }
            }
            if (i < count)
            {
                offset -= 0x65;
                uint key = entry.Hash;
                key = ((key & 0xff0000) << 8) | ((key & 0xff000000) >> 8)
                    | ((key & 0xff00) >> 8)   | ((key & 0xff) << 8);
                do
                {
                    values[pos+i] ^= (byte)(key >> (8 * ((int)offset & 3)));
                    ++offset;
                }
                while (++i < count);
            }
        }

        public override void Encrypt (Xp3Entry entry, long offset, byte[] values, int pos, int count)
        {
            Decrypt (entry, offset, values, pos, count);
        }
    }
}
