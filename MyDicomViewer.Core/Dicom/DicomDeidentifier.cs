using FellowOakDicom;

namespace MyDicomViewer.Core.Dicom;

/// <summary>匿名化の結果。PseudonymId は元の患者と結び付かない仮の ID。</summary>
public sealed record DeidentificationResult(DicomDataset Dataset, string PseudonymId);

/// <summary>
/// DICOM の匿名化。DICOM 規格 PS3.15 の Basic Application Level Confidentiality Profile
/// （fo-dicom の DicomAnonymizer）で個人情報を削除・置換したうえで、仮の患者名・ID を付ける。
/// 元のデータセットは変更しない。
/// </summary>
public static class DicomDeidentifier
{
    public const string AnonymousPatientName = "ANONYMOUS_USER";
    public const string PseudonymPrefix = "ID-";

    public static DeidentificationResult Deidentify(DicomDataset source)
    {
        var anonymizer = new DicomAnonymizer();
        DicomDataset dataset = anonymizer.Anonymize(source);

        string pseudonym = PseudonymPrefix + Guid.NewGuid().ToString("N")[..8];
        dataset.AddOrUpdate(DicomTag.PatientName, AnonymousPatientName);
        dataset.AddOrUpdate(DicomTag.PatientID, pseudonym);
        dataset.AddOrUpdate(DicomTag.PatientIdentityRemoved, "YES");
        dataset.AddOrUpdate(DicomTag.DeidentificationMethod, "PS3.15 Basic Profile; pseudonymized");

        return new DeidentificationResult(dataset, pseudonym);
    }
}
