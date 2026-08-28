Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks

''' <summary>使用 FFmpeg 的默认可执行文件搜索规则探测当前 libvmaf 内置模型。</summary>
Public NotInheritable Class VmafModelScanner

    Private Shared ReadOnly ScanGate As New SemaphoreSlim(1, 1)
    Private Shared _cachedResult As VmafModelScanResult

    Private Sub New()
    End Sub

    Public Shared Async Function ScanAsync(cancellationToken As CancellationToken,
                                           Optional forceRefresh As Boolean = False) As Task(Of VmafModelScanResult)
        Await ScanGate.WaitAsync(cancellationToken).ConfigureAwait(False)
        Try
            If Not forceRefresh AndAlso _cachedResult IsNot Nothing Then Return _cachedResult
            _cachedResult = Await ScanCoreAsync(cancellationToken).ConfigureAwait(False)
            Return _cachedResult
        Finally
            ScanGate.Release()
        End Try
    End Function

    Private Shared Async Function ScanCoreAsync(cancellationToken As CancellationToken) As Task(Of VmafModelScanResult)
        Try
            Dim query = Await RunFfmpegHelpAsync(cancellationToken).ConfigureAwait(False)
            If query.ExitCode <> 0 Then
                Return New VmafModelScanResult(
                    Array.Empty(Of String)(),
                    query.ExecutablePath,
                    "当前 ffmpeg 不支持 libvmaf，或模型查询失败。")
            End If

            Dim models = ParseModelsFromFilterHelp(query.Output)
            Dim runtimeModels = Await Task.Run(
                Function() ExtractModelsFromRuntime(query.ExecutablePath, cancellationToken),
                cancellationToken).ConfigureAwait(False)
            For Each model In runtimeModels
                If Not models.Contains(model, StringComparer.OrdinalIgnoreCase) Then models.Add(model)
            Next
            models.Sort(StringComparer.OrdinalIgnoreCase)

            Return New VmafModelScanResult(
                models,
                query.ExecutablePath,
                If(models.Count = 0, "未从当前 ffmpeg 中发现可命名的 VMAF 模型。", String.Empty))
        Catch ex As OperationCanceledException
            Throw
        Catch ex As Exception
            Return New VmafModelScanResult(Array.Empty(Of String)(), String.Empty, ex.Message)
        End Try
    End Function

    Public Shared Function ParseModelsFromFilterHelp(output As String) As List(Of String)
        Dim result As New List(Of String)()
        Dim optionMatch = Regex.Match(
            If(output, String.Empty),
            "^\s*model\s+<string>.*?\(default\s+""(?<value>[^""]+)""\)",
            RegexOptions.IgnoreCase Or RegexOptions.Multiline Or RegexOptions.CultureInvariant)
        If Not optionMatch.Success Then Return result

        For Each versionMatch As Match In Regex.Matches(
            optionMatch.Groups("value").Value,
            "(?:^|\|)version=(?<value>[^:|]+)",
            RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
            Dim model = versionMatch.Groups("value").Value.Trim()
            If model <> "" AndAlso Not result.Contains(model, StringComparer.OrdinalIgnoreCase) Then result.Add(model)
        Next
        Return result
    End Function

    Public Shared Function ExtractModelsFromRuntime(
        ffmpegPath As String,
        Optional cancellationToken As CancellationToken = Nothing) As List(Of String)

        Dim result As New List(Of String)()
        If String.IsNullOrWhiteSpace(ffmpegPath) OrElse Not File.Exists(ffmpegPath) Then Return result

        Dim files As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {ffmpegPath}
        Try
            Dim directory = Path.GetDirectoryName(ffmpegPath)
            If String.IsNullOrWhiteSpace(directory) Then Return result
            For Each pattern In {"avfilter*.dll", "libavfilter*.dll", "libvmaf*.dll"}
                For Each file In System.IO.Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                    files.Add(file)
                Next
            Next
        Catch
        End Try

        For Each file In files
            Try
                cancellationToken.ThrowIfCancellationRequested()
                Dim info As New FileInfo(file)
                If info.Length <= 0 OrElse info.Length > 256L * 1024L * 1024L Then Continue For
                ExtractModelsFromFile(file, result, cancellationToken)
            Catch ex As OperationCanceledException
                Throw
            Catch
            End Try
        Next

        result.Sort(StringComparer.OrdinalIgnoreCase)
        Return result
    End Function

    Private Shared Sub ExtractModelsFromFile(
        file As String,
        result As List(Of String),
        cancellationToken As CancellationToken)

        Const BufferSize As Integer = 1024 * 1024
        Const CarryLength As Integer = 256
        Dim buffer(BufferSize - 1) As Byte
        Dim carry = String.Empty

        Using stream = New FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite Or FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan)

            Do
                cancellationToken.ThrowIfCancellationRequested()
                Dim read = stream.Read(buffer, 0, buffer.Length)
                If read <= 0 Then Exit Do

                Dim content = carry & Encoding.ASCII.GetString(buffer, 0, read)
                ExtractModelsFromAsciiChunk(content, result)
                carry = If(
                    content.Length <= CarryLength,
                    content,
                    content.Substring(content.Length - CarryLength))
            Loop
        End Using
    End Sub

    Private Shared Sub ExtractModelsFromAsciiChunk(content As String, result As List(Of String))
        Dim searchFrom = 0
        Do
            Dim start = content.IndexOf("vmaf_", searchFrom, StringComparison.OrdinalIgnoreCase)
            If start < 0 Then Exit Do
            searchFrom = start + 5

            If start > 0 AndAlso IsModelNameCharacter(content(start - 1)) Then Continue Do

            Dim finish = start
            Dim limit = Math.Min(content.Length, start + 192)
            While finish < limit AndAlso IsModelNameCharacter(content(finish), allowDot:=True)
                finish += 1
            End While

            Dim candidate = content.Substring(start, finish - start)
            Dim modelMatch = Regex.Match(
                candidate,
                "^(?<value>vmaf_[A-Za-z0-9_]*v\d+\.\d+\.\d+[A-Za-z0-9]*(?:_[A-Za-z0-9]+)*)",
                RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100))
            If Not modelMatch.Success Then Continue Do

            Dim model = modelMatch.Groups("value").Value
            If Not result.Contains(model, StringComparer.OrdinalIgnoreCase) Then result.Add(model)
        Loop
    End Sub

    Private Shared Function IsModelNameCharacter(value As Char, Optional allowDot As Boolean = False) As Boolean
        Return (value >= "a"c AndAlso value <= "z"c) OrElse
               (value >= "A"c AndAlso value <= "Z"c) OrElse
               (value >= "0"c AndAlso value <= "9"c) OrElse
               value = "_"c OrElse
               (allowDot AndAlso value = "."c)
    End Function

    Private Shared Async Function RunFfmpegHelpAsync(cancellationToken As CancellationToken) As Task(Of ProcessQueryResult)
        Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(10)),
              linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token),
              process As New Process()

            Dim executablePath = ResolveFfmpegPath()

            process.StartInfo = New ProcessStartInfo With {
                .FileName = "ffmpeg",
                .WorkingDirectory = PluginEnvironment.PluginDirectory,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8
            }
            process.StartInfo.ArgumentList.Add("-hide_banner")
            process.StartInfo.ArgumentList.Add("-h")
            process.StartInfo.ArgumentList.Add("filter=libvmaf")

            If Not process.Start() Then Throw New InvalidOperationException("无法启动 ffmpeg。")

            Try
                If process.MainModule IsNot Nothing Then executablePath = process.MainModule.FileName
            Catch
            End Try

            Dim stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token)
            Dim stderrTask = process.StandardError.ReadToEndAsync(linked.Token)
            Try
                Await process.WaitForExitAsync(linked.Token).ConfigureAwait(False)
                Dim stdout = Await stdoutTask.ConfigureAwait(False)
                Dim stderr = Await stderrTask.ConfigureAwait(False)
                Return New ProcessQueryResult(process.ExitCode, stdout & Environment.NewLine & stderr, executablePath)
            Catch ex As OperationCanceledException When timeout.IsCancellationRequested AndAlso Not cancellationToken.IsCancellationRequested
                If Not process.HasExited Then process.Kill(entireProcessTree:=True)
                Throw New TimeoutException("查询 ffmpeg VMAF 模型超时。", ex)
            Catch
                If Not process.HasExited Then process.Kill(entireProcessTree:=True)
                Throw
            Finally
                If Not process.HasExited Then process.Kill(entireProcessTree:=True)
            End Try
        End Using
    End Function

    Private Shared Function ResolveFfmpegPath() As String
        Dim directories As New List(Of String) From {
            PluginEnvironment.PluginDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        }

        Dim pathValue = Environment.GetEnvironmentVariable("PATH")
        If Not String.IsNullOrWhiteSpace(pathValue) Then
            For Each directory In pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                directories.Add(directory.Trim().Trim(""""c))
            Next
        End If

        For Each directory In directories.Distinct(StringComparer.OrdinalIgnoreCase)
            Try
                If String.IsNullOrWhiteSpace(directory) Then Continue For
                For Each fileName In {"ffmpeg.exe", "ffmpeg"}
                    Dim candidate = Path.GetFullPath(Path.Combine(directory, fileName))
                    If File.Exists(candidate) Then Return candidate
                Next
            Catch
            End Try
        Next
        Return String.Empty
    End Function

    Private NotInheritable Class ProcessQueryResult
        Public Sub New(exitCode As Integer, output As String, executablePath As String)
            Me.ExitCode = exitCode
            Me.Output = output
            Me.ExecutablePath = executablePath
        End Sub

        Public ReadOnly Property ExitCode As Integer
        Public ReadOnly Property Output As String
        Public ReadOnly Property ExecutablePath As String
    End Class

End Class

Public NotInheritable Class VmafModelScanResult
    Public Sub New(models As IEnumerable(Of String), ffmpegPath As String, errorMessage As String)
        Me.Models = models.ToArray()
        Me.FfmpegPath = If(ffmpegPath, String.Empty)
        Me.ErrorMessage = If(errorMessage, String.Empty)
    End Sub

    Public ReadOnly Property Models As IReadOnlyList(Of String)
    Public ReadOnly Property FfmpegPath As String
    Public ReadOnly Property ErrorMessage As String
End Class
