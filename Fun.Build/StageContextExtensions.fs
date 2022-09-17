﻿[<AutoOpen>]
module Fun.Build.StageContextExtensions

open System
open System.Text
open System.Diagnostics
open Spectre.Console


let private parallelStepLock = obj ()


type StageContext with

    static member Create(name: string) = {
        Name = name
        IsActive = fun _ -> true
        IsParallel = false
        Timeout = ValueNone
        TimeoutForStep = ValueNone
        WorkingDir = ValueNone
        EnvVars = Map.empty
        ParentContext = ValueNone
        Steps = []
    }


    member ctx.GetNamePath() =
        ctx.ParentContext
        |> ValueOption.map (
            function
            | StageParent.Stage x -> x.GetNamePath() + "/"
            | StageParent.Pipeline _ -> ""
        )
        |> ValueOption.defaultValue ""
        |> fun x -> x + ctx.Name


    member ctx.GetWorkingDir() =
        ctx.WorkingDir
        |> ValueOption.defaultWithVOption (fun _ ->
            ctx.ParentContext
            |> ValueOption.bind (
                function
                | StageParent.Stage x -> x.GetWorkingDir()
                | StageParent.Pipeline x -> x.WorkingDir
            )
        )

    member ctx.GetTimeoutForStage() =
        ctx.Timeout
        |> ValueOption.map (fun x -> int x.TotalMilliseconds)
        |> ValueOption.defaultWithVOption (fun _ ->
            ctx.ParentContext
            |> ValueOption.bind (
                function
                | StageParent.Stage x -> x.GetTimeoutForStage() |> ValueSome
                | StageParent.Pipeline x -> x.TimeoutForStage |> ValueOption.map (fun x -> int x.TotalMilliseconds)
            )
        )
        |> ValueOption.defaultValue -1

    member ctx.GetTimeoutForStep() =
        ctx.TimeoutForStep
        |> ValueOption.map (fun x -> int x.TotalMilliseconds)
        |> ValueOption.defaultWithVOption (fun _ ->
            ctx.ParentContext
            |> ValueOption.bind (
                function
                | StageParent.Stage x -> x.GetTimeoutForStep() |> ValueSome
                | StageParent.Pipeline x -> x.TimeoutForStep |> ValueOption.map (fun x -> int x.TotalMilliseconds)
            )
        )
        |> ValueOption.defaultValue -1


    member ctx.BuildEnvVars() =
        let vars = Collections.Generic.Dictionary()

        ctx.ParentContext
        |> ValueOption.map (
            function
            | StageParent.Stage x -> x.BuildEnvVars()
            | StageParent.Pipeline x -> x.EnvVars
        )
        |> ValueOption.iter (fun kvs ->
            for KeyValue (k, v) in kvs do
                vars[k] <- v
        )

        for KeyValue (k, v) in ctx.EnvVars do
            vars[k] <- v

        vars |> Seq.map (fun (KeyValue (k, v)) -> k, v) |> Map.ofSeq


    member ctx.TryGetEnvVar(key: string) =
        ctx.EnvVars
        |> Map.tryFind key
        |> ValueOption.ofOption
        |> ValueOption.defaultWithVOption (fun _ ->
            ctx.ParentContext
            |> ValueOption.map (
                function
                | StageParent.Stage x -> x.TryGetEnvVar key
                | StageParent.Pipeline x -> x.EnvVars |> Map.tryFind key |> ValueOption.ofOption
            )
            |> ValueOption.defaultValue ValueNone
        )

    // If not find then return ""
    member inline ctx.GetEnvVar(key: string) = ctx.TryGetEnvVar key |> ValueOption.defaultValue ""


    member ctx.TryGetCmdArg(key: string) =
        ctx.ParentContext
        |> ValueOption.bind (
            function
            | StageParent.Stage x -> x.TryGetCmdArg key
            | StageParent.Pipeline x ->
                match x.CmdArgs |> List.tryFindIndex ((=) key) with
                | Some index ->
                    if List.length x.CmdArgs > index + 1 then
                        ValueSome x.CmdArgs[index + 1]
                    else
                        ValueSome ""
                | _ -> ValueNone
        )

    member inline ctx.GetCmdArg(key) = ctx.TryGetCmdArg key |> ValueOption.defaultValue ""


    member inline ctx.TryGetCmdArgOrEnvVar(key: string) =
        match ctx.TryGetCmdArg(key) with
        | ValueSome x -> ValueSome x
        | _ -> ctx.TryGetEnvVar(key)

    member inline ctx.GetCmdArgOrEnvVar(key) = ctx.TryGetCmdArgOrEnvVar key |> ValueOption.defaultValue ""


    member ctx.BuildCommand(commandStr: string) =
        let index = commandStr.IndexOf " "

        let cmd, args =
            if index > 0 then
                let cmd = commandStr.Substring(0, index)
                let args = commandStr.Substring(index + 1)
                cmd, args
            else
                commandStr, ""

        let command = ProcessStartInfo(cmd, args)


        ctx.GetWorkingDir() |> ValueOption.iter (fun x -> command.WorkingDirectory <- x)

        ctx.BuildEnvVars() |> Map.iter (fun k v -> command.Environment[ k ] <- v)

        command.StandardOutputEncoding <- Encoding.UTF8
        command.RedirectStandardOutput <- true
        command


    member ctx.AddCommandStep(commandStrFn: StageContext -> Async<string>) =
        { ctx with
            Steps =
                ctx.Steps
                @ [
                    StepFn(fun ctx -> async {
                        let! commandStr = commandStrFn ctx
                        let command = ctx.BuildCommand(commandStr)

                        AnsiConsole.MarkupLine $"[green]{commandStr}[/]"

                        use result = Process.Start command

                        use! cd =
                            Async.OnCancel(fun _ ->
                                AnsiConsole.MarkupLine $"[yellow]{commandStr}[/] is cancelled or timeouted and the process will be killed."
                                result.Kill()
                            )

                        result.WaitForExit()
                        return result.ExitCode
                    }
                    )
                ]
        }


    member ctx.WhenEnvArg(envKey: string, envValue: string) =
        match ctx.TryGetEnvVar envKey with
        | ValueSome v when envValue = "" || v = envValue -> true
        | _ -> false

    member ctx.WhenCmdArg(argKey: string, argValue: string) =
        match ctx.TryGetCmdArg argKey with
        | ValueSome v when argValue = "" || v = argValue -> true
        | _ -> false


    member ctx.WhenBranch(branch: string) =
        try
            let command = ctx.BuildCommand("git branch --show-current")
            ctx.GetWorkingDir() |> ValueOption.iter (fun x -> command.WorkingDirectory <- x)

            let result = Process.Start command
            result.WaitForExit()
            result.StandardOutput.ReadLine() = branch
        with ex ->
            AnsiConsole.MarkupLine $"[red]Run git to get branch info failed: {ex.Message}[/]"
            false


    /// Run the stage. If index is not provided then it will be treated as sub-stage.
    member stage.Run(index: int voption, cancelToken: Threading.CancellationToken) =
        let mutable exitCode = 0

        let isActive = stage.IsActive stage
        let namePath = stage.GetNamePath()

        if isActive then
            let stageSW = Stopwatch.StartNew()
            let isParallel = stage.IsParallel
            let timeoutForStep = stage.GetTimeoutForStep()
            let timeoutForStage = stage.GetTimeoutForStage()

            use cts = new Threading.CancellationTokenSource(timeoutForStage)
            use linkedCTS = Threading.CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancelToken)

            use stepCTS = new Threading.CancellationTokenSource(timeoutForStep)
            use linkedStepCTS = Threading.CancellationTokenSource.CreateLinkedTokenSource(stepCTS.Token, linkedCTS.Token)


            AnsiConsole.Write(Rule())
            AnsiConsole.Write(
                match index with
                | ValueSome i ->
                    Rule($"STAGE #{i} [bold teal]{namePath}[/] started. Stage timeout: {timeoutForStage}ms. Step timeout: {timeoutForStep}ms.")
                        .LeftAligned()
                | _ -> Rule($"SUB-STAGE {namePath}. Stage timeout: {timeoutForStage}ms. Step timeout: {timeoutForStep}ms.").LeftAligned()
            )
            AnsiConsole.WriteLine()


            let steps =
                stage.Steps
                |> Seq.map (fun step -> async {
                    let sw = Stopwatch.StartNew()
                    AnsiConsole.MarkupLine $"""[grey]> step started{if isParallel then " in parallel -->" else ":"}[/]"""
                    let! result =
                        match step with
                        | StepFn fn -> fn stage
                        | StepOfStage subStage -> async {
                            let subStage =
                                { subStage with
                                    ParentContext = ValueSome(StageParent.Stage stage)
                                }
                            return subStage.Run(ValueNone, linkedStepCTS.Token)
                          }

                    AnsiConsole.MarkupLine $"""[gray]> step finished{if isParallel then " in parallel." else "."} {sw.ElapsedMilliseconds}ms.[/]"""
                    AnsiConsole.WriteLine()
                    if result <> 0 then
                        failwith $"Step finished without a success exist code. {result}"
                }
                )

            try
                let ts =
                    if isParallel then
                        async {
                            let mutable count = 0

                            for ts in steps do
                                Async.Start(
                                    async {
                                        do! ts
                                        // TODO should find a better way to do parallel and pass cancellation token down
                                        lock parallelStepLock (fun _ -> count <- count + 1)
                                    },
                                    linkedStepCTS.Token
                                )

                            while count < Seq.length steps do
                                do! Async.Sleep 10
                        }
                    else
                        async {
                            for step in steps do
                                let! completer = Async.StartChild(step, timeoutForStep)
                                do! completer
                        }

                Async.RunSynchronously(ts, cancellationToken = linkedCTS.Token)

            with ex ->
                exitCode <- -1
                if linkedCTS.Token.IsCancellationRequested then
                    AnsiConsole.MarkupLine $"[yellow]Stage is cancelled or timeouted.[/]"
                    AnsiConsole.WriteLine()
                else
                    AnsiConsole.MarkupLine $"[red]> step failed: {ex.Message}[/]"
                    AnsiConsole.WriteException ex
                    AnsiConsole.WriteLine()

            AnsiConsole.Write(
                match index with
                | ValueSome i ->
                    Rule($"""STAGE #{i} [bold {if exitCode <> 0 then "red" else "teal"}]{namePath}[/] finished. {stageSW.ElapsedMilliseconds}ms.""")
                        .LeftAligned()
                | _ ->
                    Rule($"""SUB-STAGE [bold {if exitCode <> 0 then "red" else "teal"}]{namePath}[/] finished. {stageSW.ElapsedMilliseconds}ms.""")
                        .LeftAligned()
            )
            AnsiConsole.Write(Rule())

        else
            AnsiConsole.Write(Rule())
            AnsiConsole.MarkupLine(
                match index with
                | ValueSome i -> $"STAGE #{i} [bold grey]{namePath}[/] is inactive"
                | _ -> $"SUB-STAGE [bold grey]{namePath}[/] is inactive"
            )
            AnsiConsole.Write(Rule())

        exitCode
