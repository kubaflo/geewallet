#!/usr/bin/env fsharpi

open System
open System.IO
#load "Infra.fs"
open FSX.Infrastructure

let DEFAULT_FRONTEND = "GWallet.Frontend.Console"

let rec private GatherTarget (args: string list, targetSet: Option<string>): Option<string> =
    match args with
    | [] -> targetSet
    | head::tail ->
        if (targetSet.IsSome) then
            failwith "only one target can be passed to make"
        GatherTarget (tail, Some (head))

let GatherPrefix(): string =
    let buildConfig = FileInfo (Path.Combine (__SOURCE_DIRECTORY__, "build.config"))
    if not (buildConfig.Exists) then
        Console.Error.WriteLine "ERROR: configure hasn't been run yet, run ./configure.sh first"
        Environment.Exit 1
    let buildConfigContents = File.ReadAllText buildConfig.FullName
    (buildConfigContents.Substring ("Prefix=".Length)).Trim()

let prefix = GatherPrefix ()
let libInstallPath = DirectoryInfo (Path.Combine (prefix, "lib", "gwallet"))
let binInstallPath = DirectoryInfo (Path.Combine (prefix, "bin"))

let launcherScriptPath = FileInfo (Path.Combine (__SOURCE_DIRECTORY__, "bin", "gwallet"))
let mainBinariesPath = DirectoryInfo (Path.Combine(__SOURCE_DIRECTORY__,
                                                   "src", DEFAULT_FRONTEND, "bin", "Debug"))

let wrapperScript = """#!/bin/sh
set -e
exec mono "$TARGET_DIR/$GWALLET_PROJECT.exe" "$@"
"""

let JustBuild () =
    Console.WriteLine "Gathering gwallet dependencies..."
    let nuget = Process.Execute ("nuget restore", true, false)
    if (nuget.ExitCode <> 0) then
        Environment.Exit 1

    Console.WriteLine "Compiling gwallet..."
    let xbuild = Process.Execute ("xbuild", true, false)
    if (xbuild.ExitCode <> 0) then
        Environment.Exit 1

    Directory.CreateDirectory(launcherScriptPath.Directory.FullName) |> ignore
    let wrapperScriptWithPaths =
        wrapperScript.Replace("$TARGET_DIR", libInstallPath.FullName)
                     .Replace("$GWALLET_PROJECT", DEFAULT_FRONTEND)
    File.WriteAllText (launcherScriptPath.FullName, wrapperScriptWithPaths)

let maybeTarget = GatherTarget (Util.FsxArguments(), None)
match maybeTarget with
| None -> JustBuild ()
| Some("check") ->
    Console.WriteLine "Running tests..."
    Console.WriteLine ()

    let nunitCommand = "nunit-console"
    let nunitWhich = Process.Execute(sprintf "which %s" nunitCommand, false, true)
    if (nunitWhich.ExitCode <> 0) then
        Console.Error.WriteLine (sprintf "%s not found, please install it first" nunitCommand)
        Environment.Exit 1
    let nunitRun = Process.Execute(sprintf "%s src/GWallet.Backend.Tests/bin/GWallet.Backend.Tests.dll" nunitCommand, true, false)
    if (nunitWhich.ExitCode <> 0) then
        Console.Error.WriteLine "Tests failed"
        Environment.Exit 1

| Some("install") ->
    Console.WriteLine "Installing gwallet..."
    Console.WriteLine ()
    Directory.CreateDirectory(libInstallPath.FullName) |> ignore
    Misc.CopyDirectoryRecursively (mainBinariesPath, libInstallPath)

    let finalPrefixPathOfWrapperScript = FileInfo (Path.Combine(binInstallPath.FullName, launcherScriptPath.Name))
    if not (Directory.Exists(finalPrefixPathOfWrapperScript.Directory.FullName)) then
        Directory.CreateDirectory(finalPrefixPathOfWrapperScript.Directory.FullName) |> ignore
    File.Copy(launcherScriptPath.FullName, finalPrefixPathOfWrapperScript.FullName, true)
    if ((Process.Execute(sprintf "chmod ugo+x %s" finalPrefixPathOfWrapperScript.FullName, false, true)).ExitCode <> 0) then
        failwith "Unexpected chmod failure, please report this bug"
| Some(someOtherTarget) ->
    Console.Error.WriteLine("Unrecognized target: " + someOtherTarget)
    Environment.Exit 2
