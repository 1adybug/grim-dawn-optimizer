using System.Buffers.Binary;
using System.Text;

namespace GrimDawnOptimizer.Core.Parsing;

/// <summary>
/// Read-only Grim Dawn save/stash stream decoder. The key schedule follows the MIT licensed
/// Grim Dawn Item Assistant implementation; no write operation is intentionally exposed.
/// </summary>
internal sealed class GdCryptoReader
{
    private const uint XorKey = 1431655765u;
    private const uint Prime = 39916801u;
    private readonly byte[] _data;
    private readonly uint[] _table = new uint[256];
    private uint _key;

    public GdCryptoReader(byte[] data) => _data = data;
    public int Position { get; private set; }
    public int Remaining => _data.Length - Position;

    public void ReadKey()
    {
        var encoded = ReadRawUInt32();
        _key = encoded ^ XorKey;
        var seed = _key;
        for (var i = 0; i < _table.Length; i++)
        {
            seed = (seed >> 1) | (seed << 31);
            seed *= Prime;
            _table[i] = seed;
        }
    }

    public uint ReadUInt32(bool updateKey = true)
    {
        var raw = ReadRawUInt32();
        var value = raw ^ _key;
        if (updateKey) UpdateKey(raw);
        return value;
    }

    public int ReadInt32(bool updateKey = true) => unchecked((int)ReadUInt32(updateKey));
    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());
    public bool ReadBoolean() => ReadByte() == 1;

    public byte ReadByte()
    {
        Ensure(1);
        var raw = _data[Position++];
        var value = (byte)(raw ^ _key);
        _key ^= _table[raw];
        return value;
    }

    public string ReadString()
    {
        var length = checked((int)ReadUInt32());
        if (length == 0) return string.Empty;
        return Encoding.ASCII.GetString(ReadDecryptedBytes(length));
    }

    public string ReadWideString()
    {
        var length = checked((int)ReadUInt32());
        if (length == 0) return string.Empty;
        return Encoding.Unicode.GetString(ReadDecryptedBytes(checked(length * 2))).TrimEnd('\0');
    }

    public byte[] ReadUid() => ReadDecryptedBytes(16);

    public BlockScope BeginBlock(int? expectedId = null)
    {
        var id = ReadInt32();
        var length = ReadInt32(updateKey: false);
        if (length < 0 || length > Remaining - 4) throw new InvalidDataException($"无效区块长度：{length}");
        if (expectedId is { } expected && id != expected)
            throw new InvalidDataException($"期望区块 {expected}，实际为 {id}");
        return new BlockScope(id, Position + length);
    }

    public void EndBlock(BlockScope block)
    {
        if (Position < block.ContentEnd) SkipEncrypted(block.ContentEnd - Position);
        if (Position != block.ContentEnd) throw new InvalidDataException($"区块 {block.Id} 读取越界");
        var checksum = ReadInt32(updateKey: false);
        if (checksum != 0) throw new InvalidDataException($"区块 {block.Id} 校验失败");
    }

    public void SkipEncrypted(int byteCount)
    {
        Ensure(byteCount);
        for (var i = 0; i < byteCount; i++)
        {
            var raw = _data[Position++];
            _key ^= _table[raw];
        }
    }

    private byte[] ReadDecryptedBytes(int count)
    {
        Ensure(count);
        var result = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var raw = _data[Position++];
            result[i] = (byte)(raw ^ _key);
            _key ^= _table[raw];
        }
        return result;
    }

    private uint ReadRawUInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    private void UpdateKey(uint raw)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, raw);
        foreach (var value in bytes) _key ^= _table[value];
    }

    private void Ensure(int count)
    {
        if (count < 0 || Position > _data.Length - count) throw new EndOfStreamException();
    }

    internal readonly record struct BlockScope(int Id, int ContentEnd);
}
