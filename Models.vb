Imports System.Globalization
Imports System.IO

Public Enum QualityMetric
    Vmaf
    Xpsnr
End Enum

Public Module QualityMetricHelpers

    Public Function GetMetricDisplayName(metric As QualityMetric) As String
        Return If(metric = QualityMetric.Xpsnr, "XPSNR", "VMAF")
    End Function

    Public Function GetMetricJsonPropertyName(metric As QualityMetric) As String
        Return If(metric = QualityMetric.Xpsnr, "xpsnr", "vmaf")
    End Function

End Module

Public NotInheritable Class SearchSettings

    Public Property Metric As QualityMetric = QualityMetric.Vmaf

    Public Property TargetScore As Double = 95

    Public Property MinCrf As Double = 5

    Public Property MaxCrf As Double = 55

    Public Property Samples As Integer?

    Public Property SampleDuration As String = "20s"

    Public Property Thorough As Boolean

    ''' <summary>
    ''' VMAF 内置模型名称或本地 JSON 路径。留空时完全采用 ab-av1 的自动模型逻辑。
    ''' </summary>
    Public Property VmafModel As String = String.Empty

    Public Sub Validate()
        If Double.IsNaN(TargetScore) OrElse Double.IsInfinity(TargetScore) Then
            Throw New ArgumentOutOfRangeException(NameOf(TargetScore), "目标分数必须是有限数字。")
        End If
        If Metric = QualityMetric.Vmaf AndAlso (TargetScore <= 0 OrElse TargetScore > 100) Then
            Throw New ArgumentOutOfRangeException(NameOf(TargetScore), "目标 VMAF 必须大于 0 且不超过 100。")
        End If

        If Double.IsNaN(MinCrf) OrElse Double.IsInfinity(MinCrf) OrElse
           Double.IsNaN(MaxCrf) OrElse Double.IsInfinity(MaxCrf) OrElse
           MinCrf < 0 OrElse MinCrf >= MaxCrf Then
            Throw New ArgumentException("CRF 范围无效：最小值必须大于等于 0，并且小于最大值。")
        End If

        If Samples.HasValue AndAlso Samples.Value <= 0 Then
            Throw New ArgumentOutOfRangeException(NameOf(Samples), "采样数量必须是正整数。")
        End If

        If String.IsNullOrWhiteSpace(SampleDuration) Then
            Throw New ArgumentException("采样时长不能为空。", NameOf(SampleDuration))
        End If

        If Metric = QualityMetric.Vmaf Then ValidateVmafModel(VmafModel)
    End Sub

    Friend Shared Sub ValidateVmafModel(value As String)
        Dim model = If(value, String.Empty).Trim()
        Dim modelPath = model
        If model.StartsWith("path=", StringComparison.OrdinalIgnoreCase) Then
            modelPath = model.Substring("path=".Length).Trim()
        End If
        If modelPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) AndAlso Not File.Exists(modelPath) Then
            Throw New FileNotFoundException("找不到手动指定的 VMAF 模型文件。", modelPath)
        End If
    End Sub

    Public Shared Function FormatNumber(value As Double) As String
        Return value.ToString("0.###", CultureInfo.InvariantCulture)
    End Function

End Class

Public NotInheritable Class SampleEncodeSettings

    Public Property Metric As QualityMetric = QualityMetric.Vmaf

    Public Property Crf As Double = 30

    Public Property Samples As Integer?

    Public Property SampleDuration As String = "20s"

    Public Property VmafModel As String = String.Empty

    Public Sub Validate()
        If Double.IsNaN(Crf) OrElse Double.IsInfinity(Crf) OrElse Crf < 0 Then
            Throw New ArgumentOutOfRangeException(NameOf(Crf), "CRF 必须是大于等于 0 的有限数字。")
        End If
        If Samples.HasValue AndAlso Samples.Value <= 0 Then
            Throw New ArgumentOutOfRangeException(NameOf(Samples), "采样数量必须是正整数。")
        End If
        If String.IsNullOrWhiteSpace(SampleDuration) Then
            Throw New ArgumentException("采样时长不能为空。", NameOf(SampleDuration))
        End If
        If Metric = QualityMetric.Vmaf Then SearchSettings.ValidateVmafModel(VmafModel)
    End Sub

End Class

Public NotInheritable Class SearchResult

    Public Property Crf As Double

    Public Property Metric As QualityMetric = QualityMetric.Vmaf

    Public Property Score As Double

    Public Property PredictedEncodeSize As Long

    Public Property PredictedEncodeSeconds As Double

    Public Property PredictedEncodePercent As Double

End Class

Public NotInheritable Class SampleEncodeResult

    Public Property Crf As Double

    Public Property Metric As QualityMetric = QualityMetric.Vmaf

    Public Property Score As Double

    Public Property PredictedEncodeSize As Long

    Public Property PredictedEncodeSeconds As Double

    Public Property PredictedEncodePercent As Double

End Class

Public NotInheritable Class SearchProgress

    Public Sub New(message As String,
                   Optional testedCrf As Double? = Nothing,
                   Optional testedScore As Double? = Nothing,
                   Optional metric As QualityMetric? = Nothing)
        Me.Message = message
        Me.TestedCrf = testedCrf
        Me.TestedScore = testedScore
        Me.Metric = metric
    End Sub

    Public ReadOnly Property Message As String

    Public ReadOnly Property TestedCrf As Double?

    Public ReadOnly Property TestedScore As Double?

    Public ReadOnly Property Metric As QualityMetric?

End Class

Public NotInheritable Class PresetCompatibilityException
    Inherits InvalidOperationException

    Public Sub New(message As String)
        MyBase.New(message)
    End Sub
End Class
