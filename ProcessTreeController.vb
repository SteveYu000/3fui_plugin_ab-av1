Imports System.ComponentModel
Imports System.Runtime.InteropServices

''' <summary>暂停或恢复 ab-av1 及其已经启动的 ffmpeg 子进程。</summary>
Friend NotInheritable Class ProcessTreeController

    Private Const Th32csSnapProcess As UInteger = &H2UI
    Private Const ProcessSuspendResume As UInteger = &H800UI
    Private Const ProcessQueryLimitedInformation As UInteger = &H1000UI
    Private Shared ReadOnly InvalidHandleValue As New IntPtr(-1)

    Private Sub New()
    End Sub

    Public Shared Function TrySuspend(rootProcessId As Integer,
                                      ByRef suspendedProcessIds As IReadOnlyList(Of Integer),
                                      ByRef errorMessage As String) As Boolean
        suspendedProcessIds = Array.Empty(Of Integer)()
        errorMessage = String.Empty
        If rootProcessId <= 0 Then
            errorMessage = "ab-av1 进程尚未启动。"
            Return False
        End If

        Dim suspended As New List(Of Integer)()
        Try
            '先冻结父进程，避免枚举期间继续创建新的 ffmpeg 子进程。
            SuspendProcess(rootProcessId)
            suspended.Add(rootProcessId)

            For Each processId In GetDescendantProcessIds(rootProcessId)
                Try
                    SuspendProcess(processId)
                    suspended.Add(processId)
                Catch ex As Win32Exception
                    '子进程可能在枚举后自行退出；父进程已暂停时不因此回滚整个操作。
                End Try
            Next

            suspendedProcessIds = suspended
            Return True
        Catch ex As Exception
            For index = suspended.Count - 1 To 0 Step -1
                Try
                    ResumeProcess(suspended(index))
                Catch
                End Try
            Next
            errorMessage = ex.Message
            Return False
        End Try
    End Function

    Public Shared Function TryResume(suspendedProcessIds As IReadOnlyList(Of Integer),
                                     ByRef errorMessage As String) As Boolean
        errorMessage = String.Empty
        If suspendedProcessIds Is Nothing OrElse suspendedProcessIds.Count = 0 Then
            errorMessage = "没有已暂停的 ab-av1 进程。"
            Return False
        End If

        Dim failures As New List(Of String)()
        '按子进程到父进程的顺序恢复，最后才允许 ab-av1 继续调度。
        For index = suspendedProcessIds.Count - 1 To 0 Step -1
            Try
                ResumeProcess(suspendedProcessIds(index))
            Catch ex As Win32Exception
                failures.Add(ex.Message)
            End Try
        Next

        If failures.Count > 0 Then
            errorMessage = failures(0)
            Return False
        End If
        Return True
    End Function

    Private Shared Sub SuspendProcess(processId As Integer)
        InvokeNtProcessOperation(processId, suspend:=True)
    End Sub

    Private Shared Sub ResumeProcess(processId As Integer)
        InvokeNtProcessOperation(processId, suspend:=False)
    End Sub

    Private Shared Sub InvokeNtProcessOperation(processId As Integer, suspend As Boolean)
        Dim handle = OpenProcess(ProcessSuspendResume Or ProcessQueryLimitedInformation, False, CUInt(processId))
        If handle = IntPtr.Zero Then Throw New Win32Exception(Marshal.GetLastWin32Error())

        Try
            Dim status = If(suspend, NtSuspendProcess(handle), NtResumeProcess(handle))
            If status <> 0 Then Throw New Win32Exception($"进程状态切换失败（NTSTATUS 0x{status:X8}）。")
        Finally
            CloseHandle(handle)
        End Try
    End Sub

    Private Shared Function GetDescendantProcessIds(rootProcessId As Integer) As List(Of Integer)
        Dim parentMap As New Dictionary(Of Integer, List(Of Integer))()
        Dim snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0UI)
        If snapshot = InvalidHandleValue Then Throw New Win32Exception(Marshal.GetLastWin32Error())

        Try
            Dim entry As New ProcessEntry32 With {
                .StructureSize = CUInt(Marshal.SizeOf(Of ProcessEntry32)())
            }
            If Process32First(snapshot, entry) Then
                Do
                    Dim parentId = CInt(entry.ParentProcessId)
                    Dim children As List(Of Integer) = Nothing
                    If Not parentMap.TryGetValue(parentId, children) Then
                        children = New List(Of Integer)()
                        parentMap(parentId) = children
                    End If
                    children.Add(CInt(entry.ProcessId))
                    entry.StructureSize = CUInt(Marshal.SizeOf(Of ProcessEntry32)())
                Loop While Process32Next(snapshot, entry)
            End If
        Finally
            CloseHandle(snapshot)
        End Try

        Dim result As New List(Of Integer)()
        Dim pending As New Queue(Of Integer)()
        pending.Enqueue(rootProcessId)
        While pending.Count > 0
            Dim parentId = pending.Dequeue()
            Dim children As List(Of Integer) = Nothing
            If Not parentMap.TryGetValue(parentId, children) Then Continue While
            For Each childId In children
                If childId = rootProcessId OrElse result.Contains(childId) Then Continue For
                result.Add(childId)
                pending.Enqueue(childId)
            Next
        End While
        Return result
    End Function

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Private Structure ProcessEntry32
        Public StructureSize As UInteger
        Public Usage As UInteger
        Public ProcessId As UInteger
        Public DefaultHeapId As UIntPtr
        Public ModuleId As UInteger
        Public Threads As UInteger
        Public ParentProcessId As UInteger
        Public BasePriority As Integer
        Public Flags As UInteger

        <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=260)>
        Public ExecutableFile As String
    End Structure

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CreateToolhelp32Snapshot(flags As UInteger, processId As UInteger) As IntPtr
    End Function

    <DllImport("kernel32.dll", EntryPoint:="Process32FirstW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function Process32First(snapshot As IntPtr, ByRef entry As ProcessEntry32) As Boolean
    End Function

    <DllImport("kernel32.dll", EntryPoint:="Process32NextW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function Process32Next(snapshot As IntPtr, ByRef entry As ProcessEntry32) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function OpenProcess(desiredAccess As UInteger,
                                       <MarshalAs(UnmanagedType.Bool)> inheritHandle As Boolean,
                                       processId As UInteger) As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(handle As IntPtr) As Boolean
    End Function

    <DllImport("ntdll.dll")>
    Private Shared Function NtSuspendProcess(processHandle As IntPtr) As Integer
    End Function

    <DllImport("ntdll.dll")>
    Private Shared Function NtResumeProcess(processHandle As IntPtr) As Integer
    End Function

End Class
