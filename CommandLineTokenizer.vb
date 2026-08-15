Imports System.Collections.Generic
Imports System.Text

''' <summary>
''' 拆分 FFmpegFreeUI 保存的 FFmpeg 参数片段。按照 Windows 命令行的引号和反斜杠规则处理，
''' 在不调用 shell 的情况下保留带引号的路径和滤镜表达式。
''' </summary>
Public NotInheritable Class CommandLineTokenizer

    Private Sub New()
    End Sub

    Public Shared Function Tokenize(commandLine As String) As List(Of String)
        Dim result As New List(Of String)()
        If String.IsNullOrWhiteSpace(commandLine) Then Return result

        Dim index = 0
        While index < commandLine.Length
            While index < commandLine.Length AndAlso Char.IsWhiteSpace(commandLine(index))
                index += 1
            End While
            If index >= commandLine.Length Then Exit While

            Dim token As New StringBuilder()
            Dim inQuotes = False

            While index < commandLine.Length
                Dim current = commandLine(index)
                If Char.IsWhiteSpace(current) AndAlso Not inQuotes Then Exit While

                If current = "\"c Then
                    Dim slashCount = 0
                    While index < commandLine.Length AndAlso commandLine(index) = "\"c
                        slashCount += 1
                        index += 1
                    End While

                    If index < commandLine.Length AndAlso commandLine(index) = """"c Then
                        token.Append("\"c, slashCount \ 2)
                        If slashCount Mod 2 = 0 Then
                            inQuotes = Not inQuotes
                        Else
                            token.Append(""""c)
                        End If
                        index += 1
                    Else
                        token.Append("\"c, slashCount)
                    End If
                    Continue While
                End If

                If current = """"c Then
                    inQuotes = Not inQuotes
                    index += 1
                    Continue While
                End If

                token.Append(current)
                index += 1
            End While

            If inQuotes Then Throw New FormatException("参数中存在未闭合的双引号。")
            result.Add(token.ToString())
        End While

        Return result
    End Function

End Class
