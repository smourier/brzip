using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace BrZip
{
    // all this code requires Streams to be seekable
    public class BrZipWriteArchive : IDisposable
    {
        public const uint Signature = 0x315A5242; // 'BRZ1'
        public const int DefaultBufferSize = 0x10000;

        private bool _leaveOpen;
        private BinaryWriter _writer;
        private long _lastPos;

        public event EventHandler<BrZipArchiveProgressEventArgs> Progress;
        public event EventHandler<BrZipArchiveEventArgs> AddingEntry;
        public event EventHandler<BrZipArchiveEventArgs> AddedEntry;

        public BrZipWriteArchive()
        {
            BufferSize = DefaultBufferSize;
        }

        public Stream Stream { get; private set; }
        public int BufferSize { get; set; }

        public virtual void Open(string filePath)
        {
            if (filePath == null)
                throw new ArgumentNullException(nameof(filePath));

            Open(File.OpenWrite(filePath));
        }

        public virtual void Open(Stream stream, bool leaveOpen = false)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            Stream = stream;
            _leaveOpen = leaveOpen;
            _writer = new BinaryWriter(Stream);
            _writer.Write(Signature);
            _lastPos = Stream.Position;
        }

        public virtual void AddDirectory(string directoryPath,
            string searchPattern = null,
            SearchOption searchOption = SearchOption.AllDirectories,
            Func<string, string, bool> includeFunc = null,
            Func<string, string, bool> excludeFunc = null,
            Func<string, string> nameFunc = null)
        {
            if (directoryPath == null)
                throw new ArgumentNullException(nameof(directoryPath));

            searchPattern = searchPattern ?? "*.*";
            directoryPath = Path.GetFullPath(directoryPath);
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, searchPattern, searchOption))
            {
                var relPath = filePath.Substring(directoryPath.Length + 1);
                if (excludeFunc != null && excludeFunc(filePath, relPath))
                    continue;

                if (includeFunc != null && !includeFunc(filePath, relPath))
                    continue;

                string name = null;
                if (nameFunc != null)
                {
                    name = nameFunc(relPath);
                }

                name = name ?? relPath;
                var e = new BrZipArchiveEventArgs(name, 0, filePath, relPath);
                OnAddingEntry(this, e);
                if (e.Cancel)
                    continue;

                // handle change of name
                if (e.Name != null && !string.Equals(e.Name, name, StringComparison.Ordinal))
                {
                    name = e.Name;
                }

                var len = AddEntry(filePath, name);
                OnAddedEntry(this, new BrZipArchiveEventArgs(name, len, filePath, relPath));
            }
        }

        public virtual long AddEntry(string filePath, string name = null)
        {
            if (filePath == null)
                throw new ArgumentNullException(nameof(filePath));

            name = name ?? Path.GetFileName(filePath);
            using (var stream = File.OpenRead(filePath))
            {
                return AddEntry(name, stream);
            }
        }

        public virtual long AddEntry(string name, Stream entryStream)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            if (entryStream == null)
                throw new ArgumentNullException(nameof(entryStream));

            Stream.Seek(_lastPos, SeekOrigin.Begin);
            var bytes = Encoding.UTF8.GetBytes(name);
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
            var uncompressedPos = Stream.Position;
            _writer.Write(0L); // uncompressed
            _writer.Write(0L); // compressed

            // write data
            var pos = Stream.Position;
            var entryPos = entryStream;
            var size = Math.Max(DefaultBufferSize, 1);
            var buffer = new byte[size];
            long entryLength = 0;
            using (var brotli = new BrotliStream(Stream, CompressionMode.Compress, leaveOpen: true))
            {
                do
                {
                    int read = entryStream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        break;

                    entryLength += read;
                    brotli.Write(buffer, 0, read);
                    brotli.Flush();
                    var e = new BrZipArchiveProgressEventArgs(name, entryLength, Stream.Position - pos);
                    OnProgress(this, e);
                    if (e.Cancel)
                        break; // file inside the archive will be truncated...
                }
                while (true);
            }
            _lastPos = Stream.Position;

            // go back and update lengths
            Stream.Seek(uncompressedPos, SeekOrigin.Begin);
            _writer.Write(entryLength);
            _writer.Write(_lastPos - pos);
            return _lastPos - pos;
        }

        protected virtual void OnProgress(object sender, BrZipArchiveProgressEventArgs e) => Progress?.Invoke(sender, e);
        protected virtual void OnAddedEntry(object sender, BrZipArchiveEventArgs e) => AddedEntry?.Invoke(sender, e);
        protected virtual void OnAddingEntry(object sender, BrZipArchiveEventArgs e) => AddingEntry?.Invoke(sender, e);

        public void Dispose()
        {
            if (!_leaveOpen)
            {
                Stream?.Dispose();
            }
        }
    }

    public class BrZipReadArchive : IDisposable
    {
        private bool _leaveOpen;
        private readonly ConcurrentDictionary<string, BrZipArchiveEntry> _entries = new ConcurrentDictionary<string, BrZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        private BinaryReader _reader;

        public BrZipReadArchive()
        {
            BufferSize = BrZipWriteArchive.DefaultBufferSize;
        }

        public Stream Stream { get; private set; }
        public int BufferSize { get; set; }

        public IReadOnlyDictionary<string, BrZipArchiveEntry> Entries => _entries;

        public virtual void Open(string filePath)
        {
            if (filePath == null)
                throw new ArgumentNullException(nameof(filePath));

            Open(File.OpenRead(filePath));
        }

        public virtual void Open(Stream stream, bool leaveOpen = false)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            Stream = stream;
            _leaveOpen = leaveOpen;
            _reader = new BinaryReader(stream);
            if (_reader.ReadUInt32() != BrZipWriteArchive.Signature)
                throw new InvalidDataException();

            do
            {
                if (Stream.Position == Stream.Length)
                    break;

                int bytesLength = _reader.ReadInt32();
                var bytes = _reader.ReadBytes(bytesLength);
                string name = Encoding.UTF8.GetString(bytes);
                var length = _reader.ReadInt64();
                var compressedLength = _reader.ReadInt64();
                var entry = CreateEntry(name, Stream.Position, compressedLength, length);
                _entries[entry.Name] = entry;
                Stream.Seek(entry.CompressedLength, SeekOrigin.Current);
            }
            while (true);
        }

        protected virtual BrZipArchiveEntry CreateEntry(string name, long offset, long compressedLength, long length) => new BrZipArchiveEntry(this, name, offset, compressedLength, length);

        public virtual void WriteToDirectory(string directoryPath)
        {
            if (directoryPath == null)
                throw new ArgumentNullException(nameof(directoryPath));

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            foreach (var entry in Entries)
            {
            }
        }

        public void Dispose()
        {
            if (!_leaveOpen)
            {
                Stream?.Dispose();
            }
        }
    }

    public class BrZipArchiveEntry
    {
        public event EventHandler<BrZipArchiveProgressEventArgs> Progress;

        public BrZipArchiveEntry(BrZipReadArchive archive, string name, long offset, long compressedLength, long length)
        {
            if (archive == null)
                throw new ArgumentNullException(nameof(archive));

            if (name == null)
                throw new ArgumentNullException(nameof(name));

            Archive = archive;
            Name = name;
            Offset = offset;
            CompressedLength = compressedLength;
            Length = length;
        }

        public BrZipReadArchive Archive { get; }
        public string Name { get; }
        public long Offset { get; }
        public long CompressedLength { get; }
        public long Length { get; }

        public override string ToString() => Name;

        protected virtual void OnProgress(object sender, BrZipArchiveProgressEventArgs e) => Progress?.Invoke(sender, e);

        public virtual async Task WriteAsync(string filePath)
        {
            if (filePath == null)
                throw new ArgumentNullException(nameof(filePath));

            var dir = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var file = File.OpenWrite(filePath))
            {
                await WriteAsync(file).ConfigureAwait(false);
            }
        }

        public virtual async Task WriteAsync(Stream destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            Archive.Stream.Seek(Offset, SeekOrigin.Begin);
            using (var brotli = new BrotliStream(Archive.Stream, CompressionMode.Decompress, leaveOpen: true))
            {
                var size = Math.Max(Archive.BufferSize, 1);
                var buffer = new byte[size];
                long entryLength = 0;
                do
                {
                    int read = await brotli.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    entryLength += read;
                    await destination.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    var e = new BrZipArchiveProgressEventArgs(Name, entryLength, destination.Position - Offset);
                    OnProgress(this, e);
                    if (e.Cancel)
                        return;
                }
                while (true);
            }
        }
    }

    public class BrZipArchiveProgressEventArgs : CancelEventArgs
    {
        public BrZipArchiveProgressEventArgs(string name, long inSize, long outSize)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            Name = name;
            InSize = inSize;
            OutSize = outSize;
        }

        public string Name { get; }
        public long InSize { get; }
        public long OutSize { get; }

        public override string ToString() => Name;
    }

    public class BrZipArchiveEventArgs : CancelEventArgs
    {
        public BrZipArchiveEventArgs(string name, long compressedLength = 0, string filePath = null, string relativePath = null)
        {
            if (name == null)
                throw new ArgumentNullException(name);

            Name = name;
            CompressedLength = compressedLength;
            FilePath = filePath;
            RelativePath = relativePath;
        }

        public string Name { get; set; }
        public string FilePath { get; }
        public long CompressedLength { get; }
        public string RelativePath { get; }

        public override string ToString() => Name;
    }
}
