Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions

''' <summary>
''' 读取 FFmpegFreeUI v6 JSON 预设，验证其视频处理链能否由 ab-av1 等价表示，
''' 并生成对应的 crf-search 参数。
''' </summary>
Public NotInheritable Class PresetProfile

    Private Shared ReadOnly JsonWriteOptions As New JsonSerializerOptions With {
        .WriteIndented = True,
        .Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }

    Private ReadOnly _root As JsonObject
    Private ReadOnly _svtArguments As New List(Of String)()
    Private ReadOnly _extraEncoderArguments As New List(Of String)()
    Private _effectivePreset As String
    Private _keyint As String
    Private _sceneChangeDetection As String

    Private Sub New(sourcePath As String, root As JsonObject)
        Me.SourcePath = sourcePath
        _root = root

        Encoder = GetString("视频参数_编码器_具体编码")
        _effectivePreset = GetString("视频参数_编码器_编码预设")
        PixelFormat = GetString("视频参数_色彩管理_像素格式")
        OutputContainer = GetString("输出容器")

        ValidateCompatibility()
        ParseAdditionalArguments(GetString("视频参数_质量控制_进阶参数集"), "质量控制进阶参数")
        ParseAdditionalArguments(GetString("自定义参数_视频参数"), "自定义视频参数")
    End Sub

    Public ReadOnly Property SourcePath As String

    Public ReadOnly Property Encoder As String

    Public ReadOnly Property PixelFormat As String

    Public ReadOnly Property OutputContainer As String

    Public ReadOnly Property EncoderPreset As String
        Get
            Return _effectivePreset
        End Get
    End Property

    Public ReadOnly Property SvtArgumentCount As Integer
        Get
            Return _svtArguments.Count
        End Get
    End Property

    Public Shared Function Load(sourceFilePath As String) As PresetProfile
        If String.IsNullOrWhiteSpace(sourceFilePath) Then Throw New ArgumentException("请选择 FFmpegFreeUI v6 预设。", NameOf(sourceFilePath))
        If Not File.Exists(sourceFilePath) Then Throw New FileNotFoundException("找不到 FFmpegFreeUI 预设文件。", sourceFilePath)

        Dim parsed = JsonNode.Parse(File.ReadAllText(sourceFilePath))
        Dim root = TryCast(parsed, JsonObject)
        If root Is Nothing Then Throw New InvalidDataException("预设文件的根节点不是 JSON 对象。")

        Return New PresetProfile(Path.GetFullPath(sourceFilePath), root)
    End Function

    Public Shared Function LoadJson(presetJson As String) As PresetProfile
        If String.IsNullOrWhiteSpace(presetJson) Then
            Throw New ArgumentException("FFmpegFreeUI 没有提供可供 ab-av1 分析的预设。", NameOf(presetJson))
        End If

        Dim parsed = JsonNode.Parse(presetJson)
        Dim root = TryCast(parsed, JsonObject)
        If root Is Nothing Then Throw New InvalidDataException("FFmpegFreeUI 预设的根节点不是 JSON 对象。")
        Return New PresetProfile(String.Empty, root)
    End Function

    Public Function GetSummary() As String
        Dim presetText = If(String.IsNullOrWhiteSpace(EncoderPreset), "默认", EncoderPreset)
        Dim pixelText = If(String.IsNullOrWhiteSpace(PixelFormat), "编码器默认", PixelFormat)
        Return $"{Encoder} · preset {presetText} · {pixelText} · {_svtArguments.Count} 个 SVT 高级参数"
    End Function

    Public Function BuildSearchArguments(inputPath As String,
                                         settings As SearchSettings,
                                         Optional jsonOutput As Boolean = False) As List(Of String)
        If Not File.Exists(inputPath) Then Throw New FileNotFoundException("找不到输入文件。", inputPath)
        settings.Validate()

        Return BuildSearchArgumentsCore(Path.GetFullPath(inputPath), settings, jsonOutput)
    End Function

    ''' <summary>生成使用输入文件占位符的命令行参数模板。</summary>
    Public Function BuildSearchArgumentTemplate(settings As SearchSettings,
                                                Optional jsonOutput As Boolean = False) As List(Of String)
        settings.Validate()
        Return BuildSearchArgumentsCore("<输入文件>", settings, jsonOutput)
    End Function

    Private Function BuildSearchArgumentsCore(inputArgument As String,
                                               settings As SearchSettings,
                                               jsonOutput As Boolean) As List(Of String)

        Dim arguments As New List(Of String) From {
            "crf-search",
            "--input", inputArgument,
            "--encoder", Encoder
        }

        If Not String.IsNullOrWhiteSpace(EncoderPreset) Then
            arguments.Add("--preset")
            arguments.Add(EncoderPreset)
        End If

        If Not String.IsNullOrWhiteSpace(PixelFormat) Then
            arguments.Add("--pix-format")
            arguments.Add(PixelFormat)
        End If

        If Not String.IsNullOrWhiteSpace(_keyint) Then
            arguments.Add("--keyint")
            arguments.Add(_keyint)
        End If

        If Not String.IsNullOrWhiteSpace(_sceneChangeDetection) Then
            arguments.Add("--scd")
            arguments.Add(_sceneChangeDetection)
        End If

        Dim customFilter = GetString("自定义参数_视频滤镜")
        If Not String.IsNullOrWhiteSpace(customFilter) Then
            arguments.Add("--vfilter")
            arguments.Add(customFilter)
        End If

        For Each value In _svtArguments
            arguments.Add("--svt")
            arguments.Add(value)
        Next

        For Each value In _extraEncoderArguments
            arguments.Add("--enc")
            arguments.Add(value)
        Next

        arguments.Add("--min-vmaf")
        arguments.Add(SearchSettings.FormatNumber(settings.TargetVmaf))
        arguments.Add("--min-crf")
        arguments.Add(SearchSettings.FormatNumber(settings.MinCrf))
        arguments.Add("--max-crf")
        arguments.Add(SearchSettings.FormatNumber(settings.MaxCrf))

        ' libsvtav1 的默认增量就是 1；显式指定可确保结果能通过 FFmpegFreeUI 的常规 -crf 选项应用。
        arguments.Add("--crf-increment")
        arguments.Add("1")

        If settings.Samples.HasValue Then
            arguments.Add("--samples")
            arguments.Add(settings.Samples.Value.ToString(CultureInfo.InvariantCulture))
        End If

        arguments.Add("--sample-duration")
        arguments.Add(settings.SampleDuration.Trim())

        If settings.Thorough Then arguments.Add("--thorough")

        Dim vmafModelArgument = BuildVmafModelArgument(settings.VmafModel)
        If vmafModelArgument <> "" Then
            arguments.Add("--vmaf")
            arguments.Add(vmafModelArgument)
        End If

        If jsonOutput Then
            arguments.Add("--stdout-format")
            arguments.Add("json")
        End If
        Return arguments
    End Function

    ''' <summary>
    ''' 将模型名称或 JSON 路径转换为 ab-av1 的 --vmaf 参数。
    ''' ab-av1 会把该值直接拼入 libvmaf 滤镜，因此 Windows 路径必须按 FFmpeg 滤镜语法转义。
    ''' </summary>
    Public Shared Function BuildVmafModelArgument(model As String) As String
        Dim value = If(model, String.Empty).Trim()
        If value = "" Then Return String.Empty

        Dim modelOption As String
        If value.StartsWith("model=", StringComparison.OrdinalIgnoreCase) Then
            modelOption = value.Substring("model=".Length)
        ElseIf value.StartsWith("path=", StringComparison.OrdinalIgnoreCase) OrElse
               value.StartsWith("version=", StringComparison.OrdinalIgnoreCase) Then
            modelOption = value
        ElseIf value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) Then
            modelOption = "path=" & Path.GetFullPath(value)
        Else
            modelOption = "version=" & value
        End If

        Return "model=" & EscapeFilterValue(modelOption)
    End Function

    Private Shared Function EscapeFilterValue(value As String) As String
        Return If(value, String.Empty).
            Replace("\", "\\").
            Replace(":", "\:").
            Replace("'", "\'")
    End Function

    Public Function CreateTemporaryPreset(crf As Double) As String
        Dim presetJson = ApplyCrf(crf)
        Dim temporaryDirectory = Path.Combine(Path.GetTempPath(), "ffmpegfreeui-ab-av1")
        Directory.CreateDirectory(temporaryDirectory)
        Dim temporaryPresetPath = Path.Combine(temporaryDirectory, $"preset-{Guid.NewGuid():N}.json")
        File.WriteAllText(temporaryPresetPath, presetJson)
        Return temporaryPresetPath
    End Function

    Public Function ApplyCrf(crf As Double) As String
        Dim clone = DirectCast(_root.DeepClone(), JsonObject)
        clone("视频参数_比特率_控制方式") = 1
        clone("视频参数_质量控制_参数名") = "crf"

        Dim crfText = SearchSettings.FormatNumber(crf)
        clone("视频参数_质量控制_值") = crfText

        ' 手写的 svtav1-params 若含有 crf=，会覆盖 FFmpeg 的 -crf，因此必须同步两处数值。
        Dim advanced = GetStringFrom(clone, "视频参数_质量控制_进阶参数集")
        If Not String.IsNullOrWhiteSpace(advanced) Then
            Dim pattern = "(^|[\s:])crf=[^:\s""']+"
            advanced = Regex.Replace(
                advanced,
                pattern,
                Function(match) match.Groups(1).Value & "crf=" & crfText,
                RegexOptions.IgnoreCase)
            clone("视频参数_质量控制_进阶参数集") = advanced
        End If

        Return clone.ToJsonString(JsonWriteOptions)
    End Function

    Private Sub ValidateCompatibility()
        Dim issues As New List(Of String)()

        If GetInteger("预设文件版本", 0) <> 6 Then issues.Add("仅支持 FFmpegFreeUI v6 预设")
        If Not String.Equals(Encoder, "libsvtav1", StringComparison.OrdinalIgnoreCase) Then
            issues.Add($"当前版本仅支持 libsvtav1，预设使用的是 {If(Encoder, "（空）")}")
        End If

        Dim supportedPixelFormats = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "", "yuv420p", "yuv420p10le", "yuv422p10le", "yuv444p10le"
        }
        If Not supportedPixelFormats.Contains(PixelFormat) Then issues.Add($"ab-av1 不支持像素格式 {PixelFormat}")

        AddIssueWhenNotEmpty(issues, "视频参数_分辨率", "分辨率调整")
        AddIssueWhenNotEmpty(issues, "视频参数_分辨率自动计算_宽度", "自动计算分辨率")
        AddIssueWhenNotEmpty(issues, "视频参数_分辨率自动计算_高度", "自动计算分辨率")
        AddIssueWhenNotEmpty(issues, "视频参数_分辨率_裁剪滤镜参数", "裁剪滤镜")
        AddIssueWhenNotEmpty(issues, "视频参数_帧速率", "帧率转换")
        AddIssueWhenNotEmpty(issues, "视频参数_插帧_目标帧率", "插帧")
        AddIssueWhenNotEmpty(issues, "视频参数_动态模糊_连续混合帧数", "动态模糊")
        AddIssueWhenNotZero(issues, "视频参数_降噪_方式", "内置降噪滤镜")
        AddIssueWhenNotZero(issues, "视频参数_锐化_方式", "内置锐化滤镜")
        AddIssueWhenNotZero(issues, "视频参数_胶片颗粒_方式", "FFmpegFreeUI 胶片颗粒滤镜")
        AddIssueWhenNotZero(issues, "视频参数_平滑断层_方式", "平滑断层滤镜")
        AddIssueWhenNotZero(issues, "视频参数_处理扫描方式", "扫描方式处理")
        AddIssueWhenNotZero(issues, "视频参数_画面翻转_角度翻转", "画面角度翻转")
        AddIssueWhenNotZero(issues, "视频参数_画面翻转_镜像翻转", "画面镜像翻转")
        AddIssueWhenNotZero(issues, "视频参数_烧录字幕_滤镜选择", "字幕烧录")
        AddIssueWhenNotEmpty(issues, "视频参数_色彩管理_滤镜选择", "色彩管理滤镜")
        AddIssueWhenTrue(issues, "视频参数_色彩管理_启用调整亮度", "亮度调整")
        AddIssueWhenTrue(issues, "视频参数_色彩管理_启用调整对比度", "对比度调整")
        AddIssueWhenTrue(issues, "视频参数_色彩管理_启用调整饱和度", "饱和度调整")
        AddIssueWhenTrue(issues, "视频参数_色彩管理_启用调整伽马", "伽马调整")
        AddIssueWhenTrue(issues, "视频参数_视频帧服务器_使用AviSynth", "AviSynth")
        AddIssueWhenTrue(issues, "视频参数_视频帧服务器_使用VapourSynth", "VapourSynth")
        AddIssueWhenNotEmpty(issues, "自定义参数_完全自己写", "完全自定义命令行")
        AddIssueWhenNotEmpty(issues, "自定义参数_开头参数", "自定义开头参数")
        AddIssueWhenNotEmpty(issues, "自定义参数_之前参数", "自定义输入前参数")
        AddIssueWhenNotEmpty(issues, "自定义参数_之后参数", "自定义输出后参数")
        AddIssueWhenNotEmpty(issues, "自定义参数_最后参数", "自定义末尾参数")
        AddIssueWhenNotZero(issues, "剪辑区间_方法", "剪辑区间")

        If HasItems("滤镜排序系统") Then issues.Add("FFmpegFreeUI 内置滤镜排序")
        If HasItems("视频参数_超分_滤镜叠加策略组") Then issues.Add("超分滤镜")
        If HasItems("流控制_将视频参数应用于指定流") Then issues.Add("非默认视频流选择")

        Dim decoderFields = {
            "解码参数_解码器",
            "解码参数_CPU解码线程数",
            "解码参数_解码数据格式",
            "解码参数_指定硬件的参数名",
            "解码参数_指定硬件的参数"
        }
        If decoderFields.Any(Function(name) Not String.IsNullOrWhiteSpace(GetString(name))) Then
            issues.Add("自定义解码参数")
        End If

        If issues.Count > 0 Then
            Throw New PresetCompatibilityException(
                "以下视频处理暂时无法等价映射到 ab-av1，继续搜索会使 CRF 失真：" &
                Environment.NewLine & "• " & String.Join(Environment.NewLine & "• ", issues.Distinct()))
        End If
    End Sub

    Private Sub ParseAdditionalArguments(raw As String, sourceName As String)
        If String.IsNullOrWhiteSpace(raw) Then Return

        Dim tokens = CommandLineTokenizer.Tokenize(raw)
        Dim index = 0
        While index < tokens.Count
            Dim optionToken = tokens(index)
            If Not LooksLikeOption(optionToken) Then
                Throw New PresetCompatibilityException($"{sourceName} 中存在无法识别的参数：{optionToken}")
            End If

            Dim optionName = optionToken.TrimStart("-"c)
            Dim value As String = Nothing
            If index + 1 < tokens.Count AndAlso Not LooksLikeOption(tokens(index + 1)) Then
                value = tokens(index + 1)
                index += 1
            End If

            If optionName.StartsWith("svtav1-params", StringComparison.OrdinalIgnoreCase) Then
                If String.IsNullOrWhiteSpace(value) Then
                    Throw New PresetCompatibilityException($"{sourceName} 中的 -svtav1-params 缺少值。")
                End If
                ParseSvtParameters(value)
            Else
                AddEncoderArgument(optionName, value, sourceName)
            End If

            index += 1
        End While
    End Sub

    Private Sub ParseSvtParameters(value As String)
        For Each parameter In value.Split(":"c, StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries)
            Dim separator = parameter.IndexOf("="c)
            If separator <= 0 OrElse separator = parameter.Length - 1 Then
                Throw New PresetCompatibilityException($"无法解析 SVT-AV1 高级参数：{parameter}")
            End If

            Dim name = parameter.Substring(0, separator).Trim()
            Dim parameterValue = parameter.Substring(separator + 1).Trim()
            Select Case name.ToLowerInvariant()
                Case "crf"
                    ' 搜索期间由 ab-av1 接管 CRF。
                Case "preset"
                    _effectivePreset = parameterValue
                Case "keyint"
                    _keyint = parameterValue
                Case "scd"
                    _sceneChangeDetection = ToBooleanText(parameterValue, "scd")
                Case "input-depth"
                    ValidateInputDepth(parameterValue)
                Case Else
                    _svtArguments.Add(name & "=" & parameterValue)
            End Select
        Next
    End Sub

    Private Sub AddEncoderArgument(optionName As String, value As String, sourceName As String)
        Dim normalized = optionName.ToLowerInvariant()
        Dim reserved = {"crf", "crf:v", "crf:v:0", "preset", "preset:v", "preset:v:0", "pix_fmt", "pix_fmt:v", "pix_fmt:v:0", "c:v", "c:v:0", "codec:v", "vcodec"}
        If reserved.Contains(normalized, StringComparer.OrdinalIgnoreCase) Then
            Throw New PresetCompatibilityException($"{sourceName} 中的 -{optionName} 应使用 FFmpegFreeUI 的独立字段设置。")
        End If

        Dim finalOnlyPrefixes = {"map", "c:a", "codec:a", "b:a", "c:s", "c:t", "map_metadata", "map_chapters", "metadata", "attach"}
        If finalOnlyPrefixes.Any(Function(prefix) normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) OrElse normalized.StartsWith(prefix & ":", StringComparison.OrdinalIgnoreCase)) Then
            Throw New PresetCompatibilityException($"{sourceName} 中混入了非视频采样参数 -{optionName}；请放回 FFmpegFreeUI 对应面板。")
        End If

        _extraEncoderArguments.Add(If(value Is Nothing, optionName, optionName & "=" & value))
    End Sub

    Private Sub ValidateInputDepth(value As String)
        Dim expected = If(PixelFormat.Contains("10", StringComparison.Ordinal), "10", "8")
        If value <> expected Then
            Throw New PresetCompatibilityException($"input-depth={value} 与像素格式 {PixelFormat} 不一致。")
        End If
    End Sub

    Private Shared Function ToBooleanText(value As String, name As String) As String
        Select Case value.Trim().ToLowerInvariant()
            Case "1", "true", "yes", "on"
                Return "true"
            Case "0", "false", "no", "off"
                Return "false"
            Case Else
                Throw New PresetCompatibilityException($"{name}={value} 不是有效的布尔值。")
        End Select
    End Function

    Private Shared Function LooksLikeOption(value As String) As Boolean
        If String.IsNullOrEmpty(value) OrElse value(0) <> "-"c Then Return False
        If value.Length = 1 Then Return True
        Return Not Char.IsDigit(value(1)) AndAlso value(1) <> "."c
    End Function

    Private Sub AddIssueWhenNotEmpty(issues As List(Of String), field As String, description As String)
        If Not String.IsNullOrWhiteSpace(GetString(field)) Then issues.Add(description)
    End Sub

    Private Sub AddIssueWhenNotZero(issues As List(Of String), field As String, description As String)
        If GetInteger(field, 0) <> 0 Then issues.Add(description)
    End Sub

    Private Sub AddIssueWhenTrue(issues As List(Of String), field As String, description As String)
        If GetBoolean(field) Then issues.Add(description)
    End Sub

    Private Function HasItems(field As String) As Boolean
        Dim array = TryCast(_root(field), JsonArray)
        Return array IsNot Nothing AndAlso array.Count > 0
    End Function

    Private Function GetString(name As String) As String
        Return GetStringFrom(_root, name)
    End Function

    Private Shared Function GetStringFrom(root As JsonObject, name As String) As String
        Dim node = root(name)
        If node Is Nothing Then Return String.Empty

        Dim value = TryCast(node, JsonValue)
        If value Is Nothing Then Return String.Empty

        Dim result As String = Nothing
        If value.TryGetValue(result) Then Return If(result, String.Empty)
        Return node.ToJsonString().Trim(""""c)
    End Function

    Private Function GetInteger(name As String, defaultValue As Integer) As Integer
        Dim value = TryCast(_root(name), JsonValue)
        If value Is Nothing Then Return defaultValue

        Dim result As Integer
        If value.TryGetValue(result) Then Return result
        Return defaultValue
    End Function

    Private Function GetBoolean(name As String) As Boolean
        Dim value = TryCast(_root(name), JsonValue)
        If value Is Nothing Then Return False

        Dim result As Boolean
        Return value.TryGetValue(result) AndAlso result
    End Function

End Class
