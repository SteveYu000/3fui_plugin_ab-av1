Imports System.Windows.Forms

Public NotInheritable Class Entry

    Private Shared 添加页面回调 As Action(Of String, Control)

    Private Sub New()
    End Sub

    Public Shared Sub SetHost_AddCustomWinformPanel(action As Object)
        添加页面回调 = DirectCast(action, Action(Of String, Control))
    End Sub

    Public Shared Sub Entry()
        If 添加页面回调 Is Nothing Then
            Throw New InvalidOperationException("宿主没有注入官方页面注册回调。")
        End If

        添加页面回调.Invoke(
            "Official-only smoke",
            New Panel With {
                .Name = "OfficialOnlySmokePanel",
                .Dock = DockStyle.Fill
            })
    End Sub

End Class
