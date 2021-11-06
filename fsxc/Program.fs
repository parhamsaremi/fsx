﻿
namespace FSX.Compiler

open System
open System.IO
open System.Linq

open FSX.Infrastructure
open Process

type Flag =
    | Force
    | OnlyCheck
    | Verbose

type ProvidedCommandLineArguments =
    {
        Flags: List<Flag>
        MaybeScript: Option<FileInfo>
    }

type ParsedCommandLineArguments =
    {
        Flags: List<Flag>
        Script: FileInfo
    }

type BinFolder =
    {
        Dir: DirectoryInfo
        Created: bool
    }
type ExeTarget =
    {
        Exe: FileInfo
        BinFolderCreated: bool
    }

type BuildResult =
    | Failure of BinFolder
    | Success of ExeTarget

exception NoScriptProvided

module Program =

    let PrintUsage () =
        Console.WriteLine ()
        Console.WriteLine "Usage: ./fsxc.fsx  [OPTION] yourscript.fsx"
        Console.WriteLine ()
        Console.WriteLine "Options"
        Console.WriteLine "  -f, --force     Always generate binaries again even if existing binaries are new enough"
        Console.WriteLine "  -h, --help      Show this help"
        Console.WriteLine "  -k, --check     Only check if it compiles, removing generated binaries"
        Console.WriteLine "  -v, --verbose   Verbose mode, ideal for debugging purposes"

    let rec ParseArgsInternal (args: seq<string>) (finalArgs: ProvidedCommandLineArguments): ProvidedCommandLineArguments =
        match Seq.tryHead args with
        | None -> finalArgs
        | Some arg ->
            let maybeFlag: Option<Flag> =
                if arg = "-f" || arg = "--force" then
                    Some Force
                else if arg = "-k" || arg = "--check" then
                    Some OnlyCheck
                elif arg = "-v" || arg = "--verbose" then
                    Some Verbose
                elif arg.StartsWith "-" then
                    failwithf "Flag not recognized: %s" arg
                else
                    None

            let newArgs =
                match maybeFlag with
                | None ->
                    if not (arg.EndsWith ".fsx") then
                        failwithf "Argument not recognized: %s. Only commands, or scripts ending with .fsx allowed" arg
                    else if (finalArgs.MaybeScript.IsSome) then
                        failwith "Only one .fsx script allowed"
                    else
                        {
                            Flags = finalArgs.Flags
                            MaybeScript = Some(FileInfo arg)
                        }
                | Some flag ->
                        {
                            Flags = flag::finalArgs.Flags
                            MaybeScript = finalArgs.MaybeScript
                        }

            ParseArgsInternal (Seq.tail args) newArgs

    let ParseArgs(args: seq<string>): ParsedCommandLineArguments =
        let parsedArgs = ParseArgsInternal args { Flags = []; MaybeScript = None }
        match parsedArgs.MaybeScript with
        | None -> raise NoScriptProvided
        | Some scriptFileName ->
            {
                Flags = parsedArgs.Flags
                Script = scriptFileName
            }

    let LOAD_PREPROCESSOR = "#load \""
    let REF_PREPROCESSOR = "#r \""

    type PreProcessorAction =
        | Skip
        | Load of string
        | Ref of string

    type LineAction =
        | Normal
        | PreProcessorAction of PreProcessorAction

    type FsxScript =
        {
            Original: FileInfo
            Backup: FileInfo
        }

    type CompilerInput =
        | SourceFile of FileInfo
        | Script of FsxScript
        | Ref of string

    let GetBinFolderForAScript(script: FileInfo) =
        DirectoryInfo(Path.Combine(script.Directory.FullName, "bin"))

    let GetAutoGenerationTargets (orig: FileInfo) (extension: string) =
        let binDir = GetBinFolderForAScript(orig)
        let autogeneratedFileName =
            if (String.IsNullOrEmpty(extension)) then
                orig.Name
            else
                sprintf "%s.%s" orig.Name extension
        let autogeneratedFile = FileInfo(Path.Combine(binDir.FullName, autogeneratedFileName))
        binDir,autogeneratedFile

    let ReadScriptContents(origScript: FileInfo): List<string*LineAction> =

        let readPreprocessorLine(line: string) =
            if (line.StartsWith("#!")) then
                PreProcessorAction.Skip
            elif (line.StartsWith LOAD_PREPROCESSOR) then
                let fileToLoad = line.Substring(LOAD_PREPROCESSOR.Length, line.Length - LOAD_PREPROCESSOR.Length - 1)
                PreProcessorAction.Load fileToLoad
            elif (line.StartsWith REF_PREPROCESSOR) then
                let libToRef = line.Substring(REF_PREPROCESSOR.Length, line.Length - REF_PREPROCESSOR.Length - 1)
                PreProcessorAction.Ref libToRef
            else
                failwithf "Unrecognized preprocessor line: %s" line

        let readLine (line: string): LineAction =
            if line.StartsWith "#" then
                LineAction.PreProcessorAction(readPreprocessorLine line)
            else
                LineAction.Normal

        let contents = File.ReadAllText origScript.FullName
        let lines = contents.Split([| Environment.NewLine |], StringSplitOptions.None)
        seq {
            for line in lines do
                yield line,(readLine line)
        } |> List.ofSeq

    let GetParsedContentsAndOldestLastWriteTimeFromScriptOrItsDependencies (script: FileInfo)
                                                                               : List<string*LineAction>*DateTime =
        let scriptContents = ReadScriptContents script
        let lastWriteTimes = seq {
            yield script.LastWriteTime

            for _,maybeDep in scriptContents do
                match maybeDep with
                | LineAction.PreProcessorAction preProcessorAction ->
                    match preProcessorAction with
                    | PreProcessorAction.Load file ->
                        let fileInfo = FileInfo <| Path.Combine (script.Directory.FullName, file)
                        if not fileInfo.Exists then
                            failwithf "Dependency %s not found" file
                        else
                            yield fileInfo.LastWriteTime
                    | PreProcessorAction.Ref ref ->
                        let fileInfo = FileInfo <| Path.Combine (script.Directory.FullName, ref)
                        if not fileInfo.Exists then
                            // must be a BCL lib (e.g. #r "System.Xml.Linq.dll")
                            ()
                        else
                            yield fileInfo.LastWriteTime
                    | _ ->
                        ()
                | _ ->
                    ()
        }
        scriptContents,lastWriteTimes.Max()

    let BuildFsxScript(script: FileInfo) (contents: List<string*LineAction>) (verbose: bool): BuildResult =
        if script = null then
            raise <| ArgumentNullException "script"
        if not (script.FullName.EndsWith ".fsx") then
            invalidArg "script" "The script filename needs to end with .fsx extension"

        let binFolderExistedOriginally = GetBinFolderForAScript(script).Exists

        let rec getBackupFileName(fileToBackup: FileInfo) =
            let backupFile = fileToBackup.FullName + ".bak"
            if (File.Exists backupFile) then
                getBackupFileName(FileInfo backupFile)
            else
                backupFile

        let preprocessScriptContents(origScript: FileInfo) (contents: List<string*LineAction>): List<CompilerInput> =
            let binFolder,autogeneratedFile = GetAutoGenerationTargets origScript String.Empty

            if autogeneratedFile.Exists then
                failwithf
                    "fsx needs to copy %s to %s for preprocessing work, but the file already exists"
                    origScript.FullName autogeneratedFile.FullName
            if not binFolder.Exists then
                Directory.CreateDirectory binFolder.FullName |> ignore
            File.Copy(origScript.FullName, autogeneratedFile.FullName, true)

            File.WriteAllText(autogeneratedFile.FullName, String.Empty)

            seq {

                let startCommentInFSharp = "//"
                for line,maybeDep in contents do
                    match maybeDep with
                    | LineAction.Normal ->
                        File.AppendAllText(autogeneratedFile.FullName, line + Environment.NewLine)
                    | LineAction.PreProcessorAction(action) ->
                        File.AppendAllText(autogeneratedFile.FullName, startCommentInFSharp + line + Environment.NewLine)
                        match action with
                        | PreProcessorAction.Skip -> ()
                        | PreProcessorAction.Load(fileName) ->
                            let file = FileInfo(Path.Combine(origScript.Directory.FullName, fileName))
                            yield CompilerInput.SourceFile(file)
                        | PreProcessorAction.Ref(refName) ->
                            let maybeFile = FileInfo(Path.Combine(origScript.Directory.FullName, refName))
                            if maybeFile.Exists then
                                yield CompilerInput.Ref maybeFile.FullName
                            else
                                // must be a BCL lib (e.g. #r "System.Xml.Linq.dll")
                                yield CompilerInput.Ref refName

                let backupFile = FileInfo(getBackupFileName origScript)
                File.Copy(origScript.FullName, backupFile.FullName)
                File.Copy(autogeneratedFile.FullName, origScript.FullName, true)
                autogeneratedFile.Delete()
                yield CompilerInput.Script({ Original = origScript; Backup = backupFile})
            } |> List.ofSeq

        let getSourceFiles(flags: seq<CompilerInput>): seq<FileInfo>=
            seq {
                for f in flags do
                    match f with
                    | CompilerInput.SourceFile file -> yield file
                    | CompilerInput.Script script -> yield script.Original
                    | _ -> ()
            }

        let getCompilerReferences(flags: seq<CompilerInput>): seq<string>=
            seq {
                for flag in flags do
                    match flag with
                    | CompilerInput.Ref refName -> yield sprintf "--reference:%s" refName
                    | _ -> ()
            }

        let restoreBackups(compilerInputs: seq<CompilerInput>) =
            for compilerInput in compilerInputs do
                match compilerInput with
                | CompilerInput.Script script ->
                    // it's sad that File.Move(_,_,bool) overload doesn't exist...
                    File.Copy(script.Backup.FullName, script.Original.FullName, true)
                    script.Backup.Delete()
                | _ -> ()

        if verbose then
            Console.WriteLine(sprintf "Building %s" script.FullName)

        let binFolder = GetBinFolderForAScript script
        let compilerInputs = preprocessScriptContents script contents
        let filesToCompile = getSourceFiles compilerInputs
        let exitCode,exeTarget =
            try
                let _,exeTarget = GetAutoGenerationTargets script "exe"
                let sourceFiles = String.Join(" ", filesToCompile)
                let refs = String.Join (" ", getCompilerReferences compilerInputs)
                let fscompilerflags = (sprintf "%s --warnaserror --target:exe --out:%s %s"
                                               refs exeTarget.FullName sourceFiles)
                let echo =
                    if verbose then
                        Echo.All
                    else
                        Echo.Off
                let processResult = Process.Execute({ Command = "fsharpc"; Arguments = fscompilerflags }, echo)
                if processResult.ExitCode <> 0 then
                    processResult.Output.PrintToConsole()
                processResult.ExitCode,exeTarget

            finally
                restoreBackups compilerInputs

        let success =
            match exitCode with
            | 0 -> true
            | _ -> false

        if not success then
            Console.Error.WriteLine "Build failure"
            BuildResult.Failure({ Dir = binFolder; Created = (not binFolderExistedOriginally) })
        else
            BuildResult.Success({ Exe = exeTarget; BinFolderCreated = (not binFolderExistedOriginally) })

    let GetAlreadyBuiltExecutable (exeTarget: FileInfo)
                                  (binFolder:DirectoryInfo)
                                  (lastWriteTimeOfSourceFile: DateTime)
                                      : Option<FileInfo> =
        if not binFolder.Exists then
            None
        elif binFolder.LastWriteTime < lastWriteTimeOfSourceFile then
            None
        elif not exeTarget.Exists then
            None
        elif exeTarget.LastWriteTime < lastWriteTimeOfSourceFile then
            None
        else
            Some exeTarget

    let Build parsedArgs (generateArtifacts: bool) contents (verbose: bool) =
        let buildResult = BuildFsxScript parsedArgs.Script contents verbose

        match buildResult with
        | Failure binFolder ->
            if binFolder.Created then
                binFolder.Dir.Delete true
            Environment.Exit 1
            failwith "Unreachable"

        | Success exeTarget ->
            if not generateArtifacts then
                if exeTarget.BinFolderCreated then
                    exeTarget.Exe.Directory.Delete true
            exeTarget.Exe

    [<EntryPoint>]
    let main argv =
        if argv.Length = 0 then
            Console.Error.WriteLine "Please pass the .fsx script as an argument"
            PrintUsage()
            Environment.Exit 1

        if argv.Length = 1 && argv.[0] = "--help" then
            PrintUsage()
            Environment.Exit 0

        let parsedArgs =
            try
                ParseArgs argv
            with
            | :? NoScriptProvided ->
                Console.Error.WriteLine "At least one .fsx script is required as input. Use --help for info."
                Environment.Exit 1
                failwith "Unreachable"

        let check,force = parsedArgs.Flags.Contains Flag.OnlyCheck,parsedArgs.Flags.Contains Flag.Force
        let verbose = parsedArgs.Flags.Contains Flag.Verbose

        let scriptContents,lastWriteTimeOfSourceFiles =
            GetParsedContentsAndOldestLastWriteTimeFromScriptOrItsDependencies parsedArgs.Script

        if check || force then
            let generateArtifacts = not check
            Build parsedArgs generateArtifacts scriptContents verbose
                |> ignore
            Environment.Exit 0

        let binFolder,exeTarget = GetAutoGenerationTargets parsedArgs.Script "exe"
        let maybeExe = GetAlreadyBuiltExecutable exeTarget binFolder lastWriteTimeOfSourceFiles
        if maybeExe.IsNone then
            Build parsedArgs true scriptContents verbose
                |> ignore
        elif verbose then
            Console.WriteLine "Up-to-date binary found, skipping compilation"

        0 // return an integer exit code
