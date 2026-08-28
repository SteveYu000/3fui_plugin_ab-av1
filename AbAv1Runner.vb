Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks

Public NotInheritable Class AbAv1Runner

    Private Shared ReadOnly JsonSupportGate As New SemaphoreSlim(1, 1)
    Private Shared ReadOnly AnsiEscapeRegex As New Regex(
        "\x1B\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)
    Private Shared ReadOnly HumanResultRegex As New Regex(
        "\bcrf\s+(?<crf>-?\d+(?:\.\d+)?)\s+(?<metric>VMAF|XPSNR)\s+(?<score>-?\d+(?:\.\d+)?)\s+predicted\s+(?:video\s+stream|image)\s+size\s+(?<size>\d+(?:\.\d+)?)\s+(?<unit>[KMGTPE]?i?B)\b",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant Or RegexOptions.IgnoreCase)
    Private Shared ReadOnly HumanAttemptRegex As New Regex(
        "\bcrf\s+(?<crf>-?\d+(?:\.\d+)?)\s+(?<metric>VMAF|XPSNR)\s+(?<score>-?\d+(?:\.\d+)?)\b",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant Or RegexOptions.IgnoreCase)
    Private Shared ReadOnly HumanSampleResultRegex As New Regex(
        "\b(?<metric>VMAF|XPSNR)\s+(?<score>-?\d+(?:\.\d+)?)\s+predicted\s+(?:video\s+stream|image)\s+size\s+(?<size>\d+(?:\.\d+)?)\s+(?<unit>[KMGTPE]?i?B)\b",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant Or RegexOptions.IgnoreCase)

    Private Shared _jsonSupportExecutable As String = String.Empty
    Private Shared _jsonSupportTimestamp As DateTime
    Private Shared _jsonOutputSupported As Boolean?

    Private ReadOnly _processLock As New Object()
    Private _currentProcess As Process
    Private _currentCommandLine As String = String.Empty
    Private _suspendedProcessIds As IReadOnlyList(Of Integer) = Array.Empty(Of Integer)()

    Public ReadOnly Property HasActiveProcess As Boolean
        Get
            SyncLock _processLock
                Return _currentProcess IsNot Nothing AndAlso Not HasExitedSafely(_currentProcess)
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property IsPaused As Boolean
        Get
            SyncLock _processLock
                Return _suspendedProcessIds.Count > 0
            End SyncLock
        End Get
    End Property

    ''' <summary>最近一次搜索实际使用的完整命令行。</summary>
    Public ReadOnly Property CurrentCommandLine As String
        Get
            SyncLock _processLock
                Return _currentCommandLine
            End SyncLock
        End Get
    End Property

    Public Function TryPause(ByRef errorMessage As String) As Boolean
        SyncLock _processLock
            If _currentProcess Is Nothing OrElse HasExitedSafely(_currentProcess) Then
                errorMessage = "ab-av1 进程尚未启动或已经结束。"
                Return False
            End If
            If _suspendedProcessIds.Count > 0 Then
                errorMessage = String.Empty
                Return True
            End If

            Dim suspended As IReadOnlyList(Of Integer) = Nothing
            If Not ProcessTreeController.TrySuspend(_currentProcess.Id, suspended, errorMessage) Then Return False
            _suspendedProcessIds = suspended
            Return True
        End SyncLock
    End Function

    Public Function TryResume(ByRef errorMessage As String) As Boolean
        SyncLock _processLock
            If _suspendedProcessIds.Count = 0 Then
                errorMessage = "任务当前没有暂停。"
                Return False
            End If

            Dim resumed = ProcessTreeController.TryResume(_suspendedProcessIds, errorMessage)
            If resumed Then _suspendedProcessIds = Array.Empty(Of Integer)()
            Return resumed
        End SyncLock
    End Function

    Public Shared Async Function BuildCommandLineAsync(profile As PresetProfile,
                                                        inputPath As String,
                                                        settings As SearchSettings,
                                                        cancellationToken As CancellationToken) As Task(Of String)
        Dim executable = GetExecutablePath()
        Dim useJsonOutput = Await SupportsJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
        Dim arguments = profile.BuildSearchArguments(inputPath, settings, useJsonOutput)
        Return FormatCommandLine(executable, arguments)
    End Function

    Public Shared Async Function BuildCommandLineTemplateAsync(profile As PresetProfile,
                                                                settings As SearchSettings,
                                                                cancellationToken As CancellationToken) As Task(Of String)
        Dim executable = GetExecutablePath()
        Dim useJsonOutput = Await SupportsJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
        Dim arguments = profile.BuildSearchArgumentTemplate(settings, useJsonOutput)
        Return FormatCommandLine(executable, arguments)
    End Function

    Public Shared Async Function BuildSampleCommandLineAsync(profile As PresetProfile,
                                                              inputPath As String,
                                                              settings As SampleEncodeSettings,
                                                              cancellationToken As CancellationToken) As Task(Of String)
        Dim executable = GetExecutablePath()
        Dim useJsonOutput = Await SupportsJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
        Dim arguments = profile.BuildSampleEncodeArguments(inputPath, settings, useJsonOutput)
        Return FormatCommandLine(executable, arguments)
    End Function

    Public Shared Async Function BuildSampleCommandLineTemplateAsync(profile As PresetProfile,
                                                                      settings As SampleEncodeSettings,
                                                                      cancellationToken As CancellationToken) As Task(Of String)
        Dim executable = GetExecutablePath()
        Dim useJsonOutput = Await SupportsJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
        Dim arguments = profile.BuildSampleEncodeArgumentTemplate(settings, useJsonOutput)
        Return FormatCommandLine(executable, arguments)
    End Function

    Public Async Function SearchAsync(profile As PresetProfile,
                                      inputPath As String,
                                      settings As SearchSettings,
                                      progress As IProgress(Of SearchProgress),
                                      cancellationToken As CancellationToken) As Task(Of SearchResult)
        Dim executable = GetExecutablePath()

        Dim useJsonOutput = Await SupportsJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
        Dim arguments = profile.BuildSearchArguments(inputPath, settings, useJsonOutput)
        SetCurrentCommandLine(FormatCommandLine(executable, arguments))

        Dim startInfo As New ProcessStartInfo With {
            .FileName = executable,
            .WorkingDirectory = PluginEnvironment.PluginDirectory,
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }

        For Each argument In arguments
            startInfo.ArgumentList.Add(argument)
        Next

        Using process As New Process With {.StartInfo = startInfo}
            If Not process.Start() Then Throw New InvalidOperationException("无法启动 ab-av1.exe。")
            RegisterProcess(process)

            Dim stderrLines As New ConcurrentQueue(Of String)()
            Dim stdoutTask As Task(Of SearchResult)
            If useJsonOutput Then
                stdoutTask = ReadJsonStdoutAsync(process.StandardOutput, settings.Metric, progress, cancellationToken)
            Else
                stdoutTask = ReadHumanStdoutAsync(process.StandardOutput, progress, cancellationToken)
            End If
            Dim stderrTask = ReadStderrAsync(process.StandardError, stderrLines, progress, cancellationToken)

            Try
                Await process.WaitForExitAsync(cancellationToken).ConfigureAwait(False)
                Dim result = Await stdoutTask.ConfigureAwait(False)
                Await stderrTask.ConfigureAwait(False)

                If process.ExitCode <> 0 Then
                    Throw New InvalidOperationException(BuildFailureMessage(process.ExitCode, stderrLines))
                End If

                If result Is Nothing Then
                    Throw New InvalidDataException(
                        "ab-av1 已结束，但没有输出可识别的 CRF 搜索结果。")
                End If

                Return result
            Catch ex As OperationCanceledException
                TerminateProcess(process)
                Throw
            Catch
                TerminateProcess(process)
                Throw
            Finally
                TerminateProcess(process)
                UnregisterProcess(process)
            End Try
        End Using
    End Function

    Public Async Function SampleEncodeAsync(profile As PresetProfile,
                                             inputPath As String,
                                             settings As SampleEncodeSettings,
                                             progress As IProgress(Of SearchProgress),
                                             cancellationToken As CancellationToken) As Task(Of SampleEncodeResult)
        Dim executable = GetExecutablePath()
        Dim useJsonOutput = Await SupportsJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
        Dim arguments = profile.BuildSampleEncodeArguments(inputPath, settings, useJsonOutput)
        SetCurrentCommandLine(FormatCommandLine(executable, arguments))

        Dim startInfo As New ProcessStartInfo With {
            .FileName = executable,
            .WorkingDirectory = PluginEnvironment.PluginDirectory,
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        For Each argument In arguments
            startInfo.ArgumentList.Add(argument)
        Next

        Using process As New Process With {.StartInfo = startInfo}
            If Not process.Start() Then Throw New InvalidOperationException("无法启动 ab-av1.exe。")
            RegisterProcess(process)

            Dim stderrLines As New ConcurrentQueue(Of String)()
            Dim stdoutTask As Task(Of SampleEncodeResult)
            If useJsonOutput Then
                stdoutTask = ReadJsonSampleStdoutAsync(
                    process.StandardOutput,
                    settings,
                    progress,
                    cancellationToken)
            Else
                stdoutTask = ReadHumanSampleStdoutAsync(
                    process.StandardOutput,
                    settings,
                    progress,
                    cancellationToken)
            End If
            Dim stderrTask = ReadStderrAsync(process.StandardError, stderrLines, progress, cancellationToken)

            Try
                Await process.WaitForExitAsync(cancellationToken).ConfigureAwait(False)
                Dim result = Await stdoutTask.ConfigureAwait(False)
                Await stderrTask.ConfigureAwait(False)

                If process.ExitCode <> 0 Then
                    Throw New InvalidOperationException(BuildFailureMessage(process.ExitCode, stderrLines))
                End If
                If result Is Nothing Then
                    Throw New InvalidDataException("ab-av1 已结束，但没有输出可识别的样本编码结果。")
                End If
                Return result
            Catch ex As OperationCanceledException
                TerminateProcess(process)
                Throw
            Catch
                TerminateProcess(process)
                Throw
            Finally
                TerminateProcess(process)
                UnregisterProcess(process)
            End Try
        End Using
    End Function

    Private Shared Function GetExecutablePath() As String
        Dim executable = PluginEnvironment.AbAv1Path
        If Not File.Exists(executable) Then
            Throw New FileNotFoundException(
                $"请将 ab-av1.exe 放到插件目录：{PluginEnvironment.PluginDirectory}",
                executable)
        End If
        Return executable
    End Function

    Private Sub SetCurrentCommandLine(value As String)
        SyncLock _processLock
            _currentCommandLine = value
        End SyncLock
    End Sub

    Private Shared Function FormatCommandLine(executable As String,
                                               arguments As IEnumerable(Of String)) As String
        Dim commandLine As New StringBuilder(QuoteCommandLineArgument(executable))
        For Each argument In arguments
            commandLine.Append(" "c)
            commandLine.Append(QuoteCommandLineArgument(argument))
        Next
        Return commandLine.ToString()
    End Function

    Private Shared Function QuoteCommandLineArgument(value As String) As String
        Dim argument = If(value, String.Empty)
        Dim requiresQuotes = argument.Length = 0 OrElse
                             argument.Any(
                                 Function(character)
                                     Return Char.IsWhiteSpace(character) OrElse
                                            character = """"c OrElse
                                            "&|<>^()".Contains(character)
                                 End Function)
        If Not requiresQuotes Then Return argument

        '遵循 CommandLineToArgvW 的反斜杠/双引号规则，同时给模板中的 <输入文件> 加引号。
        Dim quoted As New StringBuilder()
        quoted.Append(""""c)
        Dim backslashCount = 0
        For Each character In argument
            If character = "\"c Then
                backslashCount += 1
            ElseIf character = """"c Then
                quoted.Append("\"c, backslashCount * 2 + 1)
                quoted.Append(character)
                backslashCount = 0
            Else
                quoted.Append("\"c, backslashCount)
                quoted.Append(character)
                backslashCount = 0
            End If
        Next
        quoted.Append("\"c, backslashCount * 2)
        quoted.Append(""""c)
        Return quoted.ToString()
    End Function

    Private Sub RegisterProcess(process As Process)
        SyncLock _processLock
            _currentProcess = process
            _suspendedProcessIds = Array.Empty(Of Integer)()
        End SyncLock
    End Sub

    Private Sub UnregisterProcess(process As Process)
        SyncLock _processLock
            If Object.ReferenceEquals(_currentProcess, process) Then
                _currentProcess = Nothing
                _suspendedProcessIds = Array.Empty(Of Integer)()
            End If
        End SyncLock
    End Sub

    Private Sub TerminateProcess(process As Process)
        SyncLock _processLock
            If Object.ReferenceEquals(_currentProcess, process) AndAlso _suspendedProcessIds.Count > 0 Then
                Dim ignored As String = Nothing
                ProcessTreeController.TryResume(_suspendedProcessIds, ignored)
                _suspendedProcessIds = Array.Empty(Of Integer)()
            End If
        End SyncLock

        Try
            If Not process.HasExited Then process.Kill(entireProcessTree:=True)
        Catch
        End Try
    End Sub

    Private Shared Function HasExitedSafely(process As Process) As Boolean
        Try
            Return process.HasExited
        Catch
            Return True
        End Try
    End Function

    Private Shared Async Function SupportsJsonOutputAsync(executable As String,
                                                           cancellationToken As CancellationToken) As Task(Of Boolean)
        Dim timestamp = File.GetLastWriteTimeUtc(executable)
        Await JsonSupportGate.WaitAsync(cancellationToken).ConfigureAwait(False)
        Try
            If _jsonOutputSupported.HasValue AndAlso
               String.Equals(_jsonSupportExecutable, executable, StringComparison.OrdinalIgnoreCase) AndAlso
               _jsonSupportTimestamp = timestamp Then
                Return _jsonOutputSupported.Value
            End If

            Dim supported = Await ProbeJsonOutputAsync(executable, cancellationToken).ConfigureAwait(False)
            _jsonSupportExecutable = executable
            _jsonSupportTimestamp = timestamp
            _jsonOutputSupported = supported
            Return supported
        Finally
            JsonSupportGate.Release()
        End Try
    End Function

    Private Shared Async Function ProbeJsonOutputAsync(executable As String,
                                                        cancellationToken As CancellationToken) As Task(Of Boolean)
        Dim startInfo As New ProcessStartInfo With {
            .FileName = executable,
            .WorkingDirectory = PluginEnvironment.PluginDirectory,
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        startInfo.ArgumentList.Add("crf-search")
        startInfo.ArgumentList.Add("--help")

        Using process As New Process With {.StartInfo = startInfo}
            If Not process.Start() Then Throw New InvalidOperationException("无法检查 ab-av1.exe 版本能力。")
            Dim stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken)
            Dim stderrTask = process.StandardError.ReadToEndAsync(cancellationToken)

            Try
                Await process.WaitForExitAsync(cancellationToken).ConfigureAwait(False)
                Dim stdout = Await stdoutTask.ConfigureAwait(False)
                Dim stderr = Await stderrTask.ConfigureAwait(False)
                Dim helpText = stdout & Environment.NewLine & stderr
                Return process.ExitCode = 0 AndAlso
                       helpText.Contains("--stdout-format", StringComparison.Ordinal)
            Catch ex As OperationCanceledException
                If Not process.HasExited Then process.Kill(entireProcessTree:=True)
                Throw
            Finally
                If Not process.HasExited Then process.Kill(entireProcessTree:=True)
            End Try
        End Using
    End Function

    Private Shared Async Function ReadJsonStdoutAsync(reader As StreamReader,
                                                       expectedMetric As QualityMetric,
                                                       progress As IProgress(Of SearchProgress),
                                                       cancellationToken As CancellationToken) As Task(Of SearchResult)
        Dim finalResult As SearchResult = Nothing

        While True
            Dim line = Await reader.ReadLineAsync(cancellationToken).ConfigureAwait(False)
            If line Is Nothing Then Exit While
            If String.IsNullOrWhiteSpace(line) Then Continue While

            Try
                Using document = JsonDocument.Parse(line)
                    Dim root = document.RootElement
                    Dim type = GetString(root, "type")

                    Select Case type
                        Case "sample-encode-done"
                            Dim crf = GetDouble(root, "crf")
                            Dim actualMetric = expectedMetric
                            Dim score = GetMetricScore(root, expectedMetric, actualMetric)
                            Dim message = $"测试 CRF {FormatNumber(crf)}"
                            If score.HasValue Then
                                message &= $" · {GetMetricDisplayName(actualMetric)} {score.Value:0.###}"
                            End If
                            progress?.Report(New SearchProgress(message, crf, score, actualMetric))

                        Case "crf-search-done"
                            Dim actualMetric = expectedMetric
                            Dim score = GetMetricScore(root, expectedMetric, actualMetric)
                            finalResult = New SearchResult With {
                                .Crf = GetDouble(root, "crf"),
                                .Metric = actualMetric,
                                .Score = score.GetValueOrDefault(),
                                .PredictedEncodeSize = GetOptionalInt64(root, "predicted_encode_size").GetValueOrDefault(),
                                .PredictedEncodeSeconds = GetOptionalDouble(root, "predicted_encode_seconds").GetValueOrDefault(),
                                .PredictedEncodePercent = GetOptionalDouble(root, "predicted_encode_percent").GetValueOrDefault()
                            }

                        Case "crf-search-error"
                            Dim message = GetString(root, "message")
                            If String.IsNullOrWhiteSpace(message) Then message = "ab-av1 未找到满足条件的 CRF。"
                            Throw New InvalidOperationException(message)
                    End Select
                End Using
            Catch ex As JsonException
                Throw New InvalidDataException($"无法解析 ab-av1 JSON 输出：{line}", ex)
            End Try
        End While

        Return finalResult
    End Function

    Private Shared Async Function ReadJsonSampleStdoutAsync(reader As StreamReader,
                                                             settings As SampleEncodeSettings,
                                                             progress As IProgress(Of SearchProgress),
                                                             cancellationToken As CancellationToken) As Task(Of SampleEncodeResult)
        Dim finalResult As SampleEncodeResult = Nothing

        While True
            Dim line = Await reader.ReadLineAsync(cancellationToken).ConfigureAwait(False)
            If line Is Nothing Then Exit While
            If String.IsNullOrWhiteSpace(line) Then Continue While

            Try
                Using document = JsonDocument.Parse(line)
                    Dim root = document.RootElement
                    Dim type = GetString(root, "type")
                    ' ab-av1 <= 0.11.4 的直接 sample-encode JSON 没有 type/crf/from_cache 字段。
                    If type = "sample-encode-done" OrElse
                       (type = "" AndAlso GetOptionalInt64(root, "predicted_encode_size").HasValue) Then
                        Dim actualMetric = settings.Metric
                        Dim score = GetMetricScore(root, settings.Metric, actualMetric)
                        finalResult = New SampleEncodeResult With {
                            .Crf = GetOptionalDouble(root, "crf").GetValueOrDefault(settings.Crf),
                            .Metric = actualMetric,
                            .Score = score.GetValueOrDefault(),
                            .PredictedEncodeSize = GetOptionalInt64(root, "predicted_encode_size").GetValueOrDefault(),
                            .PredictedEncodeSeconds = GetOptionalDouble(root, "predicted_encode_seconds").GetValueOrDefault(),
                            .PredictedEncodePercent = GetOptionalDouble(root, "predicted_encode_percent").GetValueOrDefault()
                        }
                        Dim message = $"CRF {FormatNumber(finalResult.Crf)} 样本完成"
                        If score.HasValue Then
                            message &= $" · {GetMetricDisplayName(actualMetric)} {score.Value:0.###}"
                        End If
                        progress?.Report(New SearchProgress(message, finalResult.Crf, score, actualMetric))
                    End If
                End Using
            Catch ex As JsonException
                Throw New InvalidDataException($"无法解析 ab-av1 JSON 输出：{line}", ex)
            End Try
        End While
        Return finalResult
    End Function

    Private Shared Async Function ReadHumanStdoutAsync(reader As StreamReader,
                                                        progress As IProgress(Of SearchProgress),
                                                        cancellationToken As CancellationToken) As Task(Of SearchResult)
        Dim finalResult As SearchResult = Nothing

        While True
            Dim line = Await reader.ReadLineAsync(cancellationToken).ConfigureAwait(False)
            If line Is Nothing Then Exit While
            Dim clean = StripTerminalFormatting(line).Trim()
            If clean.Length = 0 Then Continue While

            Dim parsed = TryParseHumanResult(clean)
            If parsed IsNot Nothing Then
                finalResult = parsed
                progress?.Report(New SearchProgress(
                    $"找到 CRF {FormatNumber(parsed.Crf)} · {GetMetricDisplayName(parsed.Metric)} {parsed.Score:0.###}",
                    parsed.Crf,
                    parsed.Score,
                    parsed.Metric))
            Else
                progress?.Report(New SearchProgress(clean))
            End If
        End While

        Return finalResult
    End Function

    Private Shared Async Function ReadHumanSampleStdoutAsync(reader As StreamReader,
                                                              settings As SampleEncodeSettings,
                                                              progress As IProgress(Of SearchProgress),
                                                              cancellationToken As CancellationToken) As Task(Of SampleEncodeResult)
        Dim finalResult As SampleEncodeResult = Nothing
        While True
            Dim line = Await reader.ReadLineAsync(cancellationToken).ConfigureAwait(False)
            If line Is Nothing Then Exit While
            Dim clean = StripTerminalFormatting(line).Trim()
            If clean.Length = 0 Then Continue While

            Dim parsed = TryParseHumanSampleResult(clean, settings.Crf)
            If parsed IsNot Nothing Then
                finalResult = parsed
                progress?.Report(New SearchProgress(
                    $"CRF {FormatNumber(parsed.Crf)} 样本完成 · {GetMetricDisplayName(parsed.Metric)} {parsed.Score:0.###}",
                    parsed.Crf,
                    parsed.Score,
                    parsed.Metric))
            Else
                progress?.Report(New SearchProgress(clean))
            End If
        End While
        Return finalResult
    End Function

    Private Shared Async Function ReadStderrAsync(reader As StreamReader,
                                                   stderrLines As ConcurrentQueue(Of String),
                                                   progress As IProgress(Of SearchProgress),
                                                   cancellationToken As CancellationToken) As Task
        While True
            Dim line = Await reader.ReadLineAsync(cancellationToken).ConfigureAwait(False)
            If line Is Nothing Then Exit While
            If String.IsNullOrWhiteSpace(line) Then Continue While

            stderrLines.Enqueue(line.Trim())
            While stderrLines.Count > 40
                Dim discarded As String = Nothing
                stderrLines.TryDequeue(discarded)
            End While

            Dim clean = StripTerminalFormatting(line.Replace(ControlChars.Cr, " ")).Trim()
            If clean.Length > 0 Then
                Dim attempt = TryParseHumanAttempt(clean)
                progress?.Report(If(attempt, New SearchProgress(clean)))
            End If
        End While
    End Function

    Private Shared Function TryParseHumanResult(line As String) As SearchResult
        Dim match = HumanResultRegex.Match(line)
        If Not match.Success Then Return Nothing

        Dim crf As Double
        Dim score As Double
        Dim size As Double
        If Not TryParseInvariant(match.Groups("crf").Value, crf) OrElse
           Not TryParseInvariant(match.Groups("score").Value, score) OrElse
           Not TryParseInvariant(match.Groups("size").Value, size) Then
            Return Nothing
        End If

        Return New SearchResult With {
            .Crf = crf,
            .Metric = ParseMetric(match.Groups("metric").Value),
            .Score = score,
            .PredictedEncodeSize = ConvertHumanBytes(size, match.Groups("unit").Value)
        }
    End Function

    Private Shared Function TryParseHumanSampleResult(line As String, crf As Double) As SampleEncodeResult
        Dim match = HumanSampleResultRegex.Match(line)
        If Not match.Success Then Return Nothing

        Dim score As Double
        Dim size As Double
        If Not TryParseInvariant(match.Groups("score").Value, score) OrElse
           Not TryParseInvariant(match.Groups("size").Value, size) Then
            Return Nothing
        End If
        Return New SampleEncodeResult With {
            .Crf = crf,
            .Metric = ParseMetric(match.Groups("metric").Value),
            .Score = score,
            .PredictedEncodeSize = ConvertHumanBytes(size, match.Groups("unit").Value)
        }
    End Function

    Private Shared Function TryParseHumanAttempt(line As String) As SearchProgress
        Dim match = HumanAttemptRegex.Match(line)
        If Not match.Success Then Return Nothing

        Dim crf As Double
        Dim score As Double
        If Not TryParseInvariant(match.Groups("crf").Value, crf) OrElse
           Not TryParseInvariant(match.Groups("score").Value, score) Then
            Return Nothing
        End If

        Dim metric = ParseMetric(match.Groups("metric").Value)
        Return New SearchProgress(
            $"测试 CRF {FormatNumber(crf)} · {GetMetricDisplayName(metric)} {score:0.###}",
            crf,
            score,
            metric)
    End Function

    Private Shared Function ConvertHumanBytes(value As Double, unit As String) As Long
        Dim factor As Double
        Select Case unit.ToUpperInvariant()
            Case "B"
                factor = 1
            Case "KB"
                factor = 1000
            Case "MB"
                factor = 1000 ^ 2
            Case "GB"
                factor = 1000 ^ 3
            Case "TB"
                factor = 1000 ^ 4
            Case "KIB"
                factor = 1024
            Case "MIB"
                factor = 1024 ^ 2
            Case "GIB"
                factor = 1024 ^ 3
            Case "TIB"
                factor = 1024 ^ 4
            Case Else
                Return 0
        End Select

        Dim bytes = value * factor
        If Double.IsNaN(bytes) OrElse bytes <= 0 OrElse bytes > Long.MaxValue Then Return 0
        Return CLng(Math.Round(bytes, MidpointRounding.AwayFromZero))
    End Function

    Private Shared Function TryParseInvariant(value As String, ByRef result As Double) As Boolean
        Return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, result)
    End Function

    Private Shared Function StripTerminalFormatting(value As String) As String
        Return AnsiEscapeRegex.Replace(value, String.Empty)
    End Function

    Private Shared Function BuildFailureMessage(exitCode As Integer, stderrLines As ConcurrentQueue(Of String)) As String
        Dim detail = String.Join(Environment.NewLine, stderrLines.TakeLast(12))
        If String.IsNullOrWhiteSpace(detail) Then detail = "没有错误输出。"
        Return $"ab-av1 退出，代码 {exitCode}：{Environment.NewLine}{detail}"
    End Function

    Private Shared Function GetString(root As JsonElement, name As String) As String
        Dim value As JsonElement
        If root.TryGetProperty(name, value) AndAlso value.ValueKind = JsonValueKind.String Then
            Return value.GetString()
        End If
        Return String.Empty
    End Function

    Private Shared Function GetMetricScore(root As JsonElement,
                                           preferredMetric As QualityMetric,
                                           ByRef actualMetric As QualityMetric) As Double?
        Dim preferred = GetOptionalDouble(root, GetMetricJsonPropertyName(preferredMetric))
        If preferred.HasValue Then
            actualMetric = preferredMetric
            Return preferred
        End If

        Dim fallbackMetric = If(preferredMetric = QualityMetric.Vmaf, QualityMetric.Xpsnr, QualityMetric.Vmaf)
        Dim fallback = GetOptionalDouble(root, GetMetricJsonPropertyName(fallbackMetric))
        If fallback.HasValue Then actualMetric = fallbackMetric
        Return fallback
    End Function

    Private Shared Function ParseMetric(value As String) As QualityMetric
        Return If(String.Equals(value, "XPSNR", StringComparison.OrdinalIgnoreCase),
                  QualityMetric.Xpsnr,
                  QualityMetric.Vmaf)
    End Function

    Private Shared Function GetDouble(root As JsonElement, name As String) As Double
        Return GetOptionalDouble(root, name).
            GetValueOrDefault(Double.NaN)
    End Function

    Private Shared Function GetOptionalDouble(root As JsonElement, name As String) As Double?
        Dim value As JsonElement
        If root.TryGetProperty(name, value) AndAlso value.ValueKind = JsonValueKind.Number Then
            Dim result As Double
            If value.TryGetDouble(result) Then Return result
        End If
        Return Nothing
    End Function

    Private Shared Function GetOptionalInt64(root As JsonElement, name As String) As Long?
        Dim value As JsonElement
        If root.TryGetProperty(name, value) AndAlso value.ValueKind = JsonValueKind.Number Then
            Dim result As Long
            If value.TryGetInt64(result) Then Return result
        End If
        Return Nothing
    End Function

    Private Shared Function FormatNumber(value As Double) As String
        Return value.ToString("0.###", CultureInfo.InvariantCulture)
    End Function

End Class
