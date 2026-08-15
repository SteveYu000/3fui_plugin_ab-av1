Imports System.IO
Imports System.Reflection
Imports System.Text
Imports System.Windows.Forms

''' <summary>
''' FFmpegFreeUI 官方加载器会按“程序集名称 + .Entry”查找此类型并调用 Entry()。
''' 插件不引用宿主程序集，只通过官方文档列出的反射回调与 3FUI 通信。
''' </summary>
Public NotInheritable Class Entry

    Private Sub New()
    End Sub

    Public Shared Property HostCall_AddCustomWinformPanel As Action(Of String, Control)

    Public Shared Sub SetHost_AddCustomWinformPanel(action As Object)
        HostCall_AddCustomWinformPanel = DirectCast(action, Action(Of String, Control))
    End Sub

    Public Shared Property HostCall_AddMissionToQueueWith3fuiFile As Action(Of String, String, String, String)

    Public Shared Sub SetHost_AddMissionToQueueWith3fuiFile(action As Object)
        HostCall_AddMissionToQueueWith3fuiFile =
            DirectCast(action, Action(Of String, String, String, String))
    End Sub

    Public Shared Sub Entry()
        Try
            HostCall_AddCustomWinformPanel?.Invoke("AB-AV1", New MainPanel())
        Catch ex As Exception
            WriteLoadErrorLog(ex)
            Throw
        End Try
    End Sub

    Public Shared Sub EnqueuePresetTask(presetPath As String,
                                        displayName As String,
                                        outputPath As String,
                                        inputPath As String)
        Dim enqueue = HostCall_AddMissionToQueueWith3fuiFile
        If enqueue Is Nothing Then
            Throw New InvalidOperationException("FFmpegFreeUI 未注入预设任务队列接口。")
        End If

        enqueue.Invoke(presetPath, displayName, outputPath, inputPath)
    End Sub

    Private Shared Sub WriteLoadErrorLog(exception As Exception)
        Try
            Dim report As New StringBuilder()
            report.AppendLine($"Time: {Date.Now:O}")
            report.AppendLine($"Plugin: {GetType(Entry).Assembly.FullName}")
            report.AppendLine($"Plugin path: {GetType(Entry).Assembly.Location}")
            report.AppendLine($"Base directory: {AppContext.BaseDirectory}")
            report.AppendLine()
            report.AppendLine("Exception chain:")

            Dim current As Exception = exception
            Dim depth = 0
            While current IsNot Nothing
                report.AppendLine($"[{depth}] {current.GetType().FullName}: {current.Message}")
                report.AppendLine(current.StackTrace)
                current = current.InnerException
                depth += 1
            End While

            report.AppendLine()
            report.AppendLine("Loaded LakeUI/Vortice assemblies:")
            For Each assembly In AppDomain.CurrentDomain.GetAssemblies().
                Where(Function(value)
                          Dim name = value.GetName().Name
                          Return name.Equals("LakeUI", StringComparison.OrdinalIgnoreCase) OrElse
                                 name.StartsWith("Vortice.", StringComparison.OrdinalIgnoreCase)
                      End Function).
                OrderBy(Function(value) value.GetName().Name, StringComparer.OrdinalIgnoreCase)
                report.AppendLine($"- {assembly.FullName} | {assembly.Location}")
            Next

            File.WriteAllText(
                Path.Combine(PluginEnvironment.PluginDirectory, "FFmpegFreeUI.AbAv1.load-error.log"),
                report.ToString(),
                Encoding.UTF8)
        Catch
            ' 诊断日志写入失败时，不得遮蔽原始插件加载异常。
        End Try
    End Sub

End Class
