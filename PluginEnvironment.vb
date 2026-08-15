Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Windows.Forms

Public NotInheritable Class PluginEnvironment

    Private Sub New()
    End Sub

    Public Shared ReadOnly Property PluginDirectory As String = ResolvePluginDirectory()

    Public Shared ReadOnly Property AbAv1Path As String
        Get
            Return Path.Combine(PluginDirectory, "ab-av1.exe")
        End Get
    End Property

    Public Shared Function FindDefaultPreset() As String
        Dim userPresetDirectory = Path.Combine(Application.StartupPath, "Preset_v6", "User")
        If Not Directory.Exists(userPresetDirectory) Then Return String.Empty

        Dim preferred = Path.Combine(userPresetDirectory, "AV1-压缩.json")
        If File.Exists(preferred) Then Return preferred

        Return Directory.EnumerateFiles(userPresetDirectory, "*.json", SearchOption.TopDirectoryOnly).
            OrderBy(Function(path) path, StringComparer.CurrentCultureIgnoreCase).
            FirstOrDefault(String.Empty)
    End Function

    Public Shared Function BuildOutputPath(inputPath As String,
                                           requestedDirectory As String,
                                           outputContainer As String,
                                           crf As Double) As String
        Dim directory = requestedDirectory.Trim()
        If directory.Length = 0 Then directory = Path.GetDirectoryName(inputPath)
        If String.IsNullOrWhiteSpace(directory) Then directory = Application.StartupPath

        Dim extension = outputContainer.Trim()
        If extension.Length = 0 Then extension = ".mkv"
        If Not extension.StartsWith("."c) Then extension = "." & extension

        Dim baseName = Path.GetFileNameWithoutExtension(inputPath)
        Dim crfText = crf.ToString("0.###", CultureInfo.InvariantCulture)
        Dim outputPath = Path.Combine(directory, $"{baseName}.ab-av1.crf{crfText}{extension}")

        If String.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase) Then
            outputPath = Path.Combine(directory, $"{baseName}.ab-av1.{Date.Now:yyyyMMddHHmmss}{extension}")
        End If

        Return outputPath
    End Function

    Public Shared Function FormatBytes(value As Long) As String
        If value <= 0 Then Return "—"

        Dim units = {"B", "KiB", "MiB", "GiB", "TiB"}
        Dim amount = CDbl(value)
        Dim unitIndex = 0
        While amount >= 1024 AndAlso unitIndex < units.Length - 1
            amount /= 1024
            unitIndex += 1
        End While

        Return $"{amount:0.##} {units(unitIndex)}"
    End Function

    Private Shared Function ResolvePluginDirectory() As String
        Dim location = GetType(PluginEnvironment).Assembly.Location
        If Not String.IsNullOrWhiteSpace(location) Then
            Dim directory = Path.GetDirectoryName(location)
            If Not String.IsNullOrWhiteSpace(directory) Then Return Path.GetFullPath(directory)
        End If

        Return Path.GetFullPath(AppContext.BaseDirectory)
    End Function

End Class
