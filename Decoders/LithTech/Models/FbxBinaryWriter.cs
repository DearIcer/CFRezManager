using System.IO;
using System.Text;

namespace CFRezManager;

/// <summary>
/// One FBX binary node record: a name, a property list, and nested child records.
/// Property values are typed by their C# type: short ('Y'), bool ('C'), int ('I'),
/// long ('L'), float ('F'), double ('D'), string ('S'), byte[] ('R' raw),
/// int[]/long[]/float[]/double[] ('i'/'l'/'f'/'d' arrays, always raw/uncompressed).
/// </summary>
internal sealed class FbxNode
{
    private static readonly byte[] SentinelBytes = new byte[13];
    private readonly byte[] _nameBytes;

    public FbxNode(string name, params object[] properties)
    {
        _nameBytes = Encoding.ASCII.GetBytes(name);
        if (_nameBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException($"FBX node name '{name}' is too long.");
        }

        foreach (object property in properties)
        {
            AddProperty(property);
        }
    }

    public string Name => Encoding.ASCII.GetString(_nameBytes);

    public List<object> Properties { get; } = [];

    public List<FbxNode> Children { get; } = [];

    // Blender hardcodes a trailing NULL sentinel for these two node types even when empty.
    private bool NeedsSentinel => Children.Count > 0 || Name is "AnimationStack" or "AnimationLayer";

    internal long EndOffset { get; private set; }

    private int PropertyBytes { get; set; }

    public FbxNode AddChild(FbxNode child)
    {
        Children.Add(child);
        return child;
    }

    public void AddProperty(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (value)
        {
            case short or bool or int or long or float or double or string or byte[] or int[] or long[] or float[] or double[]:
                Properties.Add(value);
                break;
            default:
                throw new ArgumentException($"Unsupported FBX property type {value.GetType().Name}.");
        }
    }

    /// <summary>
    /// First pass: computes the absolute end offset of this record (and all descendants)
    /// starting at <paramref name="offset"/>, and returns that end offset.
    /// </summary>
    internal long Measure(long offset)
    {
        offset += 13 + _nameBytes.Length; // 3 x uint32 metadata + uint8 name length + name
        int propertyBytes = 0;
        foreach (object property in Properties)
        {
            propertyBytes += 1 + GetPayloadSize(property);
        }

        PropertyBytes = propertyBytes;
        offset += propertyBytes;
        foreach (FbxNode child in Children)
        {
            child.Measure(offset);
            offset = child.EndOffset;
        }

        if (NeedsSentinel)
        {
            offset += SentinelBytes.Length;
        }

        EndOffset = offset;
        return offset;
    }

    internal void Write(BinaryWriter writer)
    {
        writer.Write((uint)EndOffset);
        writer.Write((uint)Properties.Count);
        writer.Write((uint)PropertyBytes);
        writer.Write((byte)_nameBytes.Length);
        writer.Write(_nameBytes);
        foreach (object property in Properties)
        {
            WriteProperty(writer, property);
        }

        foreach (FbxNode child in Children)
        {
            child.Write(writer);
        }

        if (NeedsSentinel)
        {
            writer.Write(SentinelBytes);
        }

        if (writer.BaseStream.Position != EndOffset)
        {
            throw new InvalidDataException($"FBX node '{Name}' measured {EndOffset} but wrote {writer.BaseStream.Position}.");
        }
    }

    private static int GetPayloadSize(object value)
    {
        return value switch
        {
            short => sizeof(short),
            bool => sizeof(byte),
            int => sizeof(int),
            long => sizeof(long),
            float => sizeof(float),
            double => sizeof(double),
            string text => sizeof(uint) + Encoding.UTF8.GetByteCount(text),
            byte[] raw => sizeof(uint) + raw.Length,
            int[] array => 3 * sizeof(uint) + array.Length * sizeof(int),
            long[] array => 3 * sizeof(uint) + array.Length * sizeof(long),
            float[] array => 3 * sizeof(uint) + array.Length * sizeof(float),
            double[] array => 3 * sizeof(uint) + array.Length * sizeof(double),
            _ => throw new ArgumentException($"Unsupported FBX property type {value.GetType().Name}.")
        };
    }

