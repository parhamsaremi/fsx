namespace FSX.Infrastructure

open System
open System.Linq

open Process
open Misc

module Git =

    let private gitCommand = "git"

    let rec private GetBranchFromGitBranch(outchunks: list<string>) =
        match outchunks with
        | [] ->
            failwith
                "current branch not found, unexpected output from `git branch`"
        | head :: tail ->
            if (head.StartsWith("*")) then
                let branchName = head.Substring("* ".Length)
                branchName
            else
                GetBranchFromGitBranch(tail)

    let private IsGitInstalled() : bool =
        let gitCheckCommand =
            match Misc.GuessPlatform() with
            | Misc.Platform.Windows ->
                {
                    Command = "git"
                    Arguments = "--version"
                }
            | _ ->
                {
                    Command = "which"
                    Arguments = "git"
                }

        let gitCheck = Process.Execute(gitCheckCommand, Echo.Off)
        gitCheck.ExitCode = 0

    let private CheckGitIsInstalled() : unit =
        if not(IsGitInstalled()) then
            Console.Error.WriteLine "Could not continue, install 'git' first"
            Environment.Exit 1

    let GetCurrentBranch() =
        CheckGitIsInstalled()

        let gitBranch =
            Process.Execute(
                {
                    Command = gitCommand
                    Arguments = "branch"
                },
                Echo.Off
            )

        if (gitBranch.ExitCode <> 0) then
            failwith "Unexpected git behaviour, `git branch` didn't succeed"

        let branchesOutput =
            Misc.CrossPlatformStringSplitInLines gitBranch.Output.StdOut

        GetBranchFromGitBranch(branchesOutput)

    let GetLastCommit() =
        CheckGitIsInstalled()

        let gitLogCmd =
            {
                Command = gitCommand
                Arguments =
                    "log --no-color --first-parent -n1 --pretty=format:%h"
            }

        let gitLastCommit = Process.Execute(gitLogCmd, Echo.Off)

        if (gitLastCommit.ExitCode <> 0) then
            gitLastCommit.Output.PrintToConsole()
            failwith "Unexpected git behaviour, as `git log` failed"

        let lines =
            Misc.CrossPlatformStringSplitInLines gitLastCommit.Output.StdOut

        if (lines.Length <> 1) then
            failwith "Unexpected git output for special git log command"

        lines.[0]

    let private random = Random()

    let private GenerateRandomShortNameWithLettersButNoNumbers() : string =
        let chars = "abcdefghijklmnopqrstuvwxyz"

        let randomCharArray =
            Enumerable
                .Repeat(chars, 8)
                .Select(fun str -> str.[random.Next(str.Length)])
                .ToArray()

        String(randomCharArray)

    let private AddRemote (remoteName: string) (remoteUrl: string) =
        let gitRemoteAdd =
            {
                Command = gitCommand
                Arguments = sprintf "remote add %s %s" remoteName remoteUrl
            }

        Process.SafeExecute(gitRemoteAdd, Echo.Off) |> ignore

    let private RemoveRemote(remoteName: string) =
        let gitRemoteRemove =
            {
                Command = gitCommand
                Arguments = sprintf "remote remove %s" remoteName
            }

        Process.SafeExecute(gitRemoteRemove, Echo.Off) |> ignore

    let private GetRemotesInternal() =
        let gitShowRemotes =
            {
                Command = gitCommand
                Arguments = "remote -v"
            }

        Process.SafeExecute(gitShowRemotes, Echo.Off)

    let private FetchAll() =
        let gitFetchAll =
            {
                Command = gitCommand
                Arguments = "fetch --all"
            }

        Process.SafeExecute(gitFetchAll, Echo.Off) |> ignore

    let GetRemotes() =
        let remoteLines = GetRemotesInternal().Output.StdOut |> Misc.TsvParse

        seq {
            for KeyValue(remoteName, remoteUrl) in remoteLines do
                yield (remoteName, remoteUrl)
        }

    let private GetNumberOfCommitsBehindAndAheadFromRemoteBranch
        (repoUrl: string)
        (branchName: string)
        : int * int =
        CheckGitIsInstalled()

        let lastCommit = GetLastCommit()
        let remotes = GetRemotes()

        let maybeRemoteFound =
            Seq.tryFind
                (fun (_, remoteUrl: string) -> remoteUrl.Contains repoUrl)
                remotes

        let remote, cleanRemoteLater =
            match maybeRemoteFound with
            | Some(remoteName, _) -> remoteName, false
            | None ->
                let randomNameForRemoteToBeDeletedLater =
                    GenerateRandomShortNameWithLettersButNoNumbers()

                AddRemote randomNameForRemoteToBeDeletedLater repoUrl
                FetchAll()
                randomNameForRemoteToBeDeletedLater, true

        let gitRevListCmd =
            {
                Command = gitCommand
                Arguments =
                    sprintf
                        "rev-list --left-right --count %s/%s...%s"
                        remote
                        branchName
                        lastCommit
            }

        let gitCommitDivergence = Process.SafeExecute(gitRevListCmd, Echo.Off)

        let numbers =
            gitCommitDivergence.Output.StdOut.Split(
                [| "\t" |],
                StringSplitOptions.RemoveEmptyEntries
            )

        let expectedNumberOfNumbers = 2

        if (numbers.Length <> expectedNumberOfNumbers) then
            failwith(
                sprintf
                    "Unexpected git output for special `git rev-list` command, got %d numbers instead of %d"
                    numbers.Length
                    expectedNumberOfNumbers
            )

        let behind = Int32.Parse(numbers.[0])
        let ahead = Int32.Parse(numbers.[1])

        if cleanRemoteLater then
            RemoveRemote remote

        behind, ahead

    let GetNumberOfCommitsAhead repo branch : int =
        GetNumberOfCommitsBehindAndAheadFromRemoteBranch repo branch |> snd

    let GetNumberOfCommitsBehind repo branch : int =
        GetNumberOfCommitsBehindAndAheadFromRemoteBranch repo branch |> fst

    // 0 == last commit, 1 == second to last, and so on...
    let GetCommitMessageOfLastCommitNumber(number: int) : string =
        if (number < 0) then
            failwith "Expected number param to be non-negative"

        CheckGitIsInstalled()

        let gitLogCmd =
            {
                Command = gitCommand
                Arguments =
                    String.Format(
                        "log --skip={0} -1 --pretty=format:%b",
                        number
                    )
            }

        let gitLastNCommit = Process.SafeExecute(gitLogCmd, Echo.Off)
        gitLastNCommit.Output.StdOut


    let GetCommitMessagesOfCommitsInThisBranchNotPresentInRemoteBranch
        repo
        branch
        : seq<string> =
        seq {
            for i = 0 to (GetNumberOfCommitsAhead repo branch) - 1 do
                yield GetCommitMessageOfLastCommitNumber i
        }

    let GetRepoInfo() =
        if not(IsGitInstalled()) then
            String.Empty
        else
            let gitLog =
                Process.Execute(
                    {
                        Command = "git"
                        Arguments = "log --oneline"
                    },
                    Echo.Off
                )

            if gitLog.ExitCode <> 0 then
                String.Empty
            else
                let branch = GetCurrentBranch()

                let gitLogCmd =
                    {
                        Command = "git"
                        Arguments =
                            "log --no-color --first-parent -n1 --pretty=format:%h"
                    }

                let gitLastCommit = Process.Execute(gitLogCmd, Echo.Off)

                if gitLastCommit.ExitCode <> 0 then
                    failwith
                        "Unexpected git behaviour, as `git log` succeeded before but not now"

                let lines =
                    Misc.CrossPlatformStringSplitInLines
                        gitLastCommit.Output.StdOut

                if lines.Length <> 1 then
                    failwith "Unexpected git output for special git log command"
                else
                    let lastCommitSingleOutput = lines.[0]
                    sprintf "(%s/%s)" branch lastCommitSingleOutput
