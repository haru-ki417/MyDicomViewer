using System.Buffers.Binary;
using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.IO;

namespace MyDicomViewer.Core.Dicom;

/// <summary>タグ一覧の1行</summary>
public sealed record DicomTagEntry(string Tag, string Name, string Vr, string Value, int Depth)
{
    /// <summary>入れ子（シーケンス）の深さに応じて字下げした名前</summary>
    public string IndentedName => new string(' ', Depth * 4) + Name;
}

/// <summary>DICOM データセットの全タグを、表示用の文字列に変換する</summary>
public static class DicomTagLister
{
    private const int MaxValues = 16;

    public static IReadOnlyList<DicomTagEntry> List(DicomDataset dataset, int maxValueLength = 256)
    {
        var entries = new List<DicomTagEntry>();
        bool bigEndian = dataset.InternalTransferSyntax.Endian == Endian.Big;
        AddItems(dataset, 0, entries, bigEndian, maxValueLength);
        return entries;
    }

    private static void AddItems(DicomDataset dataset, int depth, List<DicomTagEntry> entries, bool bigEndian, int maxValueLength)
    {
        foreach (DicomItem item in dataset)
        {
            string tag = $"({item.Tag.Group:X4},{item.Tag.Element:X4})";
            string name = item.Tag.IsPrivate ? "Private Tag" : item.Tag.DictionaryEntry.Name;
            string vr = item.ValueRepresentation.Code;

            switch (item)
            {
                case DicomSequence sequence:
                    entries.Add(new DicomTagEntry(tag, name, vr, $"({sequence.Items.Count} items)", depth));
                    for (int i = 0; i < sequence.Items.Count; i++)
                    {
                        entries.Add(new DicomTagEntry("", $"Item #{i + 1}", "", "", depth + 1));
                        AddItems(sequence.Items[i], depth + 2, entries, bigEndian, maxValueLength);
                    }
                    break;

                case DicomFragmentSequence fragments:
                    entries.Add(new DicomTagEntry(tag, name, vr, $"<圧縮画像データ: {fragments.Fragments.Count} fragments>", depth));
                    break;

                case DicomElement element:
                    entries.Add(new DicomTagEntry(tag, name, vr, Truncate(FormatValue(dataset, element, bigEndian), maxValueLength), depth));
                    break;
            }
        }
    }

    internal static string FormatValue(DicomDataset dataset, DicomElement element, bool bigEndian)
    {
        if (element.Tag == DicomTag.PixelData) return $"<画像データ: {element.Buffer.Size:N0} bytes>";
        if (element.ValueRepresentation.IsString)
        {
            try { return dataset.GetString(element.Tag); }
            catch (DicomDataException) { return "<読み取れない値>"; }
        }

        byte[] data = element.Buffer.Data;
        return element.ValueRepresentation.Code switch
        {
            "US" => Join(data, 2, (s) => (bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s)).ToString(CultureInfo.InvariantCulture)),
            "SS" => Join(data, 2, (s) => (bigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s)).ToString(CultureInfo.InvariantCulture)),
            "UL" => Join(data, 4, (s) => (bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s)).ToString(CultureInfo.InvariantCulture)),
            "SL" => Join(data, 4, (s) => (bigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s)).ToString(CultureInfo.InvariantCulture)),
            "FL" => Join(data, 4, (s) => (bigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s)).ToString("G6", CultureInfo.InvariantCulture)),
            "FD" => Join(data, 8, (s) => (bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s)).ToString("G10", CultureInfo.InvariantCulture)),
            "AT" => Join(data, 4, (s) =>
            {
                ushort g = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
                ushort e = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s[2..]) : BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
                return $"({g:X4},{e:X4})";
            }),
            _ => $"<バイナリ: {data.Length:N0} bytes>",
        };
    }

    private delegate string Reader(ReadOnlySpan<byte> span);

    private static string Join(byte[] data, int size, Reader read)
    {
        int count = data.Length / size;
        var parts = new List<string>(Math.Min(count, MaxValues));
        for (int i = 0; i < count && i < MaxValues; i++)
        {
            parts.Add(read(data.AsSpan(i * size, size)));
        }
        string text = string.Join("\\", parts);
        return count > MaxValues ? $"{text}\\… ({count} values)" : text;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
