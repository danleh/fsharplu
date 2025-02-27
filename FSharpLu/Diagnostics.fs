﻿/// System diagnotics, process and logging helpers
module Microsoft.FSharpLu.Diagnostics

open System
open System.Security.Principal

/// Determine if the process runs elevated
let isElevated () =
    use user = WindowsIdentity.GetCurrent()
    let principal = WindowsPrincipal(user)
    principal.IsInRole(WindowsBuiltInRole.Administrator)

/// ensure that the process runs with elevated permissions
let ensureAdmin () =
    if not (isElevated ()) then
        invalidOp "Elevated privileges required."

/// Ask for user confirmation before completing a task
let confirm message =
    printf "%s  Press CTRL+C to abort, ENTER to continue." message
    System.Console.ReadLine() |> ignore

/// Process execution helpers
module Process =
    open System.Diagnostics
    open Microsoft.FSharpLu.Logging

    /// Process information returned by startProcessAsync
    type ProcessResult =
        {
            ProcessExited : bool
            ExitCode : int
            StandardOutput : string
            ExecutionTime : TimeSpan
        }

    /// Process start flag parameters for startProcessAsync
    [<FlagsAttribute>]
    type ProcessStartFlags =
        | None = 0x0
        | Elevated = 0x1
        | Minimized = 0x2
        | RedirectStandardOutput = 0x4

    /// Process timeout option
    type ProcessTimeoutAction =
        | AttemptToKillProcessAfterTimeout of TimeSpan
        | KeepTheProcessRunningAfterTimeout of TimeSpan
        | NoTimeout

    /// Starts a process and returns an asynchronous workflow that waits
    /// for it to terminate
    let startProcessAsync command arguments workingDir (flags:ProcessStartFlags) (timeout:ProcessTimeoutAction) =
        async {
            let useShellExecute =
                if flags.HasFlag ProcessStartFlags.Minimized then
                    Trace.warning "Important: Shell execute may pop-up the IE security zone window which synchronously blocks the call to Start()! To run minimized shell execute has to be used"
                    true
                else
                    false

            use instance =
                new Process(
                    StartInfo =
                        ProcessStartInfo
                            (
                                FileName = command,
                                WorkingDirectory = workingDir,
                                Arguments = arguments,
                                CreateNoWindow = false,
                                UseShellExecute = useShellExecute,
                                RedirectStandardOutput = flags.HasFlag ProcessStartFlags.RedirectStandardOutput,
                                WindowStyle = (
                                    if flags.HasFlag ProcessStartFlags.Minimized then
                                        ProcessWindowStyle.Minimized
                                    else
                                        ProcessWindowStyle.Normal
                                    ),

                                Verb = (
                                    if flags.HasFlag ProcessStartFlags.Elevated then
                                         "runas"
                                    else
                                        String.Empty)
                            ),
                    EnableRaisingEvents = true)

            Trace.info "Launching '%s %s'" command arguments
            let timer = System.Diagnostics.Stopwatch()
            
            timer.Start()
            instance.Exited.Add
                (fun a -> timer.Stop()
                          Trace.info "Process execution terminated in %O with error code 0x%X: '%O %O'" timer.Elapsed (int32 instance.ExitCode) command arguments)
            
            let waitEvent = Async.AwaitEvent(instance.Exited)
            let! waitAsync =
               match timeout with
                | NoTimeout ->
                    Async.StartChild(waitEvent)
                | AttemptToKillProcessAfterTimeout t
                | KeepTheProcessRunningAfterTimeout t ->
                    Async.StartChild(waitEvent, int t.TotalMilliseconds)

            if not (instance.Start()) then
                let message = sprintf "Could not start command: '%s' with parameters '%s'" command arguments
                return raise <| System.InvalidOperationException(message)
            else
                try
                    let! _ = waitAsync
                    Trace.info "%s %s exited with code: %d" command arguments instance.ExitCode
                    return
                        {
                            ProcessResult.ProcessExited = true
                            ExitCode = instance.ExitCode
                            ExecutionTime = timer.Elapsed
                            StandardOutput =
                                if flags.HasFlag ProcessStartFlags.RedirectStandardOutput then
                                    instance.StandardOutput.ReadToEnd()
                                else
                                    String.Empty
                        }
                with
                | :? System.TimeoutException ->
                    match timeout with
                    | NoTimeout -> ()
                    | AttemptToKillProcessAfterTimeout t ->
                        Trace.info "%s %s did not exit within allocated time out of %f seconds. Killing process." command arguments t.TotalSeconds
                        try instance.Kill() with _ -> ()
                    | KeepTheProcessRunningAfterTimeout t ->
                        Trace.info "%s %s did not exit within allocated time out of %f seconds." command arguments t.TotalSeconds
                    return
                        {
                            ProcessResult.ProcessExited = false
                            ExitCode = -1
                            ExecutionTime = timer.Elapsed
                            StandardOutput = String.Empty
                        }
        }

    // Start a process and returns an asynchronous workflow that waits
    // for it to terminate and return the process exit code
    let startProcessAsyncAndWait command arguments workingDir flags =
        async {
            let! processResult = startProcessAsync command arguments workingDir flags NoTimeout
            return processResult.ExitCode
        }