    private static void WriteProperty(BinaryWriter writer, object value)
    {
        switch (value)
        {
            case short number:
                writer.Write((byte)'Y');
                writer.Write(number);
                break;
            case bool flag:
                writer.Write((byte)'C');
                writer.Write(flag ? (byte)1 : (byte)0);
                break;
            case int number:
                writer.Write((byte)'I');
                writer.Write(number);
                break;
            case long number:
                writer.Write((byte)'L');
                writer.Write(number);
                break;
            case float number:
                writer.Write((byte)'F');
                writer.Write(number);
                break;
            case double number:
                writer.Write((byte)'D');
                writer.Write(number);
                break;
            case string text:
            {
                writer.Write((byte)'S');
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                writer.Write((uint)bytes.Length);
                writer.Write(bytes);
                break;
            }
            case byte[] raw:
                writer.Write((byte)'R');
                writer.Write((uint)raw.Length);
                writer.Write(raw);
                break;
            case int[] array:
                WriteArrayHeader(writer, (byte)'i', array.Length);
                foreach (int item in array)
                {
                    writer.Write(item);
                }

                break;
            case long[] array:
                WriteArrayHeader(writer, (byte)'l', array.Length);
                foreach (long item in array)
                {
                    writer.Write(item);
                }

                break;
            case float[] array:
                WriteArrayHeader(writer, (byte)'f', array.Length);
                foreach (float item in array)
                {
                    writer.Write(item);
                }

                break;
            case double[] array:
                WriteArrayHeader(writer, (byte)'d', array.Length);
                foreach (double item in array)
                {
                    writer.Write(item);
                }

                break;
            default:
                throw new ArgumentException($"Unsupported FBX property type {value.GetType().Name}.");
        }
    }

    private static void WriteArrayHeader(BinaryWriter writer, byte code, int count)
    {
        writer.Write(code);
        writer.Write((uint)count);
        writer.Write(0u); // encoding: raw, uncompressed
        writer.Write((uint)count * GetArrayElementSize(code));
    }

    private static uint GetArrayElementSize(byte code)
    {
        return code switch
        {
            (byte)'i' or (byte)'f' => 4u,
            (byte)'l' or (byte)'d' => 8u,
            (byte)'b' => 1u,
            _ => throw new ArgumentException("Unknown FBX array code.")
        };
    }
}

/// <summary>
/// Writes FBX 7.4 binary files. Clean-room implementation of the node-record layout
/// documented in the public FBX binary format specification (header, record metadata,
/// property encodings, NULL sentinels, and the fixed footer block).
/// </summary>
internal static class FbxBinaryWriter
{
    public const int Version7400 = 7400;

    // "Kaydara FBX Binary  \x00" + 0x1A 0x00.
    private static readonly byte[] HeaderMagic =
    [
        0x4B, 0x61, 0x79, 0x64, 0x61, 0x72, 0x61, 0x20, 0x46, 0x42, 0x58, 0x20,
        0x42, 0x69, 0x6E, 0x61, 0x72, 0x79, 0x20, 0x20, 0x00, 0x1A, 0x00
    ];

    // Fixed footer block identifiers. The FBX checksum rules tie these to the file
    // creation time, so (like Blender) files are written with a fixed fake identity.
    private static readonly byte[] FooterId =
        [0xFA, 0xBC, 0xAB, 0x09, 0xD0, 0xC8, 0xD4, 0x66, 0xB1, 0x76, 0xFB, 0x83, 0x1C, 0xF7, 0x26, 0x7E];

    private static readonly byte[] FooterMagic =
        [0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E, 0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B];

    // Fixed fake values for the FileId / CreationTime entries (FBX checksums bind to them).
    public static byte[] FixedFileId { get; } =
        [0x28, 0xB3, 0x2A, 0xEB, 0xB6, 0x24, 0xCC, 0xC2, 0xBF, 0xC8, 0xB0, 0x2A, 0xA9, 0x2B, 0xFC, 0xF1];

    public const string FixedCreationTime = "1970-01-01 10:00:00:000";

    public static void Write(string path, IReadOnlyList<FbxNode> topLevelNodes)
    {
        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(HeaderMagic);
        writer.Write((uint)Version7400);

        long offset = stream.Position;
        foreach (FbxNode node in topLevelNodes)
        {
            node.Measure(offset);
            offset = node.EndOffset;
        }

        foreach (FbxNode node in topLevelNodes)
        {
            node.Write(writer);
        }

        // One final NULL sentinel closes the top-level record list.
        writer.Write(new byte[13]);

        // Footer: identifier, padding to a 16-byte boundary (a full 16 when already
        // aligned), the version repeated, 120 zero bytes, and the trailing magic.
        writer.Write(FooterId);
        writer.Write(new byte[4]);
        long position = stream.Position;
        long padding = ((position + 15) & ~15L) - position;
        if (padding == 0)
        {
            padding = 16;
        }

        writer.Write(new byte[padding]);
        writer.Write((uint)Version7400);
        writer.Write(new byte[120]);
        writer.Write(FooterMagic);
    }
}
