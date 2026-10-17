namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>
/// 多分卷归档的只读拼接流（终末地 packs 的 <c>…zip.001/002/…</c> 形态）：把按分卷顺序排列的
/// 物理文件呈现为一个连续、可寻址的流，交给 <see cref="System.IO.Compression.ZipArchive"/> 读取。
/// 不落盘拼接副本——数十 GB 的分卷归档解压既不需要等量临时磁盘，也不需要整卷复制时间；
/// 读取时按需打开所在分卷、顺序读取不重复开关文件。
/// 非线程安全：对应单个归档读取器的顺序访问语义，调用方不得并发使用。
/// </summary>
internal sealed class SplitVolumeStream : Stream
{
    /// <summary>分卷路径（顺序即拼接顺序）。</summary>
    private readonly string[] _volumePaths;

    /// <summary>各分卷在拼接空间中的起始偏移；长度 = 分卷数 + 1，末项为拼接总长。</summary>
    private readonly long[] _volumeStarts;

    /// <summary>当前打开的分卷句柄与其下标（切换分卷时关掉上一卷）。</summary>
    private FileStream? _openVolume;
    private int _openVolumeIndex = -1;

    /// <summary>当前读取位置（拼接空间，自 0 起）。</summary>
    private long _position;

    /// <summary>按分卷顺序构造拼接流；分卷长度在构造时快照（<see cref="Length"/> 为快照合计）。</summary>
    public SplitVolumeStream(IReadOnlyList<string> volumePaths)
    {
        ArgumentNullException.ThrowIfNull(volumePaths);
        _volumePaths = [.. volumePaths];
        _volumeStarts = new long[_volumePaths.Length + 1];
        for (var i = 0; i < _volumePaths.Length; i++)
        {
            _volumeStarts[i + 1] = _volumeStarts[i] + new FileInfo(_volumePaths[i]).Length;
        }
    }

    /// <summary>只读流。</summary>
    public override bool CanRead => true;

    /// <summary>支持随机访问（ZipArchive 需先定位到末卷的中央目录）。</summary>
    public override bool CanSeek => true;

    /// <summary>不支持写入。</summary>
    public override bool CanWrite => false;

    /// <summary>拼接后的总字节数（各分卷长度之和，构造时快照）。</summary>
    public override long Length => _volumeStarts[^1];

    /// <summary>当前读取位置；负值拒绝（与 FileStream 的 Seek 语义一致）。</summary>
    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
            {
                throw new IOException("Cannot position a split volume stream before its beginning.");
            }

            _position = value;
        }
    }

    /// <summary>字节数组读取重载：转交 <see cref="Read(Span{byte})"/>（避免基类的数组中转开销）。</summary>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>从当前位置读取，跨卷边界自动续读下一卷；读到拼接总长或分卷被外部截断即 EOF。</summary>
    public override int Read(Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length && _position < Length)
        {
            var index = VolumeIndexAt(_position);
            var volume = OpenVolume(index);
            var offsetInVolume = _position - _volumeStarts[index];
            if (volume.Position != offsetInVolume)
            {
                volume.Seek(offsetInVolume, SeekOrigin.Begin);
            }

            var want = (int)Math.Min(buffer.Length - read, _volumeStarts[index + 1] - _position);
            var got = volume.Read(buffer[read..(read + want)]);
            if (got == 0)
            {
                break; // 分卷被外部截断：以 EOF 收尾，由归档读取器按损坏折算
            }

            read += got;
            _position += got;
        }

        return read;
    }

    /// <summary>定位到拼接空间的任意偏移（中央目录读取等随机访问）。</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    /// <summary>只读流：无缓冲可冲刷。</summary>
    public override void Flush()
    {
    }

    /// <summary>只读流：不支持改变长度。</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>只读流：不支持写入。</summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>释放当前打开的分卷句柄。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _openVolume?.Dispose();
            _openVolume = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>位置所在的分卷下标（分卷数最多百量级，线性扫描足够；首卷起点为 0，
    /// 位置非负，故循环必然命中 >= 1 的下标或回落到首卷）。</summary>
    private int VolumeIndexAt(long position)
    {
        for (var i = _volumePaths.Length - 1; i > 0; i--)
        {
            if (position >= _volumeStarts[i])
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>打开指定分卷的文件句柄（复用同一分卷的已有句柄，切换时关掉上一卷）。</summary>
    private FileStream OpenVolume(int index)
    {
        if (_openVolume is null || _openVolumeIndex != index)
        {
            _openVolume?.Dispose();
            _openVolume = new FileStream(_volumePaths[index], FileMode.Open, FileAccess.Read, FileShare.Read);
            _openVolumeIndex = index;
        }

        return _openVolume;
    }
}
