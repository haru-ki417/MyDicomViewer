using System.Buffers.Binary;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace MyDicomViewer.Tests;

/// <summary>テスト用の小さな DICOM をコード上で作る（実在の患者データは使わない）</summary>
internal static class TestDicom
{
    static TestDicom()
    {
        new DicomSetupBuilder()
            .RegisterServices(s => s.AddFellowOakDicom())
            .SkipValidation()
            .Build();
    }

    public static DicomDataset Create16Bit(
        int width, int height, Func<int, int, ushort> pixel,
        string photometric = "MONOCHROME2", decimal slope = 1m, decimal intercept = 0m)
    {
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.PatientName, "Yamada^Taro" },
            { DicomTag.PatientID, "PAT-12345" },
            { DicomTag.PatientBirthDate, "19800101" },
            { DicomTag.ReferringPhysicianName, "Suzuki^Ichiro" },
            { DicomTag.InstitutionName, "Example Hospital" },
            { DicomTag.Modality, "CR" },
            { DicomTag.PhotometricInterpretation, photometric },
            { DicomTag.Rows, (ushort)height },
            { DicomTag.Columns, (ushort)width },
            { DicomTag.BitsAllocated, (ushort)16 },
            { DicomTag.BitsStored, (ushort)16 },
            { DicomTag.HighBit, (ushort)15 },
            { DicomTag.PixelRepresentation, (ushort)0 },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.RescaleSlope, slope },
            { DicomTag.RescaleIntercept, intercept },
        };

        var bytes = new byte[width * height * 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((y * width + x) * 2), pixel(x, y));
            }
        }

        var pixelData = DicomPixelData.Create(ds, true);
        pixelData.AddFrame(new MemoryByteBuffer(bytes));
        return ds;
    }

    /// <summary>左上から右下へ明るくなるグラデーション</summary>
    public static DicomDataset Gradient(int width, int height, string photometric = "MONOCHROME2", decimal slope = 1m, decimal intercept = 0m) =>
        Create16Bit(width, height, (x, y) => (ushort)(100 + 40 * x + 7 * y), photometric, slope, intercept);
}
