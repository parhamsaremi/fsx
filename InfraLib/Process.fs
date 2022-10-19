namespace FSX.Infrastructure

open System
open System.IO
open System.Diagnostics
open System.Threading
open System.Linq
open System.Text

module Process =

    // https://stackoverflow.com/a/961904/544947
    type internal QueuedLock() =
        let innerLock = Object()
        let ticketsCount = ref 0
        let ticketToRide = ref 1

        member __.Enter() =
            let myTicket = Interlocked.Increment ticketsCount
            Monitor.Enter innerLock

            while myTicket <> Volatile.Read ticketToRide do
                Monitor.Wait innerLock |> ignore

        member __.Exit() =
            Interlocked.Increment ticketToRide |> ignore
            Monitor.PulseAll innerLock
            Monitor.Exit innerLock

    type Standard =
        | Output
        | Error

        override self.ToString() =
            sprintf "%A" self

    type OutputChunk =
        {
            OutputType: Standard
            Chunk: StringBuilder
        }

    type Echo =
        | All
        | OutputOnly
        | Off

    type OutputBuffer(buffer: list<OutputChunk>) =

        //NOTE both Filter() and Print() process tail before head
        // because of the way the buffer-aggregation is implemented in
        // Execute()'s ReadIteration()

        let rec Filter
            (
                subBuffer: list<OutputChunk>,
                outputType: Option<Standard>
            ) =
            match subBuffer with
            | [] -> new StringBuilder()
            | head :: tail ->
                let filteredTail = Filter(tail, outputType)

                if (outputType.IsNone || head.OutputType = outputType.Value) then
                    filteredTail.Append(head.Chunk.ToString())
                else
                    filteredTail

        let rec Print(subBuffer: list<OutputChunk>) : unit =
            match subBuffer with
            | [] -> ()
            | head :: tail ->
                Print(tail)

                match head.OutputType with
                | Standard.Output ->
                    Console.Write(head.Chunk.ToString())
                    Console.Out.Flush()
                | Standard.Error ->
                    Console.Error.Write(head.Chunk.ToString())
                    Console.Error.Flush()

        member this.StdOut = Filter(buffer, Some(Standard.Output)).ToString()

        member this.StdErr = Filter(buffer, Some(Standard.Error)).ToString()

        member this.PrintToConsole() =
            Print(buffer)

        override self.ToString() =
            Filter(buffer, None).ToString()

    type ProcessResult =
        {
            ExitCode: int
            Output: OutputBuffer
        }

    type ProcessDetails =
        {
            Command: string
            Arguments: string
        }

        override self.ToString() =
            sprintf "Command: %s. Arguments: %s." self.Command self.Arguments

    type ProcessCouldNotStart
        (
            procDetails: ProcessDetails,
            innerException: Exception
        ) =
        inherit Exception
            (
                sprintf "Process could not start! %s" (procDetails.ToString()),
                innerException
            )

    type private ReaderState =
        | Continue // new character in the same line
        | Pause // e.g. an EOL has arrived -> other thread can take the lock
        | End // no more data

    let Execute(procDetails: ProcessDetails, echo: Echo) : ProcessResult =
        printfn "here 1"
        // I know, this shit below is mutable, but it's a consequence of dealing with .NET's Process class' events?
        let mutable outputBuffer: list<OutputChunk> = []
        printfn "here 2"
        let queuedLock = QueuedLock()
        printfn "here 3"

        if (echo = Echo.All) then
            Console.WriteLine(
                sprintf "%s %s" procDetails.Command procDetails.Arguments
            )

            Console.Out.Flush()
        printfn "here 4"
        let startInfo =
            new ProcessStartInfo(procDetails.Command, procDetails.Arguments)
        printfn "here 5"
        startInfo.UseShellExecute <- false
        printfn "here 6"
        startInfo.RedirectStandardOutput <- true
        printfn "here 7"
        startInfo.RedirectStandardError <- true
        printfn "here 8"
        use proc = new System.Diagnostics.Process()
        printfn "here 9"
        proc.StartInfo <- startInfo
        printfn "here 10"
        let ReadStandard(std: Standard) =

            let print =
                match std with
                | Standard.Output -> Console.Write: array<char> -> unit
                | Standard.Error -> Console.Error.Write

            let flush =
                match std with
                | Standard.Output -> Console.Out.Flush
                | Standard.Error -> Console.Error.Flush

            let outputToReadFrom =
                match std with
                | Standard.Output -> proc.StandardOutput
                | Standard.Error -> proc.StandardError

            let EndOfStream(readCount: int) : bool =
                if (readCount > 0) then
                    false
                else if (readCount < 0) then
                    true
                else //if (readCount = 0)
                    outputToReadFrom.EndOfStream

            let rec ReadIterationInner() : bool =
                // I want to hardcode this to 1 because otherwise the order of the stderr|stdout
                // chunks in the outputbuffer would innecessarily depend on this bufferSize, setting
                // it to 1 makes it slow but then the order is only relying (in theory) on how the
                // streams come and how fast the .NET IO processes them
                let outChar = [| 'x' |] // 'x' is a dummy value that will get replaced
                let bufferSize = 1
                let uniqueElementIndexInTheSingleCharBuffer = bufferSize - 1

                if not(outChar.Length = bufferSize) then
                    failwith "Buffer Size must equal current buffer size"

                let readTask =
                    outputToReadFrom.ReadAsync(
                        outChar,
                        uniqueElementIndexInTheSingleCharBuffer,
                        bufferSize
                    )

                printfn "just checking"
                readTask.Wait()

                if not(readTask.IsCompleted) then
                    failwith "Failed to read"

                let readCount = readTask.Result

                if (readCount > bufferSize) then
                    failwith
                        "StreamReader.Read() should not read more than the bufferSize if we passed the bufferSize as a parameter"

                if (readCount = bufferSize) then
                    if not(echo = Echo.Off) then
                        print outChar
                        flush()

                    let append() =
                        let leChar =
                            outChar.[uniqueElementIndexInTheSingleCharBuffer]

                        let newBuilder = StringBuilder(leChar.ToString())

                        match outputBuffer with
                        | [] ->
                            let newBlock =
                                match std with
                                | Standard.Output ->
                                    {
                                        OutputType = Standard.Output
                                        Chunk = newBuilder
                                    }
                                | Standard.Error ->
                                    {
                                        OutputType = Standard.Error
                                        Chunk = newBuilder
                                    }

                            outputBuffer <- [ newBlock ]
                        | head :: tail ->
                            if head.OutputType = std then
                                head.Chunk.Append outChar |> ignore
                            else
                                let newBuilder =
                                    {
                                        OutputType = std
                                        Chunk = newBuilder
                                    }

                                outputBuffer <- newBuilder :: outputBuffer

                    append()

                if EndOfStream readCount then
                    false
                elif outChar.Single() = '\n' then
                    true
                else
                    ReadIterationInner()

            let ReadIteration() : bool =
                try
                    printfn "lock entered"
                    queuedLock.Enter()
                    ReadIterationInner()
                finally
                    queuedLock.Exit()
                    printfn "lock exitted"
            // this is a way to do a `do...while` loop in F#...
            while (ReadIteration()) do
                ignore None
        printfn "here 11"
        let outReaderThread =
            new Thread(new ThreadStart(fun _ -> ReadStandard(Standard.Output)))
        printfn "here 12"
        let errReaderThread =
            new Thread(new ThreadStart(fun _ -> ReadStandard(Standard.Error)))
        printfn "here 13"
        try
            printfn "here 14"
            proc.Start() |> ignore
            printfn "here 15"
        with
        | e -> raise <| ProcessCouldNotStart(procDetails, e)
        printfn "here 16"
        outReaderThread.Start()
        printfn "here 17"
        errReaderThread.Start()
        printfn "here 18"
        proc.WaitForExit()
        printfn "here 19"
        let exitCode = proc.ExitCode
        printfn "here 20"

        outReaderThread.Join()
        printfn "here 21"
        errReaderThread.Join()
        printfn "here 22"

        {
            ExitCode = exitCode
            Output = OutputBuffer(outputBuffer)
        }


    exception ProcessFailed of string

    let SafeExecute(procDetails: ProcessDetails, echo: Echo) : ProcessResult =
        let procResult = Execute(procDetails, echo)

        if not(procResult.ExitCode = 0) then
            if (echo = Echo.Off) then
                Console.WriteLine(procResult.Output.StdOut)
                Console.Error.WriteLine(procResult.Output.StdErr)
                Console.Error.WriteLine()

            raise
            <| ProcessFailed(
                String.Format(
                    "Command '{0}' failed with exit code {1}. Arguments supplied: '{2}'",
                    procDetails.Command,
                    procResult.ExitCode.ToString(),
                    procDetails.Arguments
                )
            )

        procResult

    let rec private ExceptionIsOfTypeOrIncludesAnyInnerExceptionOfType
        (
            ex: Exception,
            t: Type
        ) : bool =
        if (ex = null) then
            false
        else if (ex.GetType() = t) then
            true
        else
            ExceptionIsOfTypeOrIncludesAnyInnerExceptionOfType(
                ex.InnerException,
                t
            )

    let rec private CheckIfCommandWorksInShellWithWhich
        (command: string)
        : bool =
        let WhichCommandWorksInShell() : bool =
            let maybeResult =
                try
                    Some(
                        Execute(
                            {
                                Command = "which"
                                Arguments = String.Empty
                            },
                            Echo.Off
                        )
                    )
                with
                | ex when
                    (ExceptionIsOfTypeOrIncludesAnyInnerExceptionOfType(
                        ex,
                        typeof<System.ComponentModel.Win32Exception>
                    ))
                    ->
                    None
                | _ -> reraise()

            match maybeResult with
            | None -> false
            | Some _ -> true

        if not(WhichCommandWorksInShell()) then
            failwith "'which' doesn't work, please install it first"

        let procResult =
            Execute(
                {
                    Command = "which"
                    Arguments = command
                },
                Echo.Off
            )

        match procResult.ExitCode with
        | 0 -> true
        | _ -> false

    let private HasWindowsExecutableExtension(path: string) =
        //FIXME: should do it in a case-insensitive way
        path.EndsWith(".exe")
        || path.EndsWith(".bat")
        || path.EndsWith(".cmd")
        || path.EndsWith(".com")

    let private IsFileInWindowsPath(command: string) =
        let pathEnvVar = Environment.GetEnvironmentVariable("PATH")
        let paths = pathEnvVar.Split(Path.PathSeparator)
        paths.Any(fun path -> File.Exists(Path.Combine(path, command)))

    let CommandWorksInShell(command: string) : bool =
        if (Misc.GuessPlatform() = Misc.Platform.Windows) then
            let exists = File.Exists(command) || IsFileInWindowsPath(command)

            if (exists && HasWindowsExecutableExtension(command)) then
                true
            else
                false
        else
            CheckIfCommandWorksInShellWithWhich(command)